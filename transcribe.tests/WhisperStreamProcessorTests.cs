using Transcribe;

namespace transcribe.tests;

[TestClass]
public class WhisperStreamProcessorTests
{
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
}
