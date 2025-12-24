using System;
using System.Collections.Generic;

namespace Transcribe
{
    public static class TranscribeDefaults
    {
        public const string DefaultModel = "TinyEn";
        public const string DefaultLanguage = "en";
        public const int DefaultChunkSeconds = 7;
        public const string DefaultOllamaUrl = "http://localhost:11434/";
        public const string DefaultOllamaModel = "tx-agent:latest";
        public const string DefaultDiarizationModelFileName = "wespeaker_en_voxceleb_resnet293_LM.onnx";
        public const string DefaultDiarizationConfigFileName = "voxceleb_resnet293_LM.json";
        public const string DefaultDiarizationModelUrl =
            "https://wespeaker-1256283475.cos.ap-shanghai.myqcloud.com/models/voxceleb/voxceleb_resnet293_LM.onnx";
        public const float DefaultDiarizationSimilarityThreshold = 0.65f;
        public const float DefaultDiarizationEnergyThreshold = 0.01f;
        public const int DefaultDiarizationMinSpeechMs = 600;
        public const int DefaultDiarizationMinSilenceMs = 400;
        public const int DefaultDiarizationMaxSegmentSeconds = 12;
        public const float DefaultDiarizationWindowSeconds = 1.5f;
        public const float DefaultDiarizationWindowHopSeconds = 0.75f;
        public const int DefaultDiarizationMinSpeakerWindows = 5;
        public const float DefaultDiarizationActiveMatchMargin = 0.12f;
        public const float DefaultDiarizationFallbackSimilarity = 0.55f;
        public const float DefaultDiarizationOverlapMinSimilarity = 0.7f;
        public const float DefaultDiarizationOverlapMaxGap = 0.05f;
        public const int DefaultDiarizationMinNewSpeakerWindows = 3;
        public const float DefaultDiarizationInstantSwitchSimilarity = 0.82f;
        public const int DefaultLiveDiarizationDelaySeconds = 50;

        public static readonly IReadOnlyList<string> SupportedModels = new[]
        {
            "TinyEn",
            "BaseEn",
            "LargeV3",
            "LargeV3Turbo"
        };

        public static readonly IReadOnlyList<string> LanguageOptions = new[]
        {
            "Auto",
            "en",
            "fr",
            "es",
            "de",
            "it",
            "pt",
            "nl",
            "sv",
            "no",
            "da",
            "fi",
            "pl",
            "tr",
            "ru",
            "uk",
            "ar",
            "he",
            "hi",
            "ja",
            "ko",
            "zh"
        };

        public static string NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return DefaultLanguage;
            }

            var trimmed = language.Trim();
            if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return "auto";
            }

            return trimmed;
        }
    }
}
