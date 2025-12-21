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
            switch (model.Trim())
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
            string model = "LargeV3",
            string language = "en",
            string? outputDirectory = null,
            Action<string>? log = null)
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
            using var processor = whisperFactory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "en" : language)
                .Build();

            // Read WAV duration from header to drive progress
            long totalDurationMs = GetWavDurationMilliseconds(wavFileName);
            int lastPercent = -1;

            using var fileStream = File.OpenRead(wavFileName);

            using var outputFile = new StreamWriter(txtFilePath);
            await foreach (var result in processor.ProcessAsync(fileStream))
            {
                await outputFile.WriteLineAsync($"{result.Start}->{result.End}: {result.Text}");

                if (progress != null && totalDurationMs > 0)
                {
                    var percent = (int)Math.Clamp((result.End.TotalMilliseconds * 100.0) / totalDurationMs, 0, 100);
                    if (percent > lastPercent && percent < 100)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            await processor.DisposeAsync();

            if (progress != null)
            {
                progress.Report(100);
            }
        }

        private static long GetWavDurationMilliseconds(string wavPath)
        {
            // Basic WAV header parse: ByteRate at offset 28 (4 bytes, LE), Data chunk size at offset 40 (4 bytes, LE)
            // This assumes standard PCM with fixed header layout.
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
    }
}
