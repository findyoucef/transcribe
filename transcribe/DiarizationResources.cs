namespace Transcribe
{
    public sealed record DiarizationResources(string ModelPath, SpeakerEmbeddingModelConfig Config, string? ConfigPath);
}
