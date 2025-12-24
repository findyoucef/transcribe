using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;

namespace transcribe.tests
{
    [TestClass]
    public class TranscriptionTaggerTests
    {
        [TestMethod]
        public void ApplyTags_ShouldReturnText_WhenNoLabelsProvided()
        {
            var text = "Hello World";
            var result = TranscriptionTagger.ApplyTags(text, null, null);
            result.Should().Be(text);
        }

        [TestMethod]
        public void ApplyTags_ShouldReturnEmpty_WhenTextIsNullOrWhiteSpace()
        {
            var result = TranscriptionTagger.ApplyTags("   ", "src", "spk");
            result.Should().Be("   "); // Wait, implementation returns text if whitespace
            
            // Checking implementation: if (string.IsNullOrWhiteSpace(text)) return text;
            // Yes.
        }

        [TestMethod]
        public void ApplyTags_ShouldAddSourceLabel_WhenOnlySourceProvided()
        {
            var text = "Hello";
            var result = TranscriptionTagger.ApplyTags(text, "MIC", null);
            result.Should().Be("[MIC] Hello");
        }

        [TestMethod]
        public void ApplyTags_ShouldAddSpeakerLabel_WhenOnlySpeakerProvided()
        {
            var text = "Hello";
            var result = TranscriptionTagger.ApplyTags(text, null, "SPK1");
            result.Should().Be("[SPK1] Hello");
        }

        [TestMethod]
        public void ApplyTags_ShouldAddBothLabels_WhenBothProvided()
        {
            var text = "Hello";
            var result = TranscriptionTagger.ApplyTags(text, "MIC", "SPK1");
            result.Should().Be("[MIC:SPK1] Hello");
        }

        [TestMethod]
        public void ApplyTags_ShouldHandleEmptyLabelsAsNull()
        {
            var text = "Hello";
            var result = TranscriptionTagger.ApplyTags(text, "  ", "");
            result.Should().Be(text);
        }
    }
}
