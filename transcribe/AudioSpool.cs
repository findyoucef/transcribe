using NAudio.Wave;

namespace Transcribe;

/// <summary>Persists captured PCM independently of the speed of transcription.</summary>
public sealed class AudioSpool : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly bool temporary;
    private readonly WaveFileWriter writer;
    private readonly FileStream reader;
    private readonly long dataOffset;
    private readonly int chunkBytes;
    private readonly long maxBytes;
    private long written, consumed;
    private bool complete, disposed;
    private Exception? error;

    public AudioSpool(string? path, WaveFormat format, long maxBytes = 2L * 1024 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        temporary = path == null;
        this.path = path ?? Path.Combine(Path.GetTempPath(), $"tx-{Guid.NewGuid():N}.wav");
        this.maxBytes = maxBytes;
        chunkBytes = Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 20 / format.BlockAlign * format.BlockAlign);
        var stream = new FileStream(this.path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        try
        {
            writer = new WaveFileWriter(stream, format);
            writer.Flush();
            dataOffset = stream.Position;
            reader = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            stream.Dispose();
            if (temporary) File.Delete(this.path);
            throw;
        }
    }

    public TimeSpan Backlog => TimeSpan.FromSeconds((Interlocked.Read(ref written) - Interlocked.Read(ref consumed)) / (double)writer.WaveFormat.AverageBytesPerSecond);

    public void Write(byte[] data)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (complete) throw new InvalidOperationException("Audio capture has already completed.");
            if (written + data.Length > maxBytes)
                throw new IOException($"Audio backup reached its {maxBytes} byte limit: {path}");
            if (data.Length % writer.WaveFormat.BlockAlign != 0)
                throw new ArgumentException("Audio must contain whole PCM frames.", nameof(data));
            writer.Write(data, 0, data.Length);
            // Keep the WAV header readable after an unexpected process exit.
            writer.Flush();
            written += data.Length;
            Monitor.PulseAll(gate);
        }
    }

    public IEnumerable<byte[]> ReadChunks()
    {
        while (true)
        {
            byte[] data;
            lock (gate)
            {
                while (consumed == written && !complete && !disposed) Monitor.Wait(gate);
                ObjectDisposedException.ThrowIf(disposed, this);
                if (consumed == written)
                {
                    if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    yield break;
                }
                data = new byte[(int)Math.Min(chunkBytes, written - consumed)];
                reader.Position = dataOffset + consumed;
                reader.ReadExactly(data);
                consumed += data.Length;
            }
            yield return data;
        }
    }

    public void Complete(Exception? error = null)
    {
        lock (gate)
        {
            if (complete) return;
            this.error = error;
            complete = true;
            Monitor.PulseAll(gate);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Monitor.PulseAll(gate);
            reader.Dispose();
            writer.Dispose();
            if (temporary) File.Delete(path);
        }
    }
}
