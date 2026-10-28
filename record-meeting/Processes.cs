using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RecordMeeting;

internal static partial class Native
{
    [LibraryImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(int descriptor, int operation);
    [LibraryImport("libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);
    [LibraryImport("libSystem.B.dylib", EntryPoint = "setsid", SetLastError = true)]
    internal static partial int SetSid();
    [LibraryImport("libSystem.B.dylib", EntryPoint = "getuid")]
    internal static partial uint GetUid();
    [LibraryImport("libSystem.B.dylib", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Access(string path, int mode);
    [LibraryImport("libSystem.B.dylib", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ExecV(string path, IntPtr arguments);

    // Re-exec a fresh utility process so tx owns a session/process group. This
    // preserves SIGINT shutdown for tx and its descendants without fork() in .NET.
    internal static int ExecTx(string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("Missing tx executable.");
        if (SetSid() < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the tx session.");
        var strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, IntPtr.Zero);
            ExecV(arguments[0], argv);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not execute {arguments[0]}.");
        }
        finally
        {
            foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(argv);
        }
    }
}

internal sealed class WatcherLock : IDisposable
{
    private readonly FileStream stream;
    public WatcherLock(string path)
    {
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException ex) when (ex.HResult == 35) // Darwin EWOULDBLOCK
        {
            // FileStream also checks flock sharing, including the old Python watcher's lock.
            throw new InvalidOperationException("Another record-meeting watcher is running.", ex);
        }
        if (Native.Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
        {
            stream.Dispose();
            throw new InvalidOperationException("Another record-meeting watcher is running.");
        }
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.WriteLine(Environment.ProcessId);
    }
    // Keep the lock inode in place: unlinking it after unlock can race a new watcher.
    public void Dispose() => stream.Dispose();
}

internal static class Processes
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["LC_ALL"] = "C";
        return info;
    }

    public static Result Run(string executable, IEnumerable<string> arguments, int timeoutSeconds = 20, bool check = true)
    {
        using var process = Process.Start(StartInfo(executable, arguments))
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{executable} timed out after {timeoutSeconds} seconds.");
        }
        var result = new Result(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        if (check && result.ExitCode != 0)
            throw new InvalidOperationException($"{executable} exited with code {result.ExitCode}: {result.Error.Trim()} {result.Output.Trim()}");
        return result;
    }
}
