using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RecordMeeting;

internal static class AudioDevices
{
    private static readonly Regex DeviceLine = new(@"\[(\d+)\]\s+(.+?)(?:\s+\(\d+ch\))?$");

    public static Dictionary<int, string> Parse(string output)
    {
        var devices = new Dictionary<int, string>();
        foreach (var line in output.Split('\n'))
        {
            var match = DeviceLine.Match(line.TrimEnd('\r'));
            if (match.Success) devices.Add(int.Parse(match.Groups[1].Value), match.Groups[2].Value.Trim());
        }
        return devices;
    }

    public static int Select(Dictionary<int, string> devices, int? index, string? name, string kind,
        string? fallback = null, bool allowDisabled = false)
    {
        if (index is { } selected)
        {
            if ((allowDisabled && selected == -1) || devices.ContainsKey(selected)) return selected;
        }
        else
        {
            foreach (var preferred in new[] { name, fallback }.Where(value => !string.IsNullOrEmpty(value)))
                foreach (var device in devices)
                    if (device.Value.Contains(preferred!, StringComparison.OrdinalIgnoreCase)) return device.Key;
        }
        throw new InvalidOperationException($"Configured {kind} device '{index?.ToString() ?? name ?? "(not configured)"}' is unavailable; " +
            "available devices: " + string.Join(", ", devices.Select(device => $"[{device.Key}] {device.Value}")));
    }

    public static (int Speaker, int Mic) Resolve(Configuration config, string tx)
    {
        var speakers = Parse(Processes.Run(tx, ["list-speakers"]).Output);
        var microphones = Parse(Processes.Run(tx, ["list-devices"]).Output);
        return (Select(speakers, config.SpeakerDevice, config.SpeakerName, "speaker/loopback"),
            Select(microphones, config.MicDevice, config.MicrophoneName, "microphone", "MacBook Pro Microphone", true));
    }

    public static bool BuiltInSpeakers(JsonNode report, Action<string> log)
    {
        var output = report["SPAudioDataType"]!.AsArray()
            .SelectMany(group => group!["_items"]?.AsArray() ?? new JsonArray())
            .First(device => device?["coreaudio_default_audio_output_device"]?.GetValue<string>() == "spaudio_yes")!;
        var source = output["coreaudio_output_source"]?.GetValue<string>() ?? "";
        bool enabled = output["coreaudio_device_transport"]?.GetValue<string>() == "coreaudio_device_type_builtin"
            && source.Contains("speaker", StringComparison.OrdinalIgnoreCase);
        log($"Automatic echo cancellation {(enabled ? "enabled" : "disabled")} " +
            $"(default output={output["_name"]}, source={source}).");
        return enabled;
    }

    public static bool ShouldCancelEcho(Configuration config, int mic, Action<string> log)
    {
        if (mic < 0) return false;
        if (config.EchoCancellation is { } enabled) return enabled;
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2)) return false;
        try
        {
            var report = Processes.Run("/usr/sbin/system_profiler", ["SPAudioDataType", "-json"], 10).Output;
            return BuiltInSpeakers(JsonNode.Parse(report)!, log);
        }
        catch (Exception ex)
        {
            log($"Could not identify the audio output; leaving echo cancellation off: {ex.Message}");
            return false;
        }
    }

    public static string[] Arguments(Configuration config, int speaker, int mic, bool echo)
    {
        var arguments = new List<string>
        {
            "speaker", "--diarize", "--output-dir", config.TranscriptDirectory,
            "--device", speaker.ToString(CultureInfo.InvariantCulture),
            "--mic-device", mic.ToString(CultureInfo.InvariantCulture),
            "--model", config.Model, "--language", config.Language
        };
        if (echo) arguments.Add("--echo-cancellation");
        return arguments.ToArray();
    }
}
