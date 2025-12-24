using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;

namespace transcribe.tests
{
    [TestClass]
    public class AudioUtilsTests
    {
        [TestMethod]
        public void CalculateMaxLevel_ShouldReturnCorrectMax()
        {
            // 16-bit PCM. Max value is 32767. 
            // 32767 / 32768 ~= 0.9999
            
            short val1 = 16384; // 0.5
            short val2 = -32767; // ~1.0
            
            var buffer = new byte[4];
            BitConverter.TryWriteBytes(buffer.AsSpan(0), val1);
            BitConverter.TryWriteBytes(buffer.AsSpan(2), val2);

            var level = AudioUtils.CalculateMaxLevel(buffer);
            
            // Normalized: abs(-32767) / 32768f = 0.9999695
            level.Should().BeApproximately(0.9999695f, 0.0001f);
        }

        [TestMethod]
        public void CalculateMaxLevel_ShouldHandleEmptyBuffer()
        {
            var level = AudioUtils.CalculateMaxLevel(Array.Empty<byte>());
            level.Should().Be(0);
        }

        [TestMethod]
        public void ReadWavBytesAsMono_ShouldReturnFloatArray()
        {
            var wavPath = TestHelpers.CreateTempWavFile(16000, 1, 1);
            try
            {
                var bytes = File.ReadAllBytes(wavPath);
                var samples = AudioUtils.ReadWavBytesAsMono(bytes, 16000);
                
                // 16000 samples for 1 sec
                samples.Length.Should().Be(16000);
                
                // Check they are not all zero (we generated a sine wave)
                samples.Any(s => s != 0).Should().BeTrue();
            }
            finally
            {
                File.Delete(wavPath);
            }
        }

        [TestMethod]
        public void ReadAudioFileAsMono_ShouldResample()
        {
            // Create 44.1kHz file
            var wavPath = TestHelpers.CreateTempWavFile(44100, 1, 1);
            try
            {
                var samples = AudioUtils.ReadAudioFileAsMono(wavPath, 16000);
                
                // Should be resampled to 16000
                samples.Length.Should().Be(16000);
            }
            finally
            {
                File.Delete(wavPath);
            }
        }
    }
}
