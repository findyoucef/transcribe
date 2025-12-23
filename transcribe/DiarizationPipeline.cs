using System;

namespace Transcribe
{
    public sealed class DiarizationPipeline : IDisposable
    {
        private readonly SpeakerEmbeddingModel model;

        public DiarizationPipeline(SpeakerEmbeddingModel model, DiarizationOptions options, Action<string>? log = null)
        {
            this.model = model;
            Session = new DiarizationSession(model, options, log);
        }

        public DiarizationSession Session { get; }

        public void Dispose()
        {
            model.Dispose();
        }
    }
}
