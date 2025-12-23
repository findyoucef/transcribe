namespace Transcribe
{
    public sealed class DiarizationOptions
    {
        public bool Enabled { get; init; }
        public string? ModelPath { get; init; }
        public string? ModelUrl { get; init; }
        public string? ConfigPath { get; init; }
        public float SimilarityThreshold { get; init; } = TranscribeDefaults.DefaultDiarizationSimilarityThreshold;
        public float EnergyThreshold { get; init; } = TranscribeDefaults.DefaultDiarizationEnergyThreshold;
        public int MinSpeechMs { get; init; } = TranscribeDefaults.DefaultDiarizationMinSpeechMs;
        public int MinSilenceMs { get; init; } = TranscribeDefaults.DefaultDiarizationMinSilenceMs;
        public int MaxSegmentSeconds { get; init; } = TranscribeDefaults.DefaultDiarizationMaxSegmentSeconds;
        public float WindowSeconds { get; init; } = TranscribeDefaults.DefaultDiarizationWindowSeconds;
        public float WindowHopSeconds { get; init; } = TranscribeDefaults.DefaultDiarizationWindowHopSeconds;
        public int MinSpeakerWindows { get; init; } = TranscribeDefaults.DefaultDiarizationMinSpeakerWindows;
        public float ActiveMatchMargin { get; init; } = TranscribeDefaults.DefaultDiarizationActiveMatchMargin;
        public float FallbackSimilarityThreshold { get; init; } = TranscribeDefaults.DefaultDiarizationFallbackSimilarity;
        public float OverlapMinSimilarity { get; init; } = TranscribeDefaults.DefaultDiarizationOverlapMinSimilarity;
        public float OverlapMaxGap { get; init; } = TranscribeDefaults.DefaultDiarizationOverlapMaxGap;
        public int MinNewSpeakerWindows { get; init; } = TranscribeDefaults.DefaultDiarizationMinNewSpeakerWindows;
        public float InstantSwitchSimilarity { get; init; } = TranscribeDefaults.DefaultDiarizationInstantSwitchSimilarity;
        public bool LogScores { get; init; }
    }
}
