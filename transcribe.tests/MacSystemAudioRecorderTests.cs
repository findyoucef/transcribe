using FluentAssertions;
using NAudio.Wave;
using Transcribe;
using Transcribe.MacAudio;
using System.Diagnostics;

namespace transcribe.tests;

[TestClass]
public class MacSystemAudioRecorderTests
{
    [TestMethod]
    public void NativeSpeakerSelection_ShouldNotListMicrophonesOrAcceptOldDeviceIndices()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2)) Assert.Inconclusive("Requires macOS 14.2+.");
        AudioCaptureFactory.GetSpeakers().Should().Equal(new AudioDeviceInfo(0, "System Audio (Native)", 1));
        Action create = () => AudioCaptureFactory.CreateSpeakerRecorder(1);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void CreateWavStream_ShouldResampleToWhisperFormatWithoutChangingDurationOrPitch()
    {
        using var recorder = new MacAudioRecorder();
        int rate = recorder.GetBytesPerSecond() / 2;
        var pcm = new byte[rate * 2];
        for (int i = 0; i < rate; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 16000));

        using var wav = recorder.CreateWavStream(pcm);
        wav.Position.Should().Be(0);
        using var reader = new WaveFileReader(wav);
        reader.WaveFormat.SampleRate.Should().Be(16000);
        reader.WaveFormat.Channels.Should().Be(1);
        reader.WaveFormat.BitsPerSample.Should().Be(16);
        reader.TotalTime.TotalSeconds.Should().BeApproximately(1, 0.005);
        var samples = AudioUtils.ReadWavBytesAsMono(wav.ToArray(), 16000);
        int crossings = Enumerable.Range(1, samples.Length - 1).Count(i => samples[i - 1] < 0 && samples[i] >= 0);
        crossings.Should().BeInRange(438, 441);
        samples.Max().Should().BeApproximately(16000f / 32768, 0.02f);
    }

    [TestMethod]
    public void StopBeforeStart_ShouldCompleteConsumersAndRejectRestart()
    {
        using var recorder = new MacAudioRecorder();
        recorder.StopRecording();
        recorder.StopRecording();
        recorder.GetAudioChunks().Should().BeEmpty();
        Action start = recorder.StartRecording;
        start.Should().Throw<InvalidOperationException>();
        recorder.Dispose();
        start.Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    [TestCategory("MacAudioLive")]
    [DoNotParallelize]
    public async Task LiveCapture_ShouldReceivePlaybackAndReleaseResourcesBetweenSessions()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2) || Environment.GetEnvironmentVariable("TRANSCRIBE_TEST_MAC_AUDIO") != "1")
            Assert.Inconclusive("Opt in with TRANSCRIBE_TEST_MAC_AUDIO=1 on macOS 14.2+. Plays a short tone and requires system audio recording permission.");

        var tone = TestHelpers.CreateTempWavFile(48000, 1, 2);
        try
        {
            for (int session = 0; session < 2; session++)
            {
                using var recorder = AudioCaptureFactory.CreateSpeakerRecorder();
                recorder.StartRecording();
                recorder.StartRecording(); // Starting an active session is idempotent.
                var consume = Task.Run(() => recorder.GetAudioChunks().SelectMany(chunk => chunk).ToArray());
                try
                {
                    using var playback = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
                    {
                        ArgumentList = { "-v", "0.1", tone },
                        UseShellExecute = false
                    })!;
                    await playback.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Task.Delay(300);
                }
                finally
                {
                    recorder.StopRecording();
                }
                var pcm = await consume.WaitAsync(TimeSpan.FromSeconds(5));
                Action restart = recorder.StartRecording;
                restart.Should().Throw<InvalidOperationException>();
                pcm.Should().NotBeEmpty("Core Audio should deliver buffers during playback");
                AudioUtils.CalculateMaxLevel(pcm).Should().BeGreaterThan(0.001f, "recording permission must allow real playback, not just silence");
                using var wav = recorder.CreateWavStream(pcm);
                var samples = AudioUtils.ReadWavBytesAsMono(wav.ToArray(), 16000);
                // Correlate with the known tone so ambient system sounds alone cannot pass.
                double sine = 0, cosine = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    sine += samples[i] * Math.Sin(2 * Math.PI * 440 * i / 16000);
                    cosine += samples[i] * Math.Cos(2 * Math.PI * 440 * i / 16000);
                }
                (2 * Math.Sqrt(sine * sine + cosine * cosine) / samples.Length).Should().BeGreaterThan(0.001);
            }
        }
        finally
        {
            File.Delete(tone);
        }
    }
}
