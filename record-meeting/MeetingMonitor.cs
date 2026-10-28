namespace RecordMeeting;

internal sealed class MeetingMonitor(Configuration config)
{
    public TeamsLogMonitor Teams { get; } = new(config.TeamsLogDirectory);
    public int[] ZoomMeetingPids { get; private set; } = [];
    public string? Error { get; private set; }
    public bool Active { get; private set; }
    public string DetectionSource => config.ZoomDetection
        ? "teams-meeting-log+zoom-meeting-process" : "teams-meeting-log";

    public void Poll()
    {
        Active = false;
        var errors = new List<string>();
        try
        {
            Teams.Poll();
            Active = Teams.Active.Count > 0;
        }
        catch (Exception ex) { errors.Add($"Teams log polling failed: {ex.Message}"); }
        if (config.ZoomDetection)
        {
            try
            {
                ZoomMeetingPids = ZoomProcessMonitor.Poll();
                Active |= ZoomMeetingPids.Length > 0;
            }
            catch (Exception ex) { errors.Add($"Zoom process polling failed: {ex.Message}"); }
        }
        Error = errors.Count > 0 ? string.Join("; ", errors) : null;
    }
}
