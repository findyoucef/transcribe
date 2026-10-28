using Microsoft.ML.OnnxRuntime;
using Transcribe;

namespace transcribe.tests;

[TestClass]
public class SpeakerEmbeddingModelTests
{
    // Tiny ONNX fixture: ReduceMean(feats[1, frames, 80], axis=1) -> embs[1, 80].
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "embedding.onnx");

    [TestMethod]
    public void AutomaticProvider_ShouldPreserveCpuEmbeddingsAndReportItsSelection()
    {
        var logs = new List<string>();
        using var cpu = new SpeakerEmbeddingModel(ModelPath, new(), executionProvider: "cpu");
        using var automatic = new SpeakerEmbeddingModel(ModelPath, new(), logs.Add, "auto");
        foreach (var length in new[] { 24000, 32000 })
        {
            var samples = Enumerable.Range(0, length).Select(i => (float)(0.4 * Math.Sin(i * 0.07) + 0.2 * Math.Sin(i * 0.11))).ToArray();
            var expected = cpu.ComputeEmbedding(samples);
            var actual = automatic.ComputeEmbedding(samples);
            Assert.HasCount(expected.Length, actual);
            Assert.IsTrue(actual.All(float.IsFinite));
            Assert.AreEqual(1d, actual.Sum(v => (double)v * v), 0.001);
            Assert.IsGreaterThan(0.999, actual.Zip(expected).Sum(p => (double)p.First * p.Second), "Provider selection must preserve speaker similarity.");
        }
        var expectedProvider = OperatingSystem.IsMacOSVersionAtLeast(12) && OrtEnv.Instance().GetAvailableProviders().Contains("CoreMLExecutionProvider") ? "CoreML" : "CPU";
        Assert.IsTrue(logs.Any(line => line.Contains($"execution provider: {expectedProvider}")), string.Join('\n', logs));
    }

    [TestMethod]
    public void CpuOverride_ShouldWorkAndRejectUnknownProviderNames()
    {
        var logs = new List<string>();
        using var cpu = new SpeakerEmbeddingModel(ModelPath, new(), logs.Add, "cpu");
        Assert.IsTrue(logs.Any(line => line.Contains("execution provider: CPU")));
        Assert.ThrowsExactly<ArgumentException>(() => new SpeakerEmbeddingModel(ModelPath, new(), executionProvider: "cor eml"));
    }
}
