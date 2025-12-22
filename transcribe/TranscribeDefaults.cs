using System;
using System.Collections.Generic;

namespace Transcribe
{
    public static class TranscribeDefaults
    {
        public const string DefaultModel = "TinyEn";
        public const string DefaultLanguage = "en";
        public const int DefaultChunkSeconds = 3;
        public const string DefaultOllamaUrl = "http://localhost:11434/";
        public const string DefaultOllamaModel = "tx-agent:latest";

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
