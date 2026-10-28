using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Transcribe
{
    public sealed class SpeakerEmbeddingModel : IDisposable
    {
        private InferenceSession session;
        private readonly string modelPath;
        private bool usingCoreML;
        private readonly LogMelExtractor melExtractor;
        private readonly SpeakerEmbeddingModelConfig config;
        private readonly string inputName;
        private readonly string outputName;
        private readonly object runLock = new();
        private readonly IReadOnlyList<ExtraInputSpec> extraInputs;
        private readonly Action<string>? log;

        public SpeakerEmbeddingModel(string modelPath, SpeakerEmbeddingModelConfig config, Action<string>? log = null, string? executionProvider = null)
        {
            this.log = log;
            this.modelPath = modelPath;
            var provider = (executionProvider ?? Environment.GetEnvironmentVariable("TX_DIARIZATION_PROVIDER") ?? "auto").ToLowerInvariant();
            if (provider is not ("auto" or "cpu" or "coreml"))
                throw new ArgumentException("Diarization provider must be auto, cpu, or coreml.", nameof(executionProvider));
            usingCoreML = provider != "cpu" && OperatingSystem.IsMacOSVersionAtLeast(12)
                && OrtEnv.Instance().GetAvailableProviders().Contains("CoreMLExecutionProvider");
            if (provider == "coreml" && !usingCoreML)
                log?.Invoke("CoreML is unavailable; using CPU for speaker identification.");
            try
            {
                session = CreateSession(usingCoreML);
            }
            catch (Exception ex) when (usingCoreML && ex is OnnxRuntimeException or NotSupportedException)
            {
                log?.Invoke($"CoreML initialization failed; using CPU: {ex.Message}");
                usingCoreML = false;
                session = CreateSession(false);
            }
            log?.Invoke($"Diarization execution provider: {(usingCoreML ? "CoreML (unsupported operations use CPU)" : "CPU")}");

            inputName = !string.IsNullOrWhiteSpace(config.InputName)
                ? config.InputName
                : session.InputMetadata.Keys.First();
            outputName = !string.IsNullOrWhiteSpace(config.OutputName)
                ? config.OutputName
                : session.OutputMetadata.Keys.First();

            var inputDims = session.InputMetadata[inputName].Dimensions.Select(d => (long)d).ToArray();
            this.config = ResolveConfig(config, inputDims, log);
            melExtractor = new LogMelExtractor(this.config);
            extraInputs = ResolveExtraInputs(session.InputMetadata, inputName, log);

            log?.Invoke($"Diarization model loaded: {Path.GetFileName(modelPath)}");
            log?.Invoke($"Diarization model input: {inputName}, output: {outputName}");
            log?.Invoke($"Diarization model input dims: {FormatDims(inputDims)}, layout: {this.config.InputLayout}");
        }

        public SpeakerEmbeddingModelConfig Config => config;

        public float[] ComputeEmbedding(float[] samples)
        {
            if (!string.Equals(config.InputType, "logmel", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported diarization input type: {config.InputType}");
            }

            var features = melExtractor.Extract(samples, out int frameCount);
            if (frameCount == 0)
            {
                return Array.Empty<float>();
            }

            var tensor = CreateInputTensor(features, frameCount);
            var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
            var inputs = new List<NamedOnnxValue> { input };
            foreach (var extra in extraInputs)
            {
                inputs.Add(CreateExtraInput(extra, frameCount));
            }

            lock (runLock)
            {
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
                try
                {
                    results = session.Run(inputs);
                }
                catch (OnnxRuntimeException ex) when (usingCoreML)
                {
                    log?.Invoke($"CoreML inference failed; retrying on CPU: {ex.Message}");
                    session.Dispose();
                    usingCoreML = false;
                    session = CreateSession(false);
                    results = session.Run(inputs);
                }
                using (results)
                {
                    var output = results.First(r => string.Equals(r.Name, outputName, StringComparison.OrdinalIgnoreCase))
                        .AsEnumerable<float>()
                        .ToArray();
                    return Normalize(output);
                }
            }
        }

        private InferenceSession CreateSession(bool coreML)
        {
            using var options = new SessionOptions();
            if (coreML)
            {
                // ONNX Runtime 1.20's C# generic provider API does not support CoreML.
                options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM);
            }
            return new InferenceSession(modelPath, options);
        }

        private DenseTensor<float> CreateInputTensor(float[] features, int frameCount)
        {
            var melBins = config.MelBins;
            if (string.Equals(config.InputLayout, "BCT", StringComparison.OrdinalIgnoreCase))
            {
                var tensor = new DenseTensor<float>(new[] { 1, melBins, frameCount });
                int index = 0;
                for (int t = 0; t < frameCount; t++)
                {
                    for (int m = 0; m < melBins; m++)
                    {
                        tensor[0, m, t] = features[index++];
                    }
                }
                return tensor;
            }

            if (string.Equals(config.InputLayout, "BTC", StringComparison.OrdinalIgnoreCase))
            {
                var tensor = new DenseTensor<float>(new[] { 1, frameCount, melBins });
                int index = 0;
                for (int t = 0; t < frameCount; t++)
                {
                    for (int m = 0; m < melBins; m++)
                    {
                        tensor[0, t, m] = features[index++];
                    }
                }
                return tensor;
            }

            if (string.Equals(config.InputLayout, "BCHT", StringComparison.OrdinalIgnoreCase))
            {
                var tensor = new DenseTensor<float>(new[] { 1, 1, melBins, frameCount });
                int index = 0;
                for (int t = 0; t < frameCount; t++)
                {
                    for (int m = 0; m < melBins; m++)
                    {
                        tensor[0, 0, m, t] = features[index++];
                    }
                }
                return tensor;
            }

            throw new InvalidOperationException($"Unsupported diarization input layout: {config.InputLayout}");
        }

        private static float[] Normalize(float[] vector)
        {
            if (vector.Length == 0)
            {
                return vector;
            }

            float sum = 0f;
            for (int i = 0; i < vector.Length; i++)
            {
                sum += vector[i] * vector[i];
            }

            if (sum <= 0)
            {
                return vector;
            }

            float inv = 1f / MathF.Sqrt(sum);
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] *= inv;
            }

            return vector;
        }

        public void Dispose()
        {
            session.Dispose();
        }

        private static SpeakerEmbeddingModelConfig ResolveConfig(
            SpeakerEmbeddingModelConfig source,
            long[] inputDims,
            Action<string>? log)
        {
            var resolved = new SpeakerEmbeddingModelConfig
            {
                InputName = source.InputName,
                OutputName = source.OutputName,
                InputLayout = source.InputLayout,
                InputType = source.InputType,
                SampleRate = source.SampleRate,
                MelBins = source.MelBins,
                FrameMs = source.FrameMs,
                HopMs = source.HopMs,
                ApplyPreEmphasis = source.ApplyPreEmphasis,
                PreEmphasis = source.PreEmphasis,
                ApplyMeanNormalization = source.ApplyMeanNormalization,
                ApplyVarianceNormalization = source.ApplyVarianceNormalization
            };

            var layout = ResolveInputLayout(resolved.InputLayout, inputDims, resolved.MelBins, log);
            resolved.InputLayout = layout;

            if (layout.Equals("BCT", StringComparison.OrdinalIgnoreCase) && inputDims.Length > 1 && inputDims[1] > 0)
            {
                var dimsBins = (int)inputDims[1];
                if (dimsBins != resolved.MelBins)
                {
                    log?.Invoke($"Adjusting mel bins to match model: {dimsBins}");
                    resolved.MelBins = dimsBins;
                }
            }
            else if (layout.Equals("BTC", StringComparison.OrdinalIgnoreCase) && inputDims.Length > 2 && inputDims[2] > 0)
            {
                var dimsBins = (int)inputDims[2];
                if (dimsBins != resolved.MelBins)
                {
                    log?.Invoke($"Adjusting mel bins to match model: {dimsBins}");
                    resolved.MelBins = dimsBins;
                }
            }
            else if (layout.Equals("BCHT", StringComparison.OrdinalIgnoreCase) && inputDims.Length > 2 && inputDims[2] > 0)
            {
                var dimsBins = (int)inputDims[2];
                if (dimsBins != resolved.MelBins)
                {
                    log?.Invoke($"Adjusting mel bins to match model: {dimsBins}");
                    resolved.MelBins = dimsBins;
                }
            }

            return resolved;
        }

        private static string ResolveInputLayout(string? layout, long[] inputDims, int melBins, Action<string>? log)
        {
            if (!string.IsNullOrWhiteSpace(layout) && !layout.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }

            if (inputDims.Length >= 4 && inputDims[1] == 1 && inputDims[2] == melBins)
            {
                return "BCHT";
            }

            if (inputDims.Length >= 3)
            {
                if (inputDims[2] == melBins)
                {
                    return "BTC";
                }

                if (inputDims[1] == melBins)
                {
                    return "BCT";
                }

                if (inputDims[1] > 0 && inputDims[2] <= 0)
                {
                    return "BCT";
                }

                if (inputDims[2] > 0 && inputDims[1] <= 0)
                {
                    return "BTC";
                }
            }

            log?.Invoke("Diarization input layout auto-detection failed; defaulting to BTC.");
            return "BTC";
        }

        private static string FormatDims(long[] dims)
        {
            if (dims.Length == 0)
            {
                return "(none)";
            }

            return string.Join("x", dims.Select(d => d <= 0 ? "?" : d.ToString()));
        }

        private static IReadOnlyList<ExtraInputSpec> ResolveExtraInputs(
            IReadOnlyDictionary<string, NodeMetadata> metadata,
            string primaryInput,
            Action<string>? log)
        {
            var extras = new List<ExtraInputSpec>();
            var unknown = new List<string>();

            foreach (var kvp in metadata)
            {
                var name = kvp.Key;
                if (string.Equals(name, primaryInput, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsLengthInput(name))
                {
                    extras.Add(new ExtraInputSpec(name, kvp.Value.ElementType, kvp.Value.Dimensions.ToArray(), ExtraInputKind.Length));
                    continue;
                }

                unknown.Add($"{name} ({kvp.Value.ElementType}, {FormatDims(kvp.Value.Dimensions.Select(d => (long)d).ToArray())})");
            }

            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"Unsupported diarization inputs: {string.Join(", ", unknown)}");
            }

            if (extras.Count > 0)
            {
                log?.Invoke($"Diarization extra inputs: {string.Join(", ", extras.Select(e => e.Name))}");
            }

            return extras;
        }

        private static bool IsLengthInput(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalized = name.Replace("_", "").ToLowerInvariant();
            return normalized.Contains("length");
        }

        private NamedOnnxValue CreateExtraInput(ExtraInputSpec spec, int frameCount)
        {
            return spec.Kind switch
            {
                ExtraInputKind.Length => CreateLengthInput(spec, frameCount),
                _ => throw new InvalidOperationException($"Unsupported diarization input: {spec.Name}")
            };
        }

        private static NamedOnnxValue CreateLengthInput(ExtraInputSpec spec, int frameCount)
        {
            var dims = NormalizeDims(spec.Dimensions);

            if (spec.ElementType == typeof(long))
            {
                return NamedOnnxValue.CreateFromTensor(spec.Name, CreateLengthTensorLong(dims, frameCount));
            }

            if (spec.ElementType == typeof(int))
            {
                return NamedOnnxValue.CreateFromTensor(spec.Name, CreateLengthTensorInt(dims, frameCount));
            }

            if (spec.ElementType == typeof(float))
            {
                return NamedOnnxValue.CreateFromTensor(spec.Name, CreateLengthTensorFloat(dims, frameCount));
            }

            throw new InvalidOperationException($"Unsupported length input type: {spec.ElementType}");
        }

        private static int[] NormalizeDims(IReadOnlyList<int> dims)
        {
            if (dims.Count == 0)
            {
                return new[] { 1 };
            }

            var normalized = new int[dims.Count];
            for (int i = 0; i < dims.Count; i++)
            {
                normalized[i] = dims[i] > 0 ? dims[i] : 1;
            }

            return normalized;
        }

        private static DenseTensor<long> CreateLengthTensorLong(int[] dims, int value)
        {
            var tensor = new DenseTensor<long>(dims);
            var span = tensor.Buffer.Span;
            var length = span.Length;
            for (int i = 0; i < length; i++)
            {
                span[i] = value;
            }
            return tensor;
        }

        private static DenseTensor<int> CreateLengthTensorInt(int[] dims, int value)
        {
            var tensor = new DenseTensor<int>(dims);
            var span = tensor.Buffer.Span;
            var length = span.Length;
            for (int i = 0; i < length; i++)
            {
                span[i] = value;
            }
            return tensor;
        }

        private static DenseTensor<float> CreateLengthTensorFloat(int[] dims, int value)
        {
            var tensor = new DenseTensor<float>(dims);
            var span = tensor.Buffer.Span;
            var length = span.Length;
            var valueFloat = (float)value;
            for (int i = 0; i < length; i++)
            {
                span[i] = valueFloat;
            }
            return tensor;
        }

        private sealed record ExtraInputSpec(string Name, Type ElementType, int[] Dimensions, ExtraInputKind Kind);

        private enum ExtraInputKind
        {
            Length
        }
    }
}
