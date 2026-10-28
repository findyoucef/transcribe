using Transcribe;
using NAudio.Wave;
using System.Text.RegularExpressions;

namespace transcribe.tests;

[TestClass]
public class WhisperStreamProcessorTests
{
    [TestMethod]
    [TestCategory("Inference")]
    public async Task ReturningSpeakers_ShouldKeepTheirLabelsAcrossWindowAndTurnBoundaries()
    {
        var audio = Environment.GetEnvironmentVariable("TX_TEST_PODCAST_AUDIO");
        var whisper = Path.Combine(TranscribeDefaults.MacModelCacheDirectory, "ggml-large-v3-turbo.bin");
        if (string.IsNullOrWhiteSpace(audio) || !File.Exists(audio) || !File.Exists(whisper))
            Assert.Inconclusive("Set TX_TEST_PODCAST_AUDIO to the reviewed DF #287 recording and cache LargeV3Turbo to run the podcast regression.");
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var samples = AudioUtils.ReadAudioFileAsMono(audio, 16000).Take(70 * 16000).ToArray();
            var clip = Path.Combine(folder, "opening.wav");
            using (var writer = new WaveFileWriter(clip, new WaveFormat(16000, 16, 1))) writer.WriteSamples(samples, 0, samples.Length);
            var options = new DiarizationOptions { Enabled = true };
            var resources = await DiarizationModelLoader.EnsureResourcesAsync(options);
            var pipeline = new DiarizationPipeline(new SpeakerEmbeddingModel(resources.ModelPath, resources.Config), options, speechModelPath: resources.SpeechModelPath);
            var transcript = Path.Combine(folder, "transcript.txt");
            using var recorder = new FileAudioRecorder(clip);
            using (var processor = new WhisperStreamProcessor(whisper, "en", recorder, sourceLabel: "SPK", transcriptFilePath: transcript, emitConsoleOutput: false, showLevels: false, diarization: pipeline))
            {
                recorder.StartRecording();
                processor.StartProcessing();
                await processor.WaitForCompletionAsync();
            }
            var lines = File.ReadAllLines(transcript);
            string SpeakerAt(string text) => Regex.Match(lines.Single(l => l.Contains(text, StringComparison.OrdinalIgnoreCase)), @"\[SPK:([^\]]+)\]").Groups[1].Value;
            Assert.AreEqual(SpeakerAt("warm welcome"), SpeakerAt("Will Judd"), "The host's short return after the 50-second boundary must retain his identity.");
            Assert.AreEqual(SpeakerAt("good to be here"), SpeakerAt("boat out"), "Will's last sentence must not be attributed to the host who speaks next.");
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    [TestCategory("Inference")]
    [DataRow(false, 7.6, 8.0, 14.0)]
    [DataRow(true, 3.3, 3.6, 7.0)]
    public async Task Chunking_ShouldWaitForAPauseBoundContinuousSpeechAndPreserveEverySample(bool microphone, double firstMinimum, double firstMaximum, double maximum)
    {
        var model = Environment.GetEnvironmentVariable("TX_TEST_WHISPER_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            Assert.Inconclusive("Set TX_TEST_WHISPER_MODEL to a local Whisper model; no audio is captured.");
        var audio = new byte[24 * 32000];
        for (int i = 0; i < audio.Length / 2; i++)
        {
            var seconds = i / 16000d;
            var quiet = seconds is >= 3.1 and < 3.5 or >= 7.4 and < 7.8;
            short sample = quiet ? (short)0 : (short)(10000 * Math.Sin(i * 0.1));
            audio[i * 2] = (byte)sample;
            audio[i * 2 + 1] = (byte)(sample >> 8);
        }
        var recorder = new ChunkRecorder(audio, microphone);
        var path = Path.GetTempFileName();
        try
        {
            using var processor = new WhisperStreamProcessor(model, "en", recorder, transcriptFilePath: path, emitConsoleOutput: false, showLevels: false);
            processor.StartProcessing();
            await processor.WaitForCompletionAsync();
            var firstSeconds = recorder.Processed[0].Length / 32000d;
            Assert.IsTrue(firstSeconds >= firstMinimum && firstSeconds <= firstMaximum, $"First cut was at {firstSeconds:F2}s, inside the speech.");
            Assert.IsTrue(recorder.Processed.All(chunk => chunk.Length <= maximum * 32000), "Continuous speech must not grow the buffer indefinitely.");
            CollectionAssert.AreEqual(audio, recorder.Processed.SelectMany(chunk => chunk).ToArray(), "Chunking must not lose or repeat samples.");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [TestCategory("Inference")]
    public async Task CaptureFailure_ShouldFaultCompletionAndStillAllowDisposal()
    {
        var model = Environment.GetEnvironmentVariable("TX_TEST_WHISPER_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            Assert.Inconclusive("Set TX_TEST_WHISPER_MODEL to a local Whisper model; no audio is captured.");
        var path = Path.GetTempFileName();
        try
        {
            using var processor = new WhisperStreamProcessor(model, "en", new FailingRecorder(), transcriptFilePath: path, emitConsoleOutput: false, showLevels: false);
            processor.StartProcessing();
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => processor.WaitForCompletionAsync());
            StringAssert.Contains(error.Message, "capture failed");
        }
        finally { File.Delete(path); }
    }

    private sealed class FailingRecorder : IAudioRecorder
    {
        public bool IsMicrophone => false;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
        public float GetCurrentAudioLevel() => 0;
        public int GetBytesPerSecond() => 32000;
        public MemoryStream CreateWavStream(byte[] data) => throw new NotSupportedException();
        public IEnumerable<byte[]> GetAudioChunks() { throw new IOException("capture failed"); }
    }

    private sealed class ChunkRecorder(byte[] audio, bool microphone) : IAudioRecorder
    {
        public List<byte[]> Processed { get; } = new();
        public bool IsMicrophone => microphone;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
        public float GetCurrentAudioLevel() => 0;
        public int GetBytesPerSecond() => 32000;
        public IEnumerable<byte[]> GetAudioChunks() => audio.Chunk(1600);
        public MemoryStream CreateWavStream(byte[] data)
        {
            Processed.Add(data);
            using var stream = new MemoryStream();
            using (var writer = new WaveFileWriter(stream, new WaveFormat(16000, 16, 1))) writer.Write(data, 0, data.Length);
            return new MemoryStream(stream.ToArray());
        }
    }
}
