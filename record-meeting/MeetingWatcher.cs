using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace RecordMeeting;

internal sealed class MeetingWatcher
{
    private readonly Configuration config;
    private readonly MeetingMonitor monitor;
    private readonly string statePath;
    private readonly string logPath;
    private JsonObject state;
    private Process? txProcess;
    private TextWriter? txLog;
    private Task? logPump;
    private DateTimeOffset? recordingStarted;
    private double? meetingEnded;
    private double lastRetry = double.NegativeInfinity;
    private int speaker, microphone;
    private bool echoCancellation;
    private string? meetingId;
    private string? meetingStartedAt;
    private static double MonotonicSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public static string Timestamp() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public MeetingWatcher(Configuration config)
    {
        this.config = config;
        Directory.CreateDirectory(config.TranscriptDirectory);
        statePath = Path.Combine(config.TranscriptDirectory, ".watcher-state.json");
        logPath = Path.Combine(config.TranscriptDirectory, ".watcher.log");
        state = JsonFiles.ReadState(statePath);
        meetingId = state["meetingId"]?.GetValue<string>();
        meetingStartedAt = state["meetingStartedAt"]?.GetValue<string>();
        monitor = new MeetingMonitor(config);
    }

    private void Log(string message) => File.AppendAllText(logPath, $"[{Timestamp()}] {message}\n");

    private void WriteState(string status, JsonObject? extra = null)
    {
        var next = new JsonObject
        {
            ["status"] = status,
            ["watcherPid"] = Environment.ProcessId,
            ["detectionSource"] = monitor.DetectionSource,
            ["teamsLog"] = monitor.Teams.CurrentPath,
            ["zoomDetectionEnabled"] = config.ZoomDetection,
            ["zoomMeetingPids"] = new JsonArray(monitor.ZoomMeetingPids.Select(pid => (JsonNode?)JsonValue.Create(pid)).ToArray()),
            ["detectionError"] = monitor.Error,
            ["updatedAt"] = Timestamp(),
            ["meetingWindows"] = new JsonArray(monitor.Teams.Active.Order().Select(window => (JsonNode?)JsonValue.Create(window)).ToArray())
        };
        next["meetingId"] = meetingId;
        next["meetingStartedAt"] = meetingStartedAt;
        foreach (var key in new[] { "lastTranscript", "lastStoppedAt" })
            if (state[key] != null) next[key] = state[key]!.DeepClone();
        if (txProcess != null)
        {
            next["txPid"] = txProcess.Id;
            next["recordingStartedAt"] = recordingStarted?.ToString("o");
            next["speaker"] = speaker;
            next["mic"] = microphone;
            next["echoCancellation"] = echoCancellation;
        }
        if (extra != null)
            foreach (var entry in extra)
                if (entry.Value != null) next[entry.Key] = entry.Value.DeepClone();
        JsonFiles.Write(statePath, next);
        state = next;
    }

    private void StartTx()
    {
        if (MonotonicSeconds - lastRetry < 30) return;
        lastRetry = MonotonicSeconds;
        try
        {
            var tx = config.ResolveTx();
            (speaker, microphone) = AudioDevices.Resolve(config, tx);
            echoCancellation = AudioDevices.ShouldCancelEcho(config, microphone, Log);
            var arguments = AudioDevices.Arguments(config, speaker, microphone, echoCancellation);
            meetingId ??= Guid.NewGuid().ToString("N");
            meetingStartedAt ??= DateTimeOffset.UtcNow.ToString("o");
            var info = Processes.StartInfo(Environment.ProcessPath!, new[] { "exec-tx", tx }.Concat(arguments));
            info.Environment["TX_MEETING_ID"] = meetingId;
            info.Environment["TX_MEETING_STARTED_AT"] = meetingStartedAt;
            txLog = TextWriter.Synchronized(new StreamWriter(
                new FileStream(Path.Combine(config.TranscriptDirectory, ".tx.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true });
            txProcess = Process.Start(info) ?? throw new InvalidOperationException("Could not start tx.");
            logPump = Task.WhenAll(Pump(txProcess.StandardOutput, txLog), Pump(txProcess.StandardError, txLog));
            recordingStarted = DateTimeOffset.Now;
            meetingEnded = null;
            Log($"Meeting detected. Started tx (pid={txProcess.Id}, speaker={speaker}, mic={microphone}, echo_cancellation={echoCancellation}).");
            WriteState("recording");
        }
        catch (Exception ex)
        {
            // No recording process exists when device resolution or process creation fails.
            if (txProcess != null) throw;
            txLog?.Dispose();
            txLog = null;
            Log($"Failed to start tx: {ex.Message}");
            WriteState("error", new JsonObject { ["lastError"] = ex.Message });
        }
    }

    private static async Task Pump(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }

    private async Task<string?> StopTx(string reason)
    {
        if (txProcess == null) return null;
        var process = txProcess;
        Log($"Stopping tx (pid={process.Id}, reason={reason}).");
        foreach (var (signal, seconds) in new[] { (2, 20), (15, 5), (9, 5) })
        {
            if (process.HasExited) break;
            // The helper normally creates the group before tx starts. Fall back to
            // the child PID if shutdown arrives before setsid() has completed.
            if (Native.Kill(-process.Id, signal) != 0 && !process.HasExited) Native.Kill(process.Id, signal);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds)); }
            catch (TimeoutException) { }
        }
        if (!process.HasExited) throw new InvalidOperationException($"tx process {process.Id} did not stop.");
        try
        {
            if (logPump != null) await logPump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            txLog?.Dispose();
            txLog = null;
            process.Dispose();
            txProcess = null;
            logPump = null;
        }
        var transcript = LatestTranscript(config.TranscriptDirectory, recordingStarted?.AddSeconds(-10).UtcDateTime);
        recordingStarted = null;
        meetingEnded = null;
        Log($"tx stopped. Last transcript: {transcript ?? "<none found>"}");
        return transcript;
    }

    public static string? LatestTranscript(string directory, DateTime? minimumTime = null)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith('.') &&
                (minimumTime == null || File.GetLastWriteTimeUtc(path) >= minimumTime))
            .MaxBy(File.GetLastWriteTimeUtc);
    }

    public async Task Run(int? maxPolls)
    {
        using var watcherLock = new WatcherLock(Path.Combine(config.TranscriptDirectory, ".watcher.lock"));
        using var stop = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        Log($"Watcher started (poll={config.PollSeconds}s, grace={config.GraceSeconds}s, native AOT).");
        int polls = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                monitor.Poll();
                if (monitor.Error != null)
                {
                    Log(monitor.Error);
                    // A healthy detector can still start/retain a recording. If
                    // neither reports a call, don't treat a failed poll as an end.
                    if (!monitor.Active)
                    {
                        meetingEnded = null;
                        WriteState("error", new JsonObject { ["lastError"] = monitor.Error });
                        if (maxPolls != null && ++polls >= maxPolls) break;
                        await Task.Delay(TimeSpan.FromSeconds(config.PollSeconds), stop.Token);
                        continue;
                    }
                }

                if (txProcess?.HasExited == true)
                {
                    Log($"tx exited unexpectedly with code {txProcess.ExitCode}.");
                    var transcript = await StopTx("unexpected exit");
                    WriteState("error", new JsonObject { ["lastError"] = "tx exited unexpectedly; see .tx.log.", ["lastTranscript"] = transcript });
                }
                if (monitor.Active)
                {
                    meetingEnded = null;
                    if (txProcess == null) StartTx();
                    else WriteState("recording");
                }
                else if (txProcess != null)
                {
                    if (meetingEnded == null)
                    {
                        meetingEnded = MonotonicSeconds;
                        Log($"Meeting ended. Waiting {config.GraceSeconds}s before stopping tx.");
                    }
                    if (MonotonicSeconds - meetingEnded >= config.GraceSeconds)
                    {
                        var transcript = await StopTx("meeting grace elapsed");
                        meetingId = null;
                        meetingStartedAt = null;
                        WriteState("watching", new JsonObject { ["lastStoppedAt"] = Timestamp(), ["lastTranscript"] = transcript });
                    }
                    else WriteState("grace", new JsonObject
                    {
                        ["stopsAt"] = DateTimeOffset.Now.AddSeconds(config.GraceSeconds - (MonotonicSeconds - meetingEnded.Value)).ToString("o")
                    });
                }
                else
                {
                    meetingId = null;
                    meetingStartedAt = null;
                    WriteState("watching");
                }

                if (maxPolls != null && ++polls >= maxPolls) break;
                await Task.Delay(TimeSpan.FromSeconds(config.PollSeconds), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            var transcript = await StopTx("watcher shutdown");
            WriteState("stopped", new JsonObject
            {
                ["meetingWindows"] = new JsonArray(), ["zoomMeetingPids"] = new JsonArray(),
                ["lastStoppedAt"] = Timestamp(), ["lastTranscript"] = transcript
            });
            Log("Watcher stopped.");
        }
    }
}
