using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace transcribe.tests
{
    [TestClass]
    public class TranscriptionMergerTests
    {
        [TestMethod]
        public async Task Submit_ShouldWriteOrderedItemsToFile()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                using (var merger = new TranscriptionMerger(tempFile, TimeSpan.FromMilliseconds(100)))
                {
                    var item1 = new TranscriptionItem(TimeSpan.Zero, TimeSpan.FromSeconds(1), "First", "MIC");
                    var item2 = new TranscriptionItem(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Second", "MIC");
                    
                    // Submit out of order? The buffer flush sorts them.
                    // But buffer flush only happens periodically. 
                    // If we submit all at once, they might be in one buffer.
                    
                    merger.Submit(new[] { item2, item1 });
                    
                    await Task.Delay(500); // Wait for flush
                }

                var lines = await File.ReadAllLinesAsync(tempFile);
                lines.Should().HaveCount(2);
                lines[0].Should().Contain("00:00:00->00:00:01: First");
                lines[1].Should().Contain("00:00:01->00:00:02: Second");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public void Dispose_ShouldNotThrow()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var merger = new TranscriptionMerger(tempFile);
                merger.Dispose();
                
                // Double dispose
                Action act = () => merger.Dispose();
                act.Should().NotThrow();
            }
            finally
            {
                 if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
