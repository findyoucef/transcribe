using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Transcribe
{
    public record TranscriptionItem(TimeSpan Start, TimeSpan End, string Text, string Source);

    public class TranscriptionMerger : IDisposable
    {
        private readonly BlockingCollection<TranscriptionItem> queue = new();
        private readonly List<TranscriptionItem> buffer = new();
        private readonly object writeLock = new();
        private readonly StreamWriter writer;
        private readonly TimeSpan flushWindow;
        private readonly CancellationTokenSource cts = new();
        private Task? worker;
        private int disposedFlag = 0;

        public TranscriptionMerger(string transcriptFilePath, TimeSpan? flushWindow = null)
        {
            var fs = new FileStream(transcriptFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(0, SeekOrigin.End);
            writer = new StreamWriter(fs) { AutoFlush = true };
            this.flushWindow = flushWindow ?? TimeSpan.FromSeconds(3);
            worker = Task.Run(WorkLoop);
        }

        public void Submit(IEnumerable<TranscriptionItem> items)
        {
            foreach (var item in items)
            {
                queue.Add(item);
            }
        }

        private async Task WorkLoop()
        {
            var lastFlush = DateTime.UtcNow;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    TranscriptionItem item;
                    if (queue.TryTake(out item, 100))
                    {
                        buffer.Add(item);
                    }

                    var now = DateTime.UtcNow;
                    if ((now - lastFlush) >= flushWindow && buffer.Count > 0)
                    {
                        FlushBuffer();
                        lastFlush = now;
                    }

                    await Task.Delay(10, cts.Token);
                }

                // Final flush
                if (buffer.Count > 0)
                {
                    FlushBuffer();
                }
            }
            catch
            {
                // swallow
            }
        }

        private void FlushBuffer()
        {
            var toWrite = buffer.OrderBy(i => i.Start).ToList();
            buffer.Clear();
            lock (writeLock)
            {
                foreach (var i in toWrite)
                {
                    writer.WriteLine($"{i.Start}->{i.End}: {i.Text}");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) == 1)
            {
                return; // already disposed
            }

            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            try { worker?.Wait(); } catch { }
            try
            {
                lock (writeLock)
                {
                    writer.Dispose();
                }
            }
            catch { }
            try { queue.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }
}
