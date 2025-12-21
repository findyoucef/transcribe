namespace Transcribe.Gui.Models;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message);

