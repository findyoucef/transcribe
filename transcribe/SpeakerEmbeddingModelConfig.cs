using System.Text.Json;

namespace Transcribe
{
    public sealed class SpeakerEmbeddingModelConfig
    {
        public string? InputName { get; set; }
        public string? OutputName { get; set; }
        public string InputLayout { get; set; } = "Auto";
        public string InputType { get; set; } = "logmel";
        public int SampleRate { get; set; } = 16000;
        public int MelBins { get; set; } = 80;
        public int FrameMs { get; set; } = 25;
        public int HopMs { get; set; } = 10;
        public bool ApplyPreEmphasis { get; set; } = true;
        public float PreEmphasis { get; set; } = 0.97f;
        public bool ApplyMeanNormalization { get; set; } = false;
        public bool ApplyVarianceNormalization { get; set; } = false;

        public static SpeakerEmbeddingModelConfig Load(string? path, Action<string>? log = null)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<SpeakerEmbeddingModelConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (config != null)
                {
                    return config;
                }
                log?.Invoke($"Diarization config could not be parsed, using defaults: {path}");
            }

            return new SpeakerEmbeddingModelConfig();
        }
    }
}
