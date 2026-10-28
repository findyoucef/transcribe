using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace RecordMeeting;

internal static class SelfTest
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed: " + message);
    }

    private static string MeetingStarted(string id) => $"Window: (windowNumber={id}; name=test; tags=) Setting close prompt from web: meeting\n";
    private static string MeetingDisposed(string id) => $"Window: (windowNumber={id}; name=test; tags=) DoDispose()\n";
    private const string MiniWindowLifecycle = """
        WebViewWindow: Changing to windowNumber=43,331; name=mini; tags=CallMonitor
        Window: (windowNumber=43331; name=mini; tags=CallMonitor) DoDispose()
        """ + "\n";

    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "record-meeting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "config.json");
            var values = Configuration.Defaults();
            values["transcript_dir"] = Path.Combine(root, "transcripts with spaces");
            values["teams_log_dir"] = Path.Combine(root, "logs");
            values.Remove("zoom_detection");
            JsonFiles.Write(configPath, values);
            Require(Configuration.Load(configPath).ZoomDetection, "Zoom detection enabled for existing configurations");
            values["zoom_detection"] = false; // Never let a real meeting affect synthetic lifecycle tests.
            JsonFiles.Write(configPath, values);
            var config = Configuration.Load(configPath);
            Directory.CreateDirectory(config.TranscriptDirectory);
            var transcriptFixture = Path.Combine(config.TranscriptDirectory, "latest.txt");
            var audioFixture = Path.Combine(config.TranscriptDirectory, "latest.wav");
            File.WriteAllText(transcriptFixture, "transcript");
            File.WriteAllText(audioFixture, "audio");
            File.SetLastWriteTimeUtc(audioFixture, DateTime.UtcNow.AddSeconds(1));
            Require(MeetingWatcher.LatestTranscript(config.TranscriptDirectory) == transcriptFixture, "audio backups must not be selected as transcripts");
            File.Delete(transcriptFixture);
            File.Delete(audioFixture);
            Require(config.EchoCancellation == null && config.GraceSeconds == 60, "existing config defaults");
            values["echo_cancellation"] = "false";
            JsonFiles.Write(configPath, values);
            bool rejected = false;
            try { Configuration.Load(configPath); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "string false must not enable cancellation");
            values["echo_cancellation"] = true;
            JsonFiles.Write(configPath, values);
            Require(Configuration.Load(configPath).EchoCancellation == true, "explicit cancellation override");
            Require(!AudioDevices.ShouldCancelEcho(Configuration.Load(configPath), -1, _ => { }), "disabled microphone");

            Directory.CreateDirectory(config.TeamsLogDirectory);
            var log = Path.Combine(config.TeamsLogDirectory, "MSTeams_first.log");
            File.WriteAllText(log, MeetingStarted("2,188") + MeetingStarted("2196").TrimEnd('\n'));
            var monitor = new TeamsLogMonitor(config.TeamsLogDirectory);
            monitor.Poll();
            Require(monitor.Active.SetEquals(["2188"]), "incomplete log line must wait for its newline");
            File.AppendAllText(log, "\n" + MeetingDisposed("2188"));
            monitor.Poll();
            Require(monitor.Active.SetEquals(["2196"]), "incremental overlapping call lifecycle");

            File.AppendAllText(log, MiniWindowLifecycle
                + "Window: (windowNumber=9; name=chat; tags=Main) Setting close prompt from web: send\n"
                + "WebViewWindow: Changing to windowNumber=10; name=prejoin; tags=\n");
            monitor.Poll();
            Require(monitor.Active.SetEquals(["2196"]), "mini window, draft chat, and prejoin do not change the meeting");

            // Rotation can happen between polls, after a final event in the old log.
            File.AppendAllText(log, MeetingDisposed("2196") + MeetingStarted("3"));
            var rotated = Path.Combine(config.TeamsLogDirectory, "MSTeams_second.log");
            File.WriteAllText(rotated, "ordinary log entry\n");
            monitor.Poll();
            Require(monitor.Active.SetEquals(["3"]) && monitor.CurrentPath == rotated,
                "rotation reads the old log tail and preserves the active meeting");
            var restarted = new TeamsLogMonitor(config.TeamsLogDirectory);
            restarted.Poll();
            Require(restarted.Active.SetEquals(["3"]), "watcher restart recovers the meeting from older logs");
            File.AppendAllText(rotated, "Window: (windowNumber=3; name=test; tags=) Setting close prompt from web: std::nullopt\n");
            monitor.Poll();
            Require(monitor.Active.Count == 0, "clearing the meeting prompt ends the meeting without closing its window");

            File.AppendAllText(rotated, MeetingStarted("4")
                + "Window: (windowNumber=4; name=test; tags=) Resetting close prompt from web\n");
            monitor.Poll();
            Require(monitor.Active.Count == 0, "resetting the meeting prompt ends the meeting");
            File.AppendAllText(rotated, MeetingStarted("5")
                + "AppDelegate: ElapsedTimeSinceProcessStart: 4,720 ms\n");
            monitor.Poll();
            Require(monitor.Active.Count == 0, "Teams restart clears an orphaned meeting from the previous process");
            File.AppendAllText(rotated, MeetingStarted("6"));
            monitor.Poll();
            File.WriteAllText(rotated, "truncated\n");
            monitor.Poll();
            Require(monitor.Active.Count == 0, "truncated logs reset detection");
            File.Delete(log);
            File.Delete(rotated);
            monitor.Poll();
            Require(monitor.Active.Count == 0 && monitor.CurrentPath == null, "missing logs clear active calls");
            File.WriteAllText(log, MiniWindowLifecycle);
            monitor.Poll();
            Require(monitor.Active.Count == 0, "a mini window alone never starts a recording");
            File.Delete(log);

            var zoomProcesses = """
                100 1 /Applications/zoom.us.app/Contents/MacOS/zoom.us
                101 100 /Applications/zoom.us.app/Contents/Frameworks/caphost.app/Contents/MacOS/caphost
                200 1 /Users/test/Applications/Zoom Workplace.app/Contents/MacOS/zoom.us
                300 1 /Applications/Other.app/Contents/MacOS/Other
                """ + "\n";
            Require(ZoomProcessMonitor.Parse(zoomProcesses).Length == 0, "idle Zoom and background helpers are not meetings");
            zoomProcesses += "102 100 /Applications/zoom.us.app/Contents/Frameworks/CptHost.app/Contents/MacOS/CptHost\n"
                + "201 200 /Users/test/Applications/Zoom Workplace.app/Contents/Frameworks/cpthost.app/Contents/MacOS/CptHost\n"
                + "301 300 /Applications/zoom.us.app/Contents/Frameworks/CptHost.app/Contents/MacOS/CptHost\n"
                + "302 999 /Applications/zoom.us.app/Contents/Frameworks/CptHost.app/Contents/MacOS/CptHost\n"
                + "303 200 /Applications/zoom.us.app/Contents/Frameworks/CptHost.app/Contents/MacOS/CptHost\n"
                + "304 100 /tmp/CptHost\n";
            Require(ZoomProcessMonitor.Parse(zoomProcesses).SequenceEqual([102, 201]),
                "only meeting helpers parented by the same Zoom bundle count, including paths with spaces");
            Require(ZoomProcessMonitor.Parse("").Length == 0, "Zoom exit clears meetings");
            var combined = new MeetingMonitor(config);
            combined.Poll();
            Require(!combined.Active && combined.ZoomMeetingPids.Length == 0 && combined.Error == null,
                "disabled Zoom ignores real meetings");
            File.WriteAllText(log, MeetingStarted("4"));
            combined.Poll();
            Require(combined.Active, "Teams detection still works with Zoom disabled");
            File.Delete(log);
            combined.Poll();
            Require(!combined.Active, "combined detection resets after the last call ends");

            var devices = AudioDevices.Parse("Available devices:\n  [0] System Audio (Native) (1ch)\n  [7] MacBook Pro Microphone (1ch)\n");
            Require(devices[0] == "System Audio (Native)", "parse native source without channel suffix");
            Require(AudioDevices.Select(devices, null, "WH-1000XM5", "microphone", "MacBook Pro Microphone") == 7, "headset fallback");
            rejected = false;
            try { AudioDevices.Select(devices, 3, "System Audio (Native)", "speaker"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "stale numeric devices must fail instead of silently selecting a new input");
            foreach (var (source, transport, expected) in new[]
            {
                ("MacBook Pro Speakers", "builtin", true), ("Headphones", "builtin", false),
                ("WH-1000XM5", "bluetooth", false), ("Speakers", "virtual", false), ("Default", "usb", false)
            })
            {
                var report = new JsonObject
                {
                    ["SPAudioDataType"] = new JsonArray(new JsonObject
                    {
                        ["_items"] = new JsonArray(
                            new JsonObject { ["coreaudio_output_source"] = "Speakers", ["coreaudio_device_transport"] = "coreaudio_device_type_builtin" },
                            new JsonObject
                            {
                                ["_name"] = source, ["coreaudio_output_source"] = source,
                                ["coreaudio_device_transport"] = "coreaudio_device_type_" + transport,
                                ["coreaudio_default_audio_output_device"] = "spaudio_yes"
                            })
                    })
                };
                Require(AudioDevices.BuiltInSpeakers(report, _ => { }) == expected, "echo selection for " + source);
            }
            var lockPath = Path.Combine(root, "test.lock");
            using (var held = new WatcherLock(lockPath))
            {
                rejected = false;
                try { using var duplicate = new WatcherLock(lockPath); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "second watcher must not acquire the lock");
            }
            using (var released = new WatcherLock(lockPath)) { }
            Console.WriteLine("PASS configuration, Teams logs, Zoom process detection, device selection, auto AEC, and lock exclusion.");

            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                await Lifecycle(root, configPath, values);
                File.WriteAllText(configPath, "invalid json");
                var status = Processes.Run(Environment.ProcessPath!, ["status", "--config", configPath]);
                Require(status.Output.Contains("Launch agent:") && status.Output.Contains("Configuration error:"),
                    "service diagnostics must remain available with malformed config");
            }
            else Console.WriteLine("Publish Native AOT and rerun self-test to exercise child-process lifecycle.");
            Console.WriteLine("record-meeting-watcher self-test passed.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Until(Func<bool> condition, string failure, int seconds = 10)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) return;
            await Task.Delay(40);
        }
        throw new InvalidOperationException("Self-test timed out: " + failure);
    }

    private static async Task Lifecycle(string root, string configPath, JsonObject values)
    {
        var fakeTx = Path.Combine(root, "fake tx");
        File.WriteAllText(fakeTx, """
            #!/bin/sh
            case "$1" in
              list-speakers) printf '  [0] System Audio (Native) (1ch)\n'; exit 0 ;;
              list-devices) printf '  [7] MacBook Pro Microphone (1ch)\n'; exit 0 ;;
            esac
            shift
            output=''
            echo_enabled=false
            while [ "$#" -gt 0 ]; do
              case "$1" in
                --output-dir) output="$2"; shift ;;
                --echo-cancellation) echo_enabled=true ;;
              esac
              shift
            done
            printf 'started\n' >> "$output/.starts"
            if [ -f "$output/.exit-immediately" ]; then exit 3; fi
            trap 'printf "Synthetic transcript flushed\n" > "$output/session.txt"; exit 0' INT
            trap 'touch "$output/.terminated"; exit 2' TERM
            printf '%s\n' "$echo_enabled" > "$output/.echo"
            printf '%s\n' "$$" > "$output/.ready"
            printf '%s\n' "$TX_MEETING_ID" > "$output/.meeting-id"
            printf 'fixture stdout\n'
            printf 'fixture stderr\n' >&2
            while :; do sleep 1; done
            """ + "\n");
        File.SetUnixFileMode(fakeTx, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        values["tx_bin"] = fakeTx;
        values["poll_seconds"] = 0.1;
        values["grace_seconds"] = 0.6;
        values["echo_cancellation"] = true;
        JsonFiles.Write(configPath, values);
        var config = Configuration.Load(configPath);
        var log = Path.Combine(config.TeamsLogDirectory, "MSTeams_lifecycle.log");
        File.WriteAllText(log, "idle\n");
        var statePath = Path.Combine(config.TranscriptDirectory, ".watcher-state.json");
        JsonObject State() => JsonFiles.ReadState(statePath);
        string? Status() => State()["status"]?.GetValue<string>();
        using var watcher = Process.Start(Processes.StartInfo(Environment.ProcessPath!, ["watch", "--config", configPath]))!;
        try
        {
            await Until(() => Status() == "watching", "initial watcher state");
            var duplicate = Processes.Run(Environment.ProcessPath!, ["watch", "--config", configPath, "--max-polls", "1"], check: false);
            Require(duplicate.ExitCode != 0 && duplicate.Error.Contains("Another record-meeting watcher"), "duplicate native process exclusion");
            File.AppendAllText(log, MeetingStarted("1"));
            await Until(() => Status() == "recording" && File.Exists(Path.Combine(config.TranscriptDirectory, ".ready")), "automatic capture start");
            int txPid = State()["txPid"]!.GetValue<int>();
            Require(Guid.TryParse(File.ReadAllText(Path.Combine(config.TranscriptDirectory, ".meeting-id")).Trim(), out _), "recorder receives meeting ID for restart continuity");
            var meetingId = State()["meetingId"]!.GetValue<string>();
            Native.Kill(-txPid, 9);
            await Until(() => Status() == "error", "capture failure becomes visible");
            await Until(() => Status() == "recording" && State()["txPid"]!.GetValue<int>() != txPid,
                "capture restarts while the meeting remains active", 40);
            Require(State()["meetingId"]!.GetValue<string>() == meetingId, "recorder restart preserves the meeting transcript");
            txPid = State()["txPid"]!.GetValue<int>();
            var group = Processes.Run("/bin/ps", ["-o", "pgid=", "-p", txPid.ToString()]).Output.Trim();
            Require(group == txPid.ToString(), "tx must own its process group");
            Require(File.ReadAllText(Path.Combine(config.TranscriptDirectory, ".echo")).Trim() == "true", "AEC flag forwarding");
            File.AppendAllText(log, MiniWindowLifecycle);
            log = Path.Combine(config.TeamsLogDirectory, "MSTeams_rotated.log");
            File.WriteAllText(log, "ordinary log entry\n");
            await Until(() => State()["teamsLog"]?.GetValue<string>() == log, "watcher follows rotated log");
            Require(Status() == "recording" && State()["txPid"]!.GetValue<int>() == txPid,
                "mini window closure and log rotation retain the same recording");
            File.AppendAllText(log, MeetingDisposed("1"));
            await Until(() => Status() == "grace", "meeting grace period");
            File.AppendAllText(log, MeetingStarted("2"));
            await Until(() => Status() == "recording", "meeting resumed during grace");
            Require(State()["txPid"]!.GetValue<int>() == txPid, "grace must retain the same recording");
            File.AppendAllText(log, MeetingDisposed("2"));
            await Until(() => Status() == "watching" && State()["lastTranscript"] != null, "grace shutdown and transcript flush");
            Require(!File.Exists(Path.Combine(config.TranscriptDirectory, ".terminated")), "normal stop must use SIGINT");
            var txLog = File.ReadAllText(Path.Combine(config.TranscriptDirectory, ".tx.log"));
            Require(txLog.Contains("fixture stdout") && txLog.Contains("fixture stderr"), "both child output streams must be logged");

            // A failed transcriber must not cause an immediate respawn loop.
            // The first recorder has already stopped, so allow a fresh test watcher.
            Native.Kill(watcher.Id, 15);
            await watcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(watcher.ExitCode == 0 && Status() == "stopped", "idle SIGTERM shutdown");
            File.WriteAllText(Path.Combine(config.TranscriptDirectory, ".exit-immediately"), "");
            File.AppendAllText(log, MeetingStarted("3"));
            using (var failing = Process.Start(Processes.StartInfo(Environment.ProcessPath!, ["watch", "--config", configPath]))!)
            {
                try
                {
                    await Until(() => Status() == "error", "child failure detection");
                    var starts = File.ReadAllLines(Path.Combine(config.TranscriptDirectory, ".starts")).Length;
                    await Task.Delay(400);
                    Require(File.ReadAllLines(Path.Combine(config.TranscriptDirectory, ".starts")).Length == starts, "failed child retry throttle");
                }
                finally
                {
                    Native.Kill(failing.Id, 15);
                    await failing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            File.Delete(Path.Combine(config.TranscriptDirectory, ".exit-immediately"));
            File.Delete(Path.Combine(config.TranscriptDirectory, ".ready"));
            using var active = Process.Start(Processes.StartInfo(Environment.ProcessPath!, ["watch", "--config", configPath]))!;
            try
            {
                await Until(() => Status() == "recording" && File.Exists(Path.Combine(config.TranscriptDirectory, ".ready")), "recording restart");
                txPid = State()["txPid"]!.GetValue<int>();
                Native.Kill(active.Id, 15);
                await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(active.ExitCode == 0 && Native.Kill(txPid, 0) != 0, "watcher shutdown must reap tx");
                Require(Status() == "stopped" && State()["lastTranscript"] != null, "shutdown preserves last transcript");
                Require(!File.Exists(Path.Combine(config.TranscriptDirectory, ".terminated")), "active shutdown must flush through SIGINT");
            }
            finally
            {
                if (!active.HasExited) { Native.Kill(active.Id, 15); await active.WaitForExitAsync(); }
            }
            Console.WriteLine("PASS native process lifecycle: call start, grace/resume, SIGINT flush, SIGTERM cleanup, failure throttle, and history preservation.");
        }
        finally
        {
            if (!watcher.HasExited) { Native.Kill(watcher.Id, 15); await watcher.WaitForExitAsync(); }
        }
    }
}
