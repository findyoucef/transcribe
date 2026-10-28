using Transcribe;

namespace transcribe.tests;

[TestClass]
public class DiarizationTimelineTests
{
    [TestMethod]
    public void Gap_ShouldUseRecentPastSpeakerAndNeverAFutureSpeaker()
    {
        var timeline = new DiarizationTimeline();
        timeline.Add(new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "S1"));
        timeline.Add(new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8), "S2"));

        Assert.IsNull(timeline.GetSpeakerLabel(TimeSpan.Zero, TimeSpan.FromSeconds(0.5)));
        Assert.AreEqual("S1", timeline.GetSpeakerLabel(TimeSpan.FromSeconds(3.2), TimeSpan.FromSeconds(3.8)));
        Assert.IsNull(timeline.GetSpeakerLabel(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(5)));
    }
}
