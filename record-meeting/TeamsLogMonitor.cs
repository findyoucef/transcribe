using System.Text;
using System.Text.RegularExpressions;

namespace RecordMeeting;

internal sealed class TeamsLogMonitor(string logDirectory)
{
    private static readonly Regex WindowEvent = new(@"Window: \(windowNumber=([\d,]+);[^\r\n]*\) (.+)$");
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private long offset;
    public string? CurrentPath { get; private set; }
    public IReadOnlySet<string> Active => active;

    public static void ApplyEvent(string line, ISet<string> windows)
    {
        // A new Teams process invalidates windows left behind by a crash.
        if (line.Contains("AppDelegate: ElapsedTimeSinceProcessStart:", StringComparison.Ordinal))
        {
            windows.Clear();
            return;
        }
        var match = WindowEvent.Match(line);
        if (!match.Success) return;
        var window = match.Groups[1].Value.Replace(",", "");
        var message = match.Groups[2].Value.TrimEnd('\r');
        // Teams sets this close prompt when the main window joins a meeting.
        // CallMonitor is only the transient mini window, not the call lifecycle.
        if (message == "Setting close prompt from web: meeting") windows.Add(window);
        else if (message.StartsWith("Setting close prompt from web:", StringComparison.Ordinal)
            || message is "Resetting close prompt from web" or "DoDispose()") windows.Remove(window);
    }

    public void Poll()
    {
        // The timestamped filenames retain event order across log rotation.
        var paths = Directory.Exists(logDirectory)
            ? Directory.EnumerateFiles(logDirectory, "MSTeams_*.log")
                .Order(StringComparer.Ordinal).ToArray() : [];
        if (paths.Length == 0)
        {
            CurrentPath = null;
            offset = 0;
            active.Clear();
            return;
        }
        var first = CurrentPath == null ? -1 : Array.IndexOf(paths, CurrentPath);
        if (first < 0)
        {
            // Recover a meeting whose start is in an older log, including when
            // the watcher restarts after rotation. App startup events reset history.
            first = 0;
            offset = 0;
            active.Clear();
        }
        for (int i = first; i < paths.Length; i++)
        {
            if (CurrentPath != paths[i])
            {
                CurrentPath = paths[i];
                offset = 0;
            }
            using var stream = new FileStream(CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset)
            {
                offset = 0;
                active.Clear();
            }
            stream.Seek(offset, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            int consumed = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (consumed == 0) continue;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, consumed).Replace("\0", "").Split('\n'))
                ApplyEvent(line, active);
            offset += consumed;
        }
    }
}
