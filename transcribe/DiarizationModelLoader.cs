using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Whisper.net.Ggml;

namespace Transcribe
{
    public static class DiarizationModelLoader
    {
        public static string GetDefaultModelPath()
        {
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "models", TranscribeDefaults.DefaultDiarizationModelFileName);
            return OperatingSystem.IsMacOS() && !File.Exists(bundledPath)
                ? Path.Combine(TranscribeDefaults.MacModelCacheDirectory, TranscribeDefaults.DefaultDiarizationModelFileName)
                : bundledPath;
        }

        public static string GetDefaultConfigPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "models", TranscribeDefaults.DefaultDiarizationConfigFileName);
        }

        public static async Task<DiarizationResources> EnsureResourcesAsync(DiarizationOptions options, Action<string>? log = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var modelPath = ResolvePath(options.ModelPath, GetDefaultModelPath());
            var configPath = ResolvePath(options.ConfigPath, GetDefaultConfigPath());

            if (!File.Exists(modelPath))
            {
                if (!string.IsNullOrWhiteSpace(options.ModelUrl))
                {
                    await DownloadModelAsync(options.ModelUrl.Trim(), modelPath, log);
                }
                else
                {
                    throw new FileNotFoundException(
                        $"Diarization model not found: {modelPath}. Provide --diarization-model or --diarization-model-url.");
                }
            }

            var config = SpeakerEmbeddingModelConfig.Load(configPath, log);
            var speechModelPath = Path.Combine(OperatingSystem.IsMacOS()
                ? TranscribeDefaults.MacModelCacheDirectory
                : Path.Combine(AppContext.BaseDirectory, "models"), "ggml-silero-v6.2.0.bin");
            if (!File.Exists(speechModelPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(speechModelPath)!);
                var temporary = speechModelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    log?.Invoke($"Downloading speech detection model to: {speechModelPath}");
                    await using (var input = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync())
                    await using (var output = File.Create(temporary))
                        await input.CopyToAsync(output);
                    File.Move(temporary, speechModelPath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return new DiarizationResources(modelPath, config, configPath) { SpeechModelPath = speechModelPath };
        }

        private static string ResolvePath(string? path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return fallback;
            }

            return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        }

        private static async Task DownloadModelAsync(string url, string modelPath, Action<string>? log)
        {
            var targetDir = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            log?.Invoke($"Downloading diarization model to: {modelPath}");

            using var http = new HttpClient();
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await stream.CopyToAsync(file);

            log?.Invoke("Diarization model download complete.");
        }
    }
}
