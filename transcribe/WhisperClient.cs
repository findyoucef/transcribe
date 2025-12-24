using Whisper.net.Ggml;
using Whisper.net;

namespace Transcribe
{
    public class WhisperClient
    {
        public static async Task<string> EnsureModelDownloaded(string model, Action<string>? log = null)
        {
            // Determine ggml type and model file name from the provided model option
            GgmlType ggmlType;
            string modelFileName;
            var normalizedModel = string.IsNullOrWhiteSpace(model) ? TranscribeDefaults.DefaultModel : model.Trim();
            switch (normalizedModel)
            {
                case "LargeV3Turbo":
                    ggmlType = GgmlType.LargeV3Turbo;
                    modelFileName = "ggml-large-v3-turbo.bin";
                    break;
                case "BaseEn":
                    ggmlType = GgmlType.BaseEn;
                    modelFileName = "ggml-base.en.bin";
                    break;
                case "TinyEn":
                    ggmlType = GgmlType.TinyEn;
                    modelFileName = "ggml-tiny.en.bin";
                    break;
                case "LargeV3":
                default:
                    ggmlType = GgmlType.LargeV3;
                    modelFileName = "ggml-large-v3.bin";
                    break;
            }

            // Resolve model path to the program's binary directory
            var baseDir = AppContext.BaseDirectory;
            var modelPath = Path.Combine(baseDir, modelFileName);

            // Download model if missing
            if (!File.Exists(modelPath))
            {
                await DownloadModel(modelPath, ggmlType, log);
            }

            return modelPath;
        }

        public static async Task TranscribeAudioAsync(
            string wavFileName,
            IProgress<int>? progress = null,
            string model = TranscribeDefaults.DefaultModel,
            string language = TranscribeDefaults.DefaultLanguage,
            string? outputDirectory = null,
            Action<string>? log = null,
            Action<string>? onSegment = null,
            DiarizationOptions? diarizationOptions = null)
        {
            var baseName = Path.GetFileNameWithoutExtension(wavFileName);
            var resolvedOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Directory.GetCurrentDirectory()
                : outputDirectory;

            Directory.CreateDirectory(resolvedOutputDirectory);

            var txtFilePath = Path.Combine(resolvedOutputDirectory, $"{baseName}.txt");
            if (File.Exists(txtFilePath))
            {
                throw new InvalidOperationException($"Transcript already exists: {txtFilePath}");
            }

            var modelPath = await EnsureModelDownloaded(model, log);

            // Create factory from selected model in the binary directory
            using var whisperFactory = WhisperFactory.FromPath(modelPath);

            // Build processor with selected language
            var resolvedLanguage = TranscribeDefaults.NormalizeLanguage(language);
            using var processor = whisperFactory.CreateBuilder()
                .WithLanguage(resolvedLanguage)
                .Build();

            // Read WAV duration to drive progress
            long totalDurationMs = GetWavDurationMilliseconds(wavFileName);
            int lastPercent = -1;

            using var fileStream = File.OpenRead(wavFileName);

            using var outputFile = new StreamWriter(txtFilePath);

            const int diarizationProgressSpan = 15;
            var transcriptionProgressStart = 0;
            DiarizationResult? diarization = null;
            if (diarizationOptions?.Enabled == true)
            {
                diarization = await BuildDiarizationResultAsync(
                    wavFileName,
                    diarizationOptions,
                    log,
                    progress,
                    0,
                    diarizationProgressSpan);
                transcriptionProgressStart = diarizationProgressSpan;
            }

            await foreach (var result in processor.ProcessAsync(fileStream))
            {
                var trimmed = result.Text.Trim();
                var speakerLabel = diarization?.GetStableSpeakerLabel(result.Start, result.End);
                var taggedText = TranscriptionTagger.ApplyTags(trimmed, null, speakerLabel);
                var line = $"{result.Start}->{result.End}: {taggedText}";
                await outputFile.WriteLineAsync(line);
                onSegment?.Invoke(line);

                if (progress != null && totalDurationMs > 0)
                {
                    var percent = (int)Math.Clamp((result.End.TotalMilliseconds * 100.0) / totalDurationMs, 0, 100);
                    if (percent > lastPercent && percent < 100)
                    {
                        lastPercent = percent;
                        progress.Report(MapProgress(percent, transcriptionProgressStart, 100));
                    }
                }
                else if (progress != null && totalDurationMs <= 0 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    lastPercent = Math.Min(99, lastPercent + 1);
                    progress.Report(MapProgress(lastPercent, transcriptionProgressStart, 100));
                }
            }

            await processor.DisposeAsync();

            if (progress != null)
            {
                progress.Report(100);
            }
        }

        private static async Task<DiarizationResult?> BuildDiarizationResultAsync(
            string wavFileName,
            DiarizationOptions options,
            Action<string>? log,
            IProgress<int>? progress,
            int progressStart,
            int progressEnd)
        {
            log?.Invoke("Running diarization...");

            var resources = await DiarizationModelLoader.EnsureResourcesAsync(options, log);
            using var model = new SpeakerEmbeddingModel(resources.ModelPath, resources.Config, log);
            var session = new DiarizationSession(model, options, log);

            progress?.Report(progressStart);

            var samples = AudioUtils.ReadAudioFileAsMono(wavFileName, resources.Config.SampleRate);
            if (samples.Length == 0)
            {
                log?.Invoke("Diarization skipped: no audio samples.");
                progress?.Report(progressEnd);
                return new DiarizationResult(session.Timeline, session.MergeableSegmentDuration);
            }

            var sampleRate = resources.Config.SampleRate;
            var totalSamples = samples.Length;
            var chunkSamples = Math.Max(sampleRate, sampleRate * TranscribeDefaults.DefaultChunkSeconds);
            for (int offset = 0; offset < totalSamples; offset += chunkSamples)
            {
                var count = Math.Min(chunkSamples, totalSamples - offset);
                var chunk = new float[count];
                Array.Copy(samples, offset, chunk, 0, count);
                session.ProcessAudioChunk(chunk, TimeSpan.FromSeconds(offset / (double)sampleRate));

                if (progress != null)
                {
                    var raw = (int)Math.Clamp(((offset + count) * 100.0) / totalSamples, 0, 100);
                    progress.Report(MapProgress(raw, progressStart, progressEnd));
                }
            }
            session.Complete();

            log?.Invoke("Diarization complete.");
            progress?.Report(progressEnd);

            return new DiarizationResult(session.Timeline, session.MergeableSegmentDuration);
        }

        private static long GetWavDurationMilliseconds(string wavPath)
        {
            // Basic WAV header parse: ByteRate at offset 28 (4 bytes, LE), Data chunk size at offset 40 (4 bytes, LE)
            // This assumes standard PCM with fixed header layout.
            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(wavPath);
                var totalMs = reader.TotalTime.TotalMilliseconds;
                if (totalMs > 0)
                {
                    return (long)totalMs;
                }
            }
            catch
            {
                // Fall back to manual header parsing.
            }

            try
            {
                using var fs = File.OpenRead(wavPath);
                if (fs.Length < 44) return 0;
                Span<byte> header = stackalloc byte[44];
                fs.ReadExactly(header);
                int byteRate = BitConverter.ToInt32(header.Slice(28, 4));
                int dataSize = BitConverter.ToInt32(header.Slice(40, 4));
                if (byteRate <= 0 || dataSize <= 0) return 0;
                return (long)((dataSize * 1000L) / byteRate);
            }
            catch
            {
                return 0;
            }
        }

        private static int MapProgress(int percent, int start, int end)
        {
            var clamped = Math.Clamp(percent, 0, 100);
            if (end <= start)
            {
                return start;
            }

            return start + (int)Math.Round((end - start) * (clamped / 100.0));
        }

        private static async Task DownloadModel(string modelPath, GgmlType ggmlType, Action<string>? log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            var message = $"{DateTime.Now} - Downloading Model {Path.GetFileName(modelPath)} to {Path.GetDirectoryName(modelPath)}";
            if (log != null)
            {
                log(message);
            }
            else
            {
                Console.WriteLine(message);
            }
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType);
            using var fileWriter = File.Open(modelPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await modelStream.CopyToAsync(fileWriter);
        }

        private sealed class DiarizationResult
        {
            public DiarizationResult(DiarizationTimeline timeline, TimeSpan mergeThreshold)
            {
                Timeline = timeline;
                MergeThreshold = mergeThreshold;
            }

            public DiarizationTimeline Timeline { get; }
            public TimeSpan MergeThreshold { get; }

            public string? GetStableSpeakerLabel(TimeSpan start, TimeSpan end)
            {
                var label = Timeline.GetSpeakerLabel(start, end);
                if (label == null)
                {
                    return null;
                }

                if (MergeThreshold <= TimeSpan.Zero)
                {
                    return label;
                }

                var segments = Timeline.Snapshot();
                if (segments.Count < 3)
                {
                    return label;
                }

                var ordered = new List<DiarizationSegment>(segments);
                ordered.Sort(static (a, b) => a.Start.CompareTo(b.Start));
                var midTicks = start.Ticks + ((end - start).Ticks / 2);
                var mid = new TimeSpan(midTicks);

                for (int i = 0; i < ordered.Count; i++)
                {
                    var seg = ordered[i];
                    if (seg.Start <= mid && seg.End >= mid)
                    {
                        if ((seg.End - seg.Start) <= MergeThreshold
                            && i > 0
                            && i < ordered.Count - 1)
                        {
                            var prev = ordered[i - 1];
                            var next = ordered[i + 1];
                            if (string.Equals(prev.Speaker, next.Speaker, StringComparison.Ordinal))
                            {
                                return prev.Speaker;
                            }
                        }
                        break;
                    }
                }

                return label;
            }
        }
    }
}
