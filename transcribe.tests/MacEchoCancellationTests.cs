using System.Diagnostics;
using FluentAssertions;
using Transcribe;
using Transcribe.MacAudio;

namespace transcribe.tests;

[TestClass]
public class MacEchoCancellationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EchoCancellation_ShouldSelectNativeMicrophoneOrRejectUnsupportedPlatform()
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            using var recorder = AudioCaptureFactory.CreateMicrophoneRecorder(0, echoCancellation: true);
            recorder.Should().BeOfType<MacAudioRecorder>();
            recorder.IsMicrophone.Should().BeTrue();
            using var system = AudioCaptureFactory.CreateSpeakerRecorder();
            system.IsMicrophone.Should().BeFalse();
            Action invalidFormat = () => AudioCaptureFactory.CreateMicrophoneRecorder(0, channels: 2, echoCancellation: true);
            invalidFormat.Should().Throw<ArgumentException>();
        }
        else
        {
            Action unsupported = () => AudioCaptureFactory.CreateMicrophoneRecorder(echoCancellation: true);
            unsupported.Should().Throw<PlatformNotSupportedException>();
        }
    }

    [TestMethod]
    [TestCategory("MacAecLive")]
    [DoNotParallelize]
    public async Task LiveEchoCancellation_ShouldReduceSpeakerLeakageAndPreserveSystemCapture()
    {
        int device = -1;
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2)
            || !int.TryParse(Environment.GetEnvironmentVariable("TRANSCRIBE_TEST_MAC_AEC_DEVICE"), out device))
            Assert.Inconclusive("Opt in with TRANSCRIBE_TEST_MAC_AEC_DEVICE=<mic index>. Requires a quiet room, physical speakers, and microphone permission. Plays speech twice; records only in memory.");

        var speech = Path.Combine(Path.GetTempPath(), $"tx-aec-{Guid.NewGuid():N}.aiff");
        try
        {
            using var say = Process.Start(new ProcessStartInfo("/usr/bin/say")
            {
                ArgumentList = { "-o", speech, "This is the remote speaker. Echo cancellation should remove this voice from the microphone while keeping the system audio clear. This is the remote speaker speaking again." },
                UseShellExecute = false
            })!;
            await say.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var raw = await RecordPlayback(device, false, speech);
            var clean = await RecordPlayback(device, true, speech);
            TestContext.WriteLine($"Raw mic RMS: {raw.Microphone:R}; processed mic RMS: {clean.Microphone:R}");
            double reduction = 20 * Math.Log10((raw.Microphone / raw.System) / Math.Max(clean.Microphone / clean.System, 1e-9));
            TestContext.WriteLine($"Speaker leakage reduction, normalized to system audio level: {reduction:F1} dB");
            TestContext.WriteLine($"System audio RMS: raw run {raw.System:R}; AEC run {clean.System:R}");
            raw.Microphone.Should().BeGreaterThan(0.001, "the microphone must hear the speakers for this acoustic test to be meaningful");
            reduction.Should().BeGreaterThan(6, "echo cancellation should reduce acoustic speaker leakage by at least 6 dB");
            clean.System.Should().BeGreaterThan(0.01, "the direct system audio must remain audible for transcription even if macOS applies playback ducking");
        }
        finally
        {
            File.Delete(speech);
        }
    }

    private static async Task<(double Microphone, double System)> RecordPlayback(int device, bool echoCancellation, string speech)
    {
        using var mic = AudioCaptureFactory.CreateMicrophoneRecorder(device, echoCancellation: echoCancellation);
        using var system = AudioCaptureFactory.CreateSpeakerRecorder();
        Task<byte[]>? micData = null, systemData = null;
        try
        {
            mic.StartRecording();
            system.StartRecording();
            micData = Task.Run(() => mic.GetAudioChunks().SelectMany(x => x).ToArray());
            systemData = Task.Run(() => system.GetAudioChunks().SelectMany(x => x).ToArray());
            await Task.Delay(500);
            using var playback = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
            {
                ArgumentList = { speech }, UseShellExecute = false
            })!;
            await playback.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(300);
        }
        finally
        {
            system.StopRecording();
            mic.StopRecording();
        }
        using var micWav = mic.CreateWavStream(await micData!.WaitAsync(TimeSpan.FromSeconds(5)));
        using var systemWav = system.CreateWavStream(await systemData!.WaitAsync(TimeSpan.FromSeconds(5)));
        var microphone = AudioUtils.ReadWavBytesAsMono(micWav.ToArray(), 16000);
        var reference = AudioUtils.ReadWavBytesAsMono(systemWav.ToArray(), 16000);
        return (Math.Sqrt(microphone.Select(x => (double)x * x).Average()),
            Math.Sqrt(reference.Select(x => (double)x * x).Average()));
    }
}
