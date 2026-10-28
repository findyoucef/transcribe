using Transcribe;

namespace transcribe.tests;

[TestClass]
public class DiarizationSessionTests
{
    [TestMethod]
    [TestCategory("Inference")]
    public void SpeechDetection_ShouldExcludeAnOpeningToneAndKeepTheFollowingSpeech()
    {
        var speechModel = Environment.GetEnvironmentVariable("TX_TEST_VAD_MODEL");
        if (string.IsNullOrWhiteSpace(speechModel) || !File.Exists(speechModel))
            Assert.Inconclusive("Set TX_TEST_VAD_MODEL to a local Silero VAD model; no audio is captured.");
        var speech = AudioUtils.ReadAudioFileAsMono(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.wav"), 16000).Take(160000).ToArray();
        var samples = new float[48000 + speech.Length];
        for (int i = 0; i < 48000; i++) samples[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 200 * i / 16000));
        speech.CopyTo(samples, 48000);
        var model = new SpeakerEmbeddingModel(Path.Combine(AppContext.BaseDirectory, "Assets", "speaker-tones.onnx"), new(), executionProvider: "cpu");
        using var pipeline = new DiarizationPipeline(model, new(), speechModelPath: speechModel);
        var session = pipeline.CreateSession();
        session.ProcessAudioChunk(samples, TimeSpan.Zero);
        session.Complete();

        var segments = session.Timeline.Snapshot();
        Assert.IsNotEmpty(segments, "Speech following the tone must still be identified.");
        Assert.IsTrue(segments.All(s => s.Start.TotalSeconds >= 2.8), string.Join("; ", segments));
        Assert.IsTrue(segments.Any(s => s.End.TotalSeconds > 8), "Speech at the end must be retained.");
    }

    [TestMethod]
    public void ShortPauses_ShouldNotMoveTheNextSpeakerEarlier()
    {
        const int rate = 16000;
        var samples = new float[8 * rate];
        for (int i = 0; i < samples.Length; i++)
        {
            // The first voice contains many pauses shorter than the 400 ms VAD timeout.
            if (i < 4 * rate && i % (rate / 2) >= rate / 5) continue;
            var frequency = i < 4 * rate ? 200 : 4000;
            samples[i] = (float)(0.4 * Math.Sin(2 * Math.PI * frequency * i / rate));
        }
        // Mean log-mel spectrum minus its scalar mean separates the two test tones.
        using var model = new SpeakerEmbeddingModel(Path.Combine(AppContext.BaseDirectory, "Assets", "speaker-tones.onnx"), new(), executionProvider: "cpu");
        var session = new DiarizationSession(model, new()
        {
            WindowSeconds = 0.5f, WindowHopSeconds = 0.25f,
            MinSpeakerWindows = 1, MinNewSpeakerWindows = 1,
            SimilarityThreshold = 0.95f, FallbackSimilarityThreshold = 0.95f
        });
        session.ProcessAudioChunk(samples, TimeSpan.Zero);
        session.Complete();
        var segments = session.Timeline.Snapshot();
        var first = session.GetSpeakerLabel(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2));
        var second = session.GetSpeakerLabel(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6.2));
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first, second, string.Join("; ", segments));
        Assert.IsTrue(segments.Where(s => s.Speaker == second).All(s => s.Start.TotalSeconds >= 3.5), string.Join("; ", segments));
    }
}
