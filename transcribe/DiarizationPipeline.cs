using System;
using Whisper.net;

namespace Transcribe
{
    public sealed class DiarizationPipeline : IDisposable
    {
        private readonly SpeakerEmbeddingModel model;
        private readonly Action<string>? log;
        private readonly WhisperVadFactory? speechFactory;
        private readonly WhisperVadProcessor? speechDetector;

        public DiarizationPipeline(SpeakerEmbeddingModel model, DiarizationOptions options, Action<string>? log = null, string? speechModelPath = null)
        {
            this.model = model;
            this.log = log;
            Options = options;
            try
            {
                if (speechModelPath != null && model.Config.SampleRate == 16000)
                {
                    speechFactory = WhisperVadFactory.FromPath(speechModelPath);
                    speechDetector = speechFactory.CreateBuilder().Build();
                }
                else if (speechModelPath != null)
                {
                    log?.Invoke("Speech detection uses 16 kHz audio; retaining energy detection for this custom speaker model's sample rate.");
                }
                Session = CreateSession();
            }
            catch
            {
                speechDetector?.Dispose();
                speechFactory?.Dispose();
                model.Dispose();
                throw;
            }
        }

        public DiarizationOptions Options { get; }
        public DiarizationSession Session { get; }
        public int SampleRate => model.Config.SampleRate;

        public DiarizationSession CreateSession(Action<string>? logOverride = null)
        {
            return new DiarizationSession(model, Options, logOverride ?? log, speechDetector);
        }

        public float[] ComputeEmbedding(float[] samples)
        {
            return model.ComputeEmbedding(samples);
        }

        public void Dispose()
        {
            speechDetector?.Dispose();
            speechFactory?.Dispose();
            model.Dispose();
        }
    }
}
