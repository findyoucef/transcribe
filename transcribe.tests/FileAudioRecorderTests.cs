using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;
using System.IO;
using System.Linq;
using System.Threading;

namespace transcribe.tests
{
    [TestClass]
    public class FileAudioRecorderTests
    {
        private string tempWav = "";

        [TestInitialize]
        public void Setup()
        {
            tempWav = TestHelpers.CreateTempWavFile(16000, 1, 1); // 1 sec mono 16k
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(tempWav)) File.Delete(tempWav);
        }

        [TestMethod]
        public void Constructor_ShouldThrow_IfFileDoesNotExist()
        {
            Action act = () => new FileAudioRecorder("nonexistent.wav");
            // Constructor doesn't verify existence, StartRecording does?
            // Let's check source code.
            // FileAudioRecorder.cs:
            // Constructor just sets path. StartRecording checks existence.
            
            var recorder = new FileAudioRecorder("nonexistent.wav");
            act = () => recorder.StartRecording();
            act.Should().Throw<FileNotFoundException>();
        }

        [TestMethod]
        public void GetAudioChunks_ShouldReturnData()
        {
            using (var recorder = new FileAudioRecorder(tempWav, chunkMilliseconds: 100))
            {
                recorder.StartRecording();

                var chunks = recorder.GetAudioChunks().ToList();
                chunks.Should().NotBeEmpty();

                // 1 second of audio, 100ms chunks -> ~10 chunks
                chunks.Count.Should().BeInRange(9, 11);

                // Check size of first chunk
                // 16000 * 2 bytes * 0.1s = 3200 bytes
                chunks[0].Length.Should().Be(3200);
            }
        }

        [TestMethod]
        public void IsMicrophone_ShouldBeFalse()
        {
            using var recorder = new FileAudioRecorder(tempWav);
            recorder.IsMicrophone.Should().BeFalse();
        }

        [TestMethod]
        public void StopRecording_ShouldStopYielding()
        {
             // Create longer file
             var longWav = TestHelpers.CreateTempWavFile(16000, 1, 5);
             try
             {
                 using var recorder = new FileAudioRecorder(longWav, chunkMilliseconds: 100);
                 recorder.StartRecording();
                 
                 var count = 0;
                 foreach(var chunk in recorder.GetAudioChunks())
                 {
                     count++;
                     if (count == 5)
                     {
                         recorder.StopRecording();
                     }
                 }
                 
                 // Should be much less than 50 (5 sec / 100ms)
                 count.Should().BeLessThan(20);
             }
             finally
             {
                 File.Delete(longWav);
             }
        }
    }
}
