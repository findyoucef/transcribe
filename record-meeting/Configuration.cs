using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RecordMeeting;

internal sealed class Configuration
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DefaultPath => Path.Combine(Home, ".config/record-meeting/config.json");
    public required string TranscriptDirectory { get; init; }
    public required string TeamsLogDirectory { get; init; }
    public bool ZoomDetection { get; init; }
    public required string TxBinary { get; init; }
    public double PollSeconds { get; init; }
    public double GraceSeconds { get; init; }
    public required string Model { get; init; }
    public required string Language { get; init; }
    public string? SpeakerName { get; init; }
    public string? MicrophoneName { get; init; }
    public int? SpeakerDevice { get; init; }
    public int? MicDevice { get; init; }
    // null means automatic; true and false are explicit overrides.
    public bool? EchoCancellation { get; init; }

    public static JsonObject Defaults() => new()
    {
        ["transcript_dir"] = "~/transcripts",
        ["teams_log_dir"] = "~/Library/Group Containers/UBF8T346G9.com.microsoft.teams/Library/Application Support/Logs",
        ["zoom_detection"] = true,
        ["tx_bin"] = "/usr/local/bin/tx_cli/tx",
        ["poll_seconds"] = 15,
        ["grace_seconds"] = 60,
        ["model"] = "TinyEn",
        ["language"] = "en",
        ["speaker_name"] = "System Audio (Native)",
        ["microphone_name"] = "WH-1000XM5",
        ["echo_cancellation"] = "auto"
    };

    public static Configuration Load(string path)
    {
        var values = Defaults();
        if (File.Exists(path))
        {
            var loaded = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"Configuration must be a JSON object: {path}");
            foreach (var entry in loaded) values[entry.Key] = entry.Value?.DeepClone();
        }
        string Text(string key) => values[key]?.GetValue<string>()
            ?? throw new InvalidDataException($"Missing configuration value: {key}");
        double Seconds(string key, double minimum)
        {
            var number = double.Parse(values[key]!.ToString(), CultureInfo.InvariantCulture);
            if (!double.IsFinite(number)) throw new InvalidDataException($"{key} must be finite.");
            return Math.Max(minimum, number);
        }
        int? Device(string key) => values[key] is { } value
            ? int.Parse(value.ToString(), CultureInfo.InvariantCulture) : null;

        bool? echo = null;
        if (values["echo_cancellation"] is JsonValue mode && mode.TryGetValue<bool>(out var enabled))
            echo = enabled;
        else if (values["echo_cancellation"]?.ToString() != "auto")
            throw new InvalidDataException("echo_cancellation must be \"auto\", true, or false.");

        return new Configuration
        {
            TranscriptDirectory = ExpandPath(Text("transcript_dir")),
            TeamsLogDirectory = ExpandPath(Text("teams_log_dir")),
            ZoomDetection = values["zoom_detection"]!.GetValue<bool>(),
            TxBinary = ExpandPath(Text("tx_bin")),
            PollSeconds = Seconds("poll_seconds", 0.1),
            GraceSeconds = Seconds("grace_seconds", 0),
            Model = Text("model"), Language = Text("language"),
            SpeakerName = values["speaker_name"]?.GetValue<string>(),
            MicrophoneName = values["microphone_name"]?.GetValue<string>(),
            SpeakerDevice = Device("speaker_device"), MicDevice = Device("mic_device"),
            EchoCancellation = echo
        };
    }

    public static string ExpandPath(string path)
    {
        if (path == "~") path = Home;
        else if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(Home, path[2..]);
        path = Regex.Replace(path, @"\$(?:\{(\w+)\}|(\w+))", match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Success
                ? match.Groups[1].Value : match.Groups[2].Value) ?? match.Value);
        return Path.GetFullPath(path);
    }

    public string ResolveTx()
    {
        var candidates = new[] { TxBinary }.Concat(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
                .Where(directory => directory.Length > 0).Select(directory => Path.Combine(directory, "tx")));
        return candidates.FirstOrDefault(path => File.Exists(path) && Native.Access(path, 1) == 0)
            ?? throw new FileNotFoundException($"tx was not found or is not executable: {TxBinary}");
    }
}

internal static class JsonFiles
{
    // Write the JSON DOM directly, without reflection-based serialization in AOT.
    public static string Text(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(string path, JsonNode node)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Text(node) + "\n");
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    public static JsonObject ReadState(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
}
