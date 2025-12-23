namespace Transcribe
{
    public static class TranscriptionTagger
    {
        public static string ApplyTags(string text, string? sourceLabel, string? speakerLabel)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var prefix = BuildPrefix(sourceLabel, speakerLabel);
            return string.IsNullOrWhiteSpace(prefix) ? text : $"{prefix} {text}";
        }

        private static string? BuildPrefix(string? sourceLabel, string? speakerLabel)
        {
            var hasSource = !string.IsNullOrWhiteSpace(sourceLabel);
            var hasSpeaker = !string.IsNullOrWhiteSpace(speakerLabel);

            if (!hasSource && !hasSpeaker)
            {
                return null;
            }

            if (hasSource && hasSpeaker)
            {
                return $"[{sourceLabel}:{speakerLabel}]";
            }

            return hasSource ? $"[{sourceLabel}]" : $"[{speakerLabel}]";
        }
    }
}
