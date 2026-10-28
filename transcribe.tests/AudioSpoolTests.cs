using NAudio.Wave;
using Transcribe;

namespace transcribe.tests;

[TestClass]
public class AudioSpoolTests
{
    [TestMethod]
    public void SlowConsumer_ShouldRetainEverySampleAndProduceRecoverableWav()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            var packet = Enumerable.Range(0, 320).Select(i => (byte)i).ToArray();
            using (var spool = new AudioSpool(path, new WaveFormat(16000, 16, 1)))
            {
                for (int i = 0; i < 2500; i++) spool.Write(packet);
                spool.Complete();
                var actual = spool.ReadChunks().SelectMany(chunk => chunk).ToArray();
                CollectionAssert.AreEqual(Enumerable.Repeat(packet, 2500).SelectMany(p => p).ToArray(), actual);
            }
            using var reader = new WaveFileReader(path);
            Assert.AreEqual(25d, reader.TotalTime.TotalSeconds, 0.001);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void CaptureFailure_ShouldDrainSavedAudioThenReachConsumer()
    {
        using var spool = new AudioSpool(null, new WaveFormat(16000, 16, 1));
        var packet = new byte[] { 1, 2, 3, 4 };
        spool.Write(packet);
        spool.Complete(new IOException("device disconnected"));
        using var chunks = spool.ReadChunks().GetEnumerator();
        Assert.IsTrue(chunks.MoveNext());
        CollectionAssert.AreEqual(packet, chunks.Current);
        var error = Assert.ThrowsExactly<IOException>(() => chunks.MoveNext());
        StringAssert.Contains(error.Message, "device disconnected");
    }

    [TestMethod]
    public void StorageLimit_ShouldFailExplicitlyAndPreservePreviouslyCapturedAudio()
    {
        using var spool = new AudioSpool(null, new WaveFormat(16000, 16, 1), maxBytes: 4);
        spool.Write([1, 2, 3, 4]);
        Assert.ThrowsExactly<IOException>(() => spool.Write([5, 6]));
        spool.Complete();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, spool.ReadChunks().SelectMany(x => x).ToArray());
    }
}
