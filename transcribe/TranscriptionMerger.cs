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
            if (worker?.IsFaulted == true) worker.GetAwaiter().GetResult();
            foreach (var item in items)
            {
                queue.Add(item);
            }
        }

        private void WorkLoop()
        {
            var lastFlush = DateTime.UtcNow;
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var item, 100))
                {
                    buffer.Add(item);
                }
                var now = DateTime.UtcNow;
                if ((now - lastFlush) >= flushWindow && buffer.Count > 0)
                {
                    FlushBuffer();
                    lastFlush = now;
                }
            }
            if (buffer.Count > 0) FlushBuffer();
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

            queue.CompleteAdding();
            try
            {
                worker?.GetAwaiter().GetResult();
            }
            finally
            {
                writer.Dispose();
                queue.Dispose();
            }
        }
    }
}
