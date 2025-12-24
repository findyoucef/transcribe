using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Transcribe;

namespace transcribe.tests
{
    [TestClass]
    public class DiarizationOptionsTests
    {
        [TestMethod]
        public void Defaults_ShouldMatchTranscribeDefaults()
        {
            var options = new DiarizationOptions();

            options.Enabled.Should().BeFalse();
            options.SimilarityThreshold.Should().Be(TranscribeDefaults.DefaultDiarizationSimilarityThreshold);
            options.EnergyThreshold.Should().Be(TranscribeDefaults.DefaultDiarizationEnergyThreshold);
            options.MinSpeechMs.Should().Be(TranscribeDefaults.DefaultDiarizationMinSpeechMs);
            options.MinSilenceMs.Should().Be(TranscribeDefaults.DefaultDiarizationMinSilenceMs);
            options.MaxSegmentSeconds.Should().Be(TranscribeDefaults.DefaultDiarizationMaxSegmentSeconds);
            options.WindowSeconds.Should().Be(TranscribeDefaults.DefaultDiarizationWindowSeconds);
            options.WindowHopSeconds.Should().Be(TranscribeDefaults.DefaultDiarizationWindowHopSeconds);
            options.MinSpeakerWindows.Should().Be(TranscribeDefaults.DefaultDiarizationMinSpeakerWindows);
            options.ActiveMatchMargin.Should().Be(TranscribeDefaults.DefaultDiarizationActiveMatchMargin);
            options.FallbackSimilarityThreshold.Should().Be(TranscribeDefaults.DefaultDiarizationFallbackSimilarity);
            options.OverlapMinSimilarity.Should().Be(TranscribeDefaults.DefaultDiarizationOverlapMinSimilarity);
            options.OverlapMaxGap.Should().Be(TranscribeDefaults.DefaultDiarizationOverlapMaxGap);
            options.MinNewSpeakerWindows.Should().Be(TranscribeDefaults.DefaultDiarizationMinNewSpeakerWindows);
            options.InstantSwitchSimilarity.Should().Be(TranscribeDefaults.DefaultDiarizationInstantSwitchSimilarity);
            options.LogScores.Should().BeFalse();
        }

        [TestMethod]
        public void Initialization_ShouldSetProperties()
        {
            var options = new DiarizationOptions
            {
                Enabled = true,
                ModelPath = "path/to/model",
                SimilarityThreshold = 0.8f
            };

            options.Enabled.Should().BeTrue();
            options.ModelPath.Should().Be("path/to/model");
            options.SimilarityThreshold.Should().Be(0.8f);
        }
    }
}
