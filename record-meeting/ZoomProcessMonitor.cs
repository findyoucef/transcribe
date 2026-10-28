using System.Globalization;
using System.Text.RegularExpressions;

namespace RecordMeeting;

internal static class ZoomProcessMonitor
{
    private const string HelperSuffix = "/Contents/Frameworks/CptHost.app/Contents/MacOS/CptHost";
    private static readonly Regex ProcessLine = new(@"^\s*(\d+)\s+(\d+)\s+(.+)$", RegexOptions.Multiline);

    public static int[] Poll() => Parse(Processes.Run("/bin/ps",
        ["-ww", "-U", Native.GetUid().ToString(CultureInfo.InvariantCulture), "-o", "pid=,ppid=,comm="]).Output);

    public static int[] Parse(string output)
    {
        var processes = new Dictionary<int, (int Parent, string Path)>();
        foreach (Match match in ProcessLine.Matches(output))
            processes[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] =
                (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), match.Groups[3].Value.Trim());

        // Zoom's main app also runs while idle. CptHost belongs to a meeting;
        // require its parent to be the Zoom executable in the same app bundle.
        return processes.Where(entry =>
        {
            var (parent, path) = entry.Value;
            return path.EndsWith(HelperSuffix, StringComparison.OrdinalIgnoreCase)
                && processes.TryGetValue(parent, out var owner)
                && owner.Path.Equals(path[..^HelperSuffix.Length] + "/Contents/MacOS/zoom.us", StringComparison.OrdinalIgnoreCase);
        }).Select(entry => entry.Key).Order().ToArray();
    }
}
