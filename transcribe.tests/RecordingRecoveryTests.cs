using System.Globalization;
using System.Text.Json;
using Transcribe;

namespace transcribe.tests;

[TestClass]
public class RecordingRecoveryTests
{
    [TestMethod]
    [TestCategory("Inference")]
    public async Task Recover_ShouldPreserveSourceTimingAndRefuseToOverwriteExistingOutput()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TX_TEST_WHISPER_MODEL")))
            Assert.Inconclusive("Set TX_TEST_WHISPER_MODEL to opt in to prerecorded inference tests.");
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(root, "meeting.audio");
        Directory.CreateDirectory(archive);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.wav"), Path.Combine(archive, "source.wav"));
            File.WriteAllText(Path.Combine(archive, "source.json"), JsonSerializer.Serialize(new RecordingArchive.SourceTiming("SPK", 65)));
            var args = new[] { "recover", archive, "--model", "TinyEn" };
            Assert.AreEqual(0, await Program.Main(args));
            var path = Path.Combine(root, "meeting.recovered.txt");
            var lines = File.ReadAllLines(path);
            Assert.IsNotEmpty(lines);
            Assert.IsTrue(lines.All(line => line.Contains("[SPK]")));
            Assert.IsTrue(lines.All(line => TimeSpan.Parse(line.Split("->")[0], CultureInfo.InvariantCulture).TotalSeconds >= 65));
            var original = File.ReadAllText(path);
            Assert.AreNotEqual(0, await Program.Main(args));
            Assert.AreEqual(original, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
