using System;

namespace Transcribe
{
    public sealed class DiarizationPipeline : IDisposable
    {
        private readonly SpeakerEmbeddingModel model;
        private readonly Action<string>? log;

        public DiarizationPipeline(SpeakerEmbeddingModel model, DiarizationOptions options, Action<string>? log = null)
        {
            this.model = model;
            this.log = log;
            Options = options;
            Session = new DiarizationSession(model, options, log);
        }

        public DiarizationOptions Options { get; }
        public DiarizationSession Session { get; }
        public int SampleRate => model.Config.SampleRate;

        public DiarizationSession CreateSession(Action<string>? logOverride = null)
        {
            return new DiarizationSession(model, Options, logOverride ?? log);
        }

        public float[] ComputeEmbedding(float[] samples)
        {
            return model.ComputeEmbedding(samples);
        }

        public void Dispose()
        {
            model.Dispose();
        }
    }
}
