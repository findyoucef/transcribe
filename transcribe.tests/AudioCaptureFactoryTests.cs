using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;

namespace transcribe.tests
{
    [TestClass]
    public class AudioCaptureFactoryTests
    {
        [TestMethod]
        public void CreateMicrophoneRecorder_ShouldReturnCorrectType()
        {
            // This test is OS dependent, so we check what we expect based on current OS
            var recorder = AudioCaptureFactory.CreateMicrophoneRecorder();
            
            if (OperatingSystem.IsWindows())
            {
                recorder.GetType().Name.Should().Be("MicrophoneRecorder");
            }
            else
            {
                recorder.GetType().Name.Should().Be("PortAudioRecorder");
            }
            recorder.Dispose();
        }

        [TestMethod]
        public void CreateSpeakerRecorder_ShouldReturnCorrectType()
        {
            var recorder = AudioCaptureFactory.CreateSpeakerRecorder();
             if (OperatingSystem.IsWindows())
            {
                recorder.GetType().Name.Should().Be("SpeakerRecorder");
            }
            else
            {
                recorder.GetType().Name.Should().Be("PortAudioRecorder");
            }
            recorder.Dispose();
        }
    }
}
