using Whisper.net;
using System.Diagnostics;

namespace Transcribe
{
    public class WhisperStreamProcessor : IDisposable
    {
        private readonly WhisperFactory whisperFactory;
        private readonly WhisperProcessor processor;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Task? processingTask;
        private Task? visualizationTask;
        private readonly List<byte> audioBuffer;
        private readonly int chunkSizeBytes;
        private readonly IAudioRecorder recorder;
        private readonly StreamWriter transcriptWriter;
        private readonly string transcriptFileName;
        private readonly string? sourceLabel;
        private readonly bool showLevels;
        private Stopwatch? recordingClock;
        private readonly TranscriptionMerger? merger;
        private readonly Action<string>? onText;
        private readonly Action<string>? onStatus;
        private readonly bool emitConsoleOutput;

        public WhisperStreamProcessor(
            string modelPath,
            string language,
            IAudioRecorder recorder,
            int chunkSizeSeconds = TranscribeDefaults.DefaultChunkSeconds,
            string? sourceLabel = null,
            bool showLevels = true,
            string? transcriptFilePath = null,
            TranscriptionMerger? merger = null,
            Action<string>? onText = null,
            Action<string>? onStatus = null,
            bool emitConsoleOutput = true)
        {
            this.recorder = recorder;
            whisperFactory = WhisperFactory.FromPath(modelPath);
            var resolvedLanguage = TranscribeDefaults.NormalizeLanguage(language);
            processor = whisperFactory.CreateBuilder()
                .WithLanguage(resolvedLanguage)
                .Build();
            
            this.sourceLabel = sourceLabel;
            this.showLevels = showLevels;
            this.merger = merger;
            this.onText = onText;
            this.onStatus = onStatus;
            this.emitConsoleOutput = emitConsoleOutput;
            
            cancellationTokenSource = new CancellationTokenSource();
            audioBuffer = new List<byte>();
            
            // Compute chunk size in bytes based on recorder throughput
            var bytesPerSecond = recorder.GetBytesPerSecond();
            if (bytesPerSecond <= 0)
            {
                // Fallback to 16kHz mono 16-bit
                bytesPerSecond = 32000;
            }
            chunkSizeBytes = chunkSizeSeconds * bytesPerSecond;
            
            // Use provided transcript path or create a new GUID
            transcriptFileName = string.IsNullOrWhiteSpace(transcriptFilePath) ? $"{Guid.NewGuid():N}.txt" : transcriptFilePath;
            
            // If merger is not provided, create a private writer
            if (merger == null)
            {
                var fs = new FileStream(transcriptFileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                fs.Seek(0, SeekOrigin.End); // append
                transcriptWriter = new StreamWriter(fs) { AutoFlush = true };
            }
            else
            {
                transcriptWriter = TextWriter.Null as StreamWriter ?? new StreamWriter(Stream.Null);
            }
        }

        public void StartProcessing()
        {
            var message = $"Saving transcript to: {transcriptFileName}" + (sourceLabel != null ? $" [{sourceLabel}]" : string.Empty);
            if (emitConsoleOutput)
            {
                Console.WriteLine(message);
                Console.WriteLine();
            }
            else
            {
                onStatus?.Invoke(message);
            }
            
            // Start wall-clock timing
            recordingClock = Stopwatch.StartNew();
            
            processingTask = Task.Run(() => ProcessAudioLoop());
            if (showLevels)
            {
                visualizationTask = Task.Run(() => AudioVisualizationLoop());
            }
        }

        private async Task AudioVisualizationLoop()
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var level = recorder.GetCurrentAudioLevel();
                    var barLength = Math.Min((int)(level * 50), 50);
                    var bar = new string('=', barLength).PadRight(50);

                    if (emitConsoleOutput)
                    {
                        if (sourceLabel != null)
                        {
                            Console.WriteLine($"[{sourceLabel}] Audio level: [{bar}]");
                        }
                        else
                        {
                            Console.WriteLine($"Audio level: [{bar}]");
                        }
                    }
                    
                    await Task.Delay(500);
                }
                catch
                {
                    // Ignore visualization errors
                }
            }
        }

        private async Task ProcessAudioLoop()
        {
            try
            {
                foreach (var chunk in recorder.GetAudioChunks())
                {
                    if (cancellationTokenSource.Token.IsCancellationRequested)
                        break;

                    audioBuffer.AddRange(chunk);

                    int threshold = chunkSizeBytes;
                    int earlyThreshold = chunkSizeBytes; // default: no early processing

                    // Allow earlier processing only for microphone to reduce latency
                    if (recorder is MicrophoneRecorder)
                    {
                        earlyThreshold = Math.Max(1, chunkSizeBytes / 3);
                    }

                    if (audioBuffer.Count >= threshold)
                    {
                        var audioToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        var baseOffset = recordingClock?.Elapsed ?? TimeSpan.Zero;
                        await ProcessAudioChunk(audioToProcess, baseOffset);
                    }
                    else if (recorder is MicrophoneRecorder && audioBuffer.Count >= earlyThreshold)
                    {
                        var audioToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        var baseOffset = recordingClock?.Elapsed ?? TimeSpan.Zero;
                        await ProcessAudioChunk(audioToProcess, baseOffset);
                    }
                }

                if (audioBuffer.Count > 0)
                {
                    var baseOffset = recordingClock?.Elapsed ?? TimeSpan.Zero;
                    await ProcessAudioChunk(audioBuffer.ToArray(), baseOffset);
                    audioBuffer.Clear();
                }
            }
            catch (Exception ex)
            {
                var message = $"Processing error: {ex.Message}";
                if (emitConsoleOutput)
                {
                    Console.Error.WriteLine(message);
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                else
                {
                    onStatus?.Invoke(message);
                }
            }
        }

        private async Task ProcessAudioChunk(byte[] audioData, TimeSpan chunkBaseOffset)
        {
            try
            {
                using var wavStream = recorder.CreateWavStream(audioData);
                
                var transcriptionLines = new List<string>();
                var consoleText = new List<string>();
                var items = new List<TranscriptionItem>();
                
                await foreach (var result in processor.ProcessAsync(wavStream))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        var adjStart = chunkBaseOffset + result.Start;
                        var adjEnd = chunkBaseOffset + result.End;
                        transcriptionLines.Add($"{adjStart}->{adjEnd}: {result.Text.Trim()}");
                        consoleText.Add(result.Text.Trim());
                        items.Add(new TranscriptionItem(adjStart, adjEnd, result.Text.Trim(), sourceLabel ?? ""));
                    }
                }
                
                if (consoleText.Count > 0)
                {
                    var consoleOutput = string.Join(" ", consoleText);

                    var output = sourceLabel != null
                        ? $"[{sourceLabel}] {consoleOutput}"
                        : consoleOutput;

                    onText?.Invoke(output);
                    if (emitConsoleOutput)
                    {
                        Console.WriteLine(output);
                    }
                }
                
                if (items.Count > 0)
                {
                    if (merger != null)
                    {
                        merger.Submit(items);
                    }
                    else
                    {
                        foreach (var line in transcriptionLines)
                        {
                            transcriptWriter.WriteLine(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var message = $"Chunk processing error: {ex.Message}";
                if (emitConsoleOutput)
                {
                    Console.Error.WriteLine($"\n{message}");
                }
                else
                {
                    onStatus?.Invoke(message);
                }
            }
        }

        public async Task StopProcessing()
        {
            cancellationTokenSource.Cancel();
            if (processingTask != null)
            {
                await processingTask;
            }
            if (visualizationTask != null)
            {
                await visualizationTask;
            }
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            processingTask?.Wait();
            visualizationTask?.Wait();
            
            transcriptWriter?.Dispose();
            
            processor?.Dispose();
            whisperFactory?.Dispose();
            cancellationTokenSource.Dispose();
        }
    }
}
