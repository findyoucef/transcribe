using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

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
            return new DiarizationResources(modelPath, config, configPath);
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
