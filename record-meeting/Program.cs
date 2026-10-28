using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

[assembly: SupportedOSPlatform("macos")]

namespace RecordMeeting;

internal static class Program
{
    private const string Label = "com.baouchiy.record-meeting";
    private static string Domain => $"gui/{Native.GetUid()}";
    private static string Service => $"{Domain}/{Label}";
    private static string PlistPath => Path.Combine(Configuration.Home, "Library/LaunchAgents", Label + ".plist");

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("record-meeting requires macOS.");
            if (args.FirstOrDefault() == "exec-tx") return Native.ExecTx(args[1..]);
            var command = "status";
            var configPath = Configuration.DefaultPath;
            int? maxPolls = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config" when i + 1 < args.Length: configPath = Configuration.ExpandPath(args[++i]); break;
                    case "--max-polls" when i + 1 < args.Length: maxPolls = int.Parse(args[++i]); break;
                    case "--probe": command = "detect"; break;
                    case "--self-test": command = "self-test"; break;
                    case "--help" or "-h": command = "help"; break;
                    case "--version": command = "version"; break;
                    default:
                        if (i == 0 && !args[i].StartsWith('-')) command = args[i];
                        else throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
                        break;
                }
            }
            if (maxPolls is <= 0) throw new ArgumentException("--max-polls must be positive.");
            if (command == "help")
            {
                Console.WriteLine("record-meeting-watcher [status|install|uninstall|start|stop|restart|watch|detect|devices|check|logs|latest|config|self-test] [--config FILE]");
                Console.WriteLine("watch accepts --max-polls N for bounded checks. Published Native AOT executable required for watch/install.");
                return 0;
            }
            if (command == "version")
            {
                Console.WriteLine($"record-meeting-watcher 1.0.0 ({(RuntimeFeature.IsDynamicCodeSupported ? "managed development build" : "Native AOT")})");
                return 0;
            }
            if (command == "self-test") return await SelfTest.Run();
            if (command == "config") { Console.WriteLine(configPath); return 0; }
            // Service recovery must still work when the recording config is malformed.
            if (command == "start") { Start(); return 0; }
            if (command == "restart") { Stop(); Start(); return 0; }
            if (command == "stop") { Stop(); return 0; }
            if (command == "uninstall")
            {
                Stop();
                Launchctl(["disable", Service], check: false);
                File.Delete(PlistPath);
                Console.WriteLine("Uninstalled. Configuration and transcripts were preserved.");
                return 0;
            }
            if (command == "status") { Status(configPath); return 0; }
            var config = Configuration.Load(configPath);
            switch (command)
            {
                case "watch":
                    RequireNative();
                    await new MeetingWatcher(config).Run(maxPolls);
                    break;
                case "install": Install(configPath, config); break;
                case "detect": Detect(config); break;
                case "devices":
                    var tx = config.ResolveTx();
                    Console.WriteLine("Speaker/output devices:\n" + Processes.Run(tx, ["list-speakers"]).Output);
                    Console.WriteLine("Microphone devices:\n" + Processes.Run(tx, ["list-devices"]).Output);
                    break;
                case "check": Check(config); break;
                case "logs":
                    foreach (var name in new[] { ".watcher.log", ".tx.log", ".launchd.err.log" })
                    {
                        var path = Path.Combine(config.TranscriptDirectory, name);
                        if (!File.Exists(path)) continue;
                        Console.WriteLine($"\n==> {path} <==");
                        foreach (var line in File.ReadLines(path).TakeLast(40)) Console.WriteLine(line);
                    }
                    break;
                case "latest":
                    var last = JsonFiles.ReadState(Path.Combine(config.TranscriptDirectory, ".watcher-state.json"))["lastTranscript"]?.GetValue<string>();
                    var latest = last != null && File.Exists(last) ? last : MeetingWatcher.LatestTranscript(config.TranscriptDirectory);
                    if (latest == null) throw new FileNotFoundException("No transcript found.");
                    Console.WriteLine(latest);
                    break;
                default: throw new ArgumentException($"Unknown command: {command}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void RequireNative()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Publish the Native AOT executable before installing or running the watcher.");
    }

    private static Processes.Result Launchctl(string[] args, bool check = true) =>
        Processes.Run("/bin/launchctl", args, 50, check);

    private static bool ServiceLoaded() => Launchctl(["print", Service], check: false).ExitCode == 0;

    private static void Install(string configPath, Configuration config)
    {
        RequireNative();
        var tx = config.ResolveTx();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(config.TranscriptDirectory);
        if (!File.Exists(configPath))
        {
            var defaults = Configuration.Defaults();
            defaults["tx_bin"] = tx;
            JsonFiles.Write(configPath, defaults);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", Label),
                new XElement("key", "ProgramArguments"), new XElement("array",
                    new XElement("string", Environment.ProcessPath), new XElement("string", "watch"),
                    new XElement("string", "--config"), new XElement("string", configPath)),
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "KeepAlive"), new XElement("true"),
                new XElement("key", "ExitTimeOut"), new XElement("integer", 40),
                new XElement("key", "ProcessType"), new XElement("string", "Background"),
                new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(config.TranscriptDirectory, ".launchd.out.log")),
                new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(config.TranscriptDirectory, ".launchd.err.log")))));
        plist.Save(PlistPath);
        File.SetUnixFileMode(PlistPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Stop();
        Launchctl(["enable", Service]);
        Launchctl(["bootstrap", Domain, PlistPath]);
        Launchctl(["kickstart", Service]);
        Console.WriteLine($"Installed and started: {Label}\nConfiguration: {configPath}\nTranscripts: {config.TranscriptDirectory}");
    }

    private static void Start()
    {
        if (!File.Exists(PlistPath)) throw new FileNotFoundException("The watcher is not installed. Run install first.");
        Launchctl(["enable", Service]);
        if (!ServiceLoaded()) Launchctl(["bootstrap", Domain, PlistPath]);
        Launchctl(["kickstart", Service]);
        Console.WriteLine($"Started: {Label}");
    }

    private static void Stop()
    {
        var service = Launchctl(["print", Service], check: false);
        if (service.ExitCode == 0)
        {
            var match = Regex.Match(service.Output, @"(?m)^\s*pid = (\d+)");
            Process? previous = null;
            if (match.Success)
            {
                try { previous = Process.GetProcessById(int.Parse(match.Groups[1].Value)); }
                catch (ArgumentException) { } // It exited before we could attach.
            }
            using (previous)
            {
                Launchctl(["bootout", Service]);
                // bootout can return before shutdown finishes. Wait for the old
                // watcher to release its lock before bootstrapping its replacement.
                if (previous != null && !previous.WaitForExit(40_000))
                    throw new TimeoutException("The previous watcher has not finished shutting down.");
            }
            Console.WriteLine($"Stopped for this login session: {Label}");
        }
        else Console.WriteLine("Watcher is already stopped.");
    }

    private static void Status(string configPath)
    {
        var service = Launchctl(["print", Service], check: false);
        Console.WriteLine(service.ExitCode == 0 ? "Launch agent: loaded" : "Launch agent: not loaded");
        if (service.ExitCode == 0)
            foreach (var line in service.Output.Split('\n').Where(line => line.Contains("state =") || line.Contains("pid =") || line.Contains("last exit code =")))
                Console.WriteLine("  " + line.Trim());
        Console.WriteLine($"Configuration: {configPath}");
        try
        {
            var config = Configuration.Load(configPath);
            var state = JsonFiles.ReadState(Path.Combine(config.TranscriptDirectory, ".watcher-state.json"));
            Console.WriteLine(state.Count > 0 ? "Watcher state:\n" + JsonFiles.Text(state) : "Watcher state: unavailable");
        }
        catch (Exception ex) { Console.WriteLine($"Configuration error: {ex.Message}"); }
    }

    private static void Detect(Configuration config)
    {
        var monitor = new MeetingMonitor(config);
        monitor.Poll();
        Console.WriteLine(JsonFiles.Text(new JsonObject
        {
            ["meetingActive"] = monitor.Active,
            ["detectionSource"] = monitor.DetectionSource,
            ["detectionError"] = monitor.Error,
            ["activeMeetingWindows"] = new JsonArray(monitor.Teams.Active.Order().Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["teamsLog"] = monitor.Teams.CurrentPath,
            ["zoomDetectionEnabled"] = config.ZoomDetection,
            ["zoomMeetingPids"] = new JsonArray(monitor.ZoomMeetingPids.Select(pid => (JsonNode?)JsonValue.Create(pid)).ToArray()),
            ["teamsRunning"] = Processes.Run("/usr/bin/pgrep", ["-x", "MSTeams"], check: false).ExitCode == 0
        }));
    }

    private static void Check(Configuration config)
    {
        var tx = config.ResolveTx();
        var (speaker, mic) = AudioDevices.Resolve(config, tx);
        var echo = AudioDevices.ShouldCancelEcho(config, mic, Console.Error.WriteLine);
        Console.WriteLine(JsonFiles.Text(new JsonObject
        {
            ["txBin"] = tx, ["speaker"] = speaker, ["mic"] = mic, ["echoCancellation"] = echo,
            ["arguments"] = new JsonArray(AudioDevices.Arguments(config, speaker, mic, echo).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        }));
    }
}
