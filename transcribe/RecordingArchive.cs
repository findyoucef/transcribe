using System.Text.Json;

namespace Transcribe;

/// <summary>Audio backups and source timing for one transcript, including recorder retries.</summary>
public sealed class RecordingArchive
{
    private readonly string directory;
    private readonly DateTimeOffset origin;

    public RecordingArchive(string transcriptPath)
    {
        directory = Path.ChangeExtension(transcriptPath, "audio");
        Directory.CreateDirectory(directory);
        origin = DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("TX_MEETING_STARTED_AT"), out var start)
            ? start : DateTimeOffset.UtcNow;
    }

    public string CreatePath(string source) => Path.Combine(directory, $"{Guid.NewGuid():N}.{source}.wav");

    public TimeSpan Started(string path, string source)
    {
        var offset = DateTimeOffset.UtcNow - origin;
        File.WriteAllText(Path.ChangeExtension(path, "json"), JsonSerializer.Serialize(new SourceTiming(source, offset.TotalSeconds)));
        return offset;
    }

    public sealed record SourceTiming(string Source, double OffsetSeconds);
}
