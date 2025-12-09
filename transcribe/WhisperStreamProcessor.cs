using Whisper.net;
using Whisper.net.Ggml;
using System.Collections.Concurrent;

namespace Transcribe
{
    public class WhisperStreamProcessor : IDisposable
    {
        private readonly WhisperFactory whisperFactory;
        private readonly WhisperProcessor processor;
        private readonly BlockingCollection<byte[]> processingQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Task? processingTask;
        private Task? visualizationTask;
        private readonly List<byte> audioBuffer;
        private readonly int chunkSizeBytes;
        private readonly IAudioRecorder recorder;
        private int audioLevelLine;
        private readonly StreamWriter transcriptWriter;
        private readonly string transcriptFileName;

        public WhisperStreamProcessor(string modelPath, string language, IAudioRecorder recorder, int chunkSizeSeconds = 3)
        {
            this.recorder = recorder;
            whisperFactory = WhisperFactory.FromPath(modelPath);
            processor = whisperFactory.CreateBuilder()
                .WithLanguage(language)
                .Build();
            
            processingQueue = new BlockingCollection<byte[]>();
            cancellationTokenSource = new CancellationTokenSource();
            audioBuffer = new List<byte>();
            
            // 16kHz, 16-bit, mono = 32000 bytes/second
            chunkSizeBytes = chunkSizeSeconds * 32000;
            
            // Create transcript file with GUID name
            transcriptFileName = $"{Guid.NewGuid():N}.txt";
            transcriptWriter = new StreamWriter(transcriptFileName, append: true) 
            { 
                AutoFlush = true 
            };
        }

        public void StartProcessing()
        {
            Console.WriteLine($"Saving transcript to: {transcriptFileName}");
            Console.WriteLine();
            
            // Capture the current line as the audio level line AFTER printing other messages
            audioLevelLine = Console.CursorTop;
            Console.WriteLine("Audio level: [                                                  ]");
            Console.WriteLine();
            
            processingTask = Task.Run(() => ProcessAudioLoop());
            visualizationTask = Task.Run(() => AudioVisualizationLoop());
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
                    
                    var savedPos = Console.GetCursorPosition();
                    Console.SetCursorPosition(0, audioLevelLine);
                    Console.Write($"Audio level: [{bar}]");
                    
                    // Only restore position if we're not on the audio level line
                    if (savedPos.Top != audioLevelLine)
                    {
                        Console.SetCursorPosition(savedPos.Left, savedPos.Top);
                    }
                    
                    await Task.Delay(50);
                }
                catch
                {
                    // Ignore cursor positioning errors
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

                    // Process when we have enough data
                    if (audioBuffer.Count >= chunkSizeBytes)
                    {
                        var audioToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();

                        await ProcessAudioChunk(audioToProcess);
                    }
                }

                // Process any remaining audio
                if (audioBuffer.Count > 0)
                {
                    await ProcessAudioChunk(audioBuffer.ToArray());
                    audioBuffer.Clear();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Processing error: {ex.Message}");
                Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private async Task ProcessAudioChunk(byte[] audioData)
        {
            try
            {
                using var wavStream = recorder.CreateWavStream(audioData);
                
                var transcriptionLines = new List<string>();
                var consoleText = new List<string>();
                
                await foreach (var result in processor.ProcessAsync(wavStream))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        // For file: include timestamp
                        transcriptionLines.Add($"{result.Start}->{result.End}: {result.Text.Trim()}");
                        
                        // For console: just the text
                        consoleText.Add(result.Text.Trim());
                    }
                }
                
                if (consoleText.Count > 0)
                {
                    // Write to console (without timestamps)
                    var consoleOutput = string.Join(" ", consoleText);
                    Console.WriteLine(consoleOutput);
                    
                    // Write to file (with timestamps)
                    foreach (var line in transcriptionLines)
                    {
                        transcriptWriter.WriteLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\nChunk processing error: {ex.Message}");
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
            processingQueue.Dispose();
            cancellationTokenSource.Dispose();
        }
    }
}
