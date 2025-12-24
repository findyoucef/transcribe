using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.Wave; // For WaveFileWriter helper if needed, though we can write raw headers

namespace Transcribe.PortAudio
{
    public class PortAudioRecorder : IAudioRecorder
    {
        private IntPtr stream = IntPtr.Zero;
        private readonly int deviceIndex;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly bool isLoopback;
        private readonly BlockingCollection<byte[]> audioChunks = new();
        private bool isRecording;
        private CancellationTokenSource? cts;
        private Task? captureTask;
        private float currentLevel;
        private bool disposed;

        public PortAudioRecorder(int deviceIndex, int sampleRate = 16000, int channels = 1, bool isLoopback = false)
        {
            this.deviceIndex = deviceIndex;
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.isLoopback = isLoopback;

            var err = PortAudioNative.Pa_Initialize();
            if (err != PortAudioNative.PaError.paNoError)
            {
                throw new Exception($"PortAudio initialization failed: {err}");
            }
        }

        public bool IsMicrophone => !isLoopback;

        public static IEnumerable<AudioDeviceInfo> GetDevices(bool input = true)
        {
            var list = new List<AudioDeviceInfo>();
            var err = PortAudioNative.Pa_Initialize();
            if (err != PortAudioNative.PaError.paNoError)
            {
                return list;
            }

            try
            {
                int count = PortAudioNative.Pa_GetDeviceCount();
                if (count < 0) return list;

                for (int i = 0; i < count; i++)
                {
                    var infoPtr = PortAudioNative.Pa_GetDeviceInfo(i);
                    if (infoPtr == IntPtr.Zero) continue;

                    var info = Marshal.PtrToStructure<PortAudioNative.PaDeviceInfo>(infoPtr);
                    string name = Marshal.PtrToStringAnsi(info.name) ?? "Unknown";

                    // Filter logic
                    if (input)
                    {
                        if (info.maxInputChannels > 0)
                        {
                            list.Add(new AudioDeviceInfo(i, name, info.maxInputChannels));
                        }
                    }
                    else
                    {
                        // For "speakers", we look for devices we can READ from that might be loopbacks
                        if (info.maxInputChannels > 0)
                        {
                            bool likelyLoopback = name.Contains("Monitor", StringComparison.OrdinalIgnoreCase) 
                                                  || name.Contains("BlackHole", StringComparison.OrdinalIgnoreCase)
                                                  || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

                            if (likelyLoopback) // Strict filtering for speakers list to avoid clutter?
                            {
                                // We'll assume users want "likely" system audio sources.
                                // If we want ALL inputs, we can just return all. 
                                // But for "Speaker" dropdown, we prefer loopbacks.
                                list.Add(new AudioDeviceInfo(i, $"{name} (Loopback)", info.maxInputChannels));
                            }
                            else
                            {
                                // Optionally include all inputs if specific loopback detection is hard?
                                // For now, let's include everything but mark non-loopbacks distinctively? 
                                // No, better to just list everything valid as an input, as PortAudio "output capture" is just an input.
                                list.Add(new AudioDeviceInfo(i, name, info.maxInputChannels));
                            }
                        }
                    }
                }
            }
            finally
            {
                PortAudioNative.Pa_Terminate();
            }
            return list;
        }

        public static void ListDevices(bool input = true)
        {
            var err = PortAudioNative.Pa_Initialize();
            if (err != PortAudioNative.PaError.paNoError)
            {
                Console.WriteLine($"PortAudio initialization failed: {err}");
                return;
            }

            try
            {
                int count = PortAudioNative.Pa_GetDeviceCount();
                if (count < 0)
                {
                    Console.WriteLine($"PortAudio error getting device count: {(PortAudioNative.PaError)count}");
                    return;
                }

                Console.WriteLine($"Available {(input ? "input" : "output/loopback")} devices (via PortAudio):");
                Console.WriteLine();

                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    var infoPtr = PortAudioNative.Pa_GetDeviceInfo(i);
                    if (infoPtr == IntPtr.Zero) continue;

                    var info = Marshal.PtrToStructure<PortAudioNative.PaDeviceInfo>(infoPtr);
                    string name = Marshal.PtrToStringAnsi(info.name) ?? "Unknown";

                    // Filter logic
                    if (input)
                    {
                        if (info.maxInputChannels > 0)
                        {
                            Console.WriteLine($"  [{i}] {name}");
                            Console.WriteLine($"      In: {info.maxInputChannels} | Out: {info.maxOutputChannels} | SR: {info.defaultSampleRate}");
                            found = true;
                        }
                    }
                    else
                    {
                        // For "speakers", we look for devices we can READ from that might be loopbacks
                        // On Linux, PulseAudio monitors appear as inputs.
                        // On macOS, BlackHole appears as input.
                        // So effectively we list inputs that look like loopbacks, OR we rely on user knowing the index.
                        // If strict output listing is requested (uncommon for *capture*), we'd check maxOutputChannels.
                        // But since this is for *recording*, we need devices with maxInputChannels > 0.
                        
                        // We'll show all inputs, but highlight likely loopbacks
                        if (info.maxInputChannels > 0)
                        {
                            bool likelyLoopback = name.Contains("Monitor", StringComparison.OrdinalIgnoreCase) 
                                                  || name.Contains("BlackHole", StringComparison.OrdinalIgnoreCase)
                                                  || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

                            if (likelyLoopback || !input) // If specifically listing 'speakers', we show everything or filter? 
                            {
                                // Show all inputs in "speaker" mode too, as the user selects the loopback device *as* an input.
                                Console.WriteLine($"  [{i}] {name}{(likelyLoopback ? " [Recommended for System Audio]" : "")}");
                                found = true;
                            }
                        }
                    }
                }

                if (!found)
                {
                    Console.WriteLine("  No matching devices found.");
                }
            }
            finally
            {
                PortAudioNative.Pa_Terminate();
            }
        }

        public void StartRecording()
        {
            if (isRecording) return;

            var inputParams = new PortAudioNative.PaStreamParameters
            {
                device = deviceIndex,
                channelCount = channels,
                sampleFormat = PortAudioNative.PaSampleFormat.paInt16, // Capture 16-bit directly
                suggestedLatency = 0.1, // 100ms
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            var err = PortAudioNative.Pa_OpenStream(
                out stream,
                ref inputParams,
                IntPtr.Zero, // No output
                sampleRate,
                0, // Frames per buffer (0 = auto)
                PortAudioNative.PaStreamFlags.paClipOff,
                IntPtr.Zero, // Blocking mode
                IntPtr.Zero
            );

            if (err != PortAudioNative.PaError.paNoError)
            {
                throw new Exception($"Failed to open PortAudio stream: {err}");
            }

            err = PortAudioNative.Pa_StartStream(stream);
            if (err != PortAudioNative.PaError.paNoError)
            {
                PortAudioNative.Pa_CloseStream(stream);
                stream = IntPtr.Zero;
                throw new Exception($"Failed to start PortAudio stream: {err}");
            }

            isRecording = true;
            cts = new CancellationTokenSource();
            captureTask = Task.Run(() => CaptureLoop(cts.Token));
        }

        private void CaptureLoop(CancellationToken token)
        {
            const int framesPerRead = 1024; // Small chunks
            int frameSize = channels * 2; // 16-bit = 2 bytes
            int bufferSize = framesPerRead * frameSize;
            var buffer = new byte[bufferSize];

            try
            {
                while (!token.IsCancellationRequested && stream != IntPtr.Zero)
                {
                    var err = PortAudioNative.Pa_ReadStream(stream, buffer, framesPerRead);
                    if (err == PortAudioNative.PaError.paNoError || err == PortAudioNative.PaError.paInputOverflowed)
                    {
                        // Copy buffer to yield
                        var chunk = new byte[bufferSize];
                        Array.Copy(buffer, chunk, bufferSize);
                        audioChunks.Add(chunk);
                        
                        // Calculate level
                        currentLevel = AudioUtils.CalculateMaxLevel(chunk);
                    }
                    else
                    {
                        // Handle error or break?
                        // For now we just yield empty? No, better to log/ignore
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        public void StopRecording()
        {
            if (!isRecording) return;

            cts?.Cancel();
            captureTask?.Wait();

            if (stream != IntPtr.Zero)
            {
                PortAudioNative.Pa_StopStream(stream);
                PortAudioNative.Pa_CloseStream(stream);
                stream = IntPtr.Zero;
            }

            isRecording = false;
            audioChunks.CompleteAdding();
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            return audioChunks.GetConsumingEnumerable();
        }

        public float GetCurrentAudioLevel()
        {
            return currentLevel;
        }

        public int GetBytesPerSecond()
        {
            return sampleRate * channels * 2;
        }

        public MemoryStream CreateWavStream(byte[] audioData)
        {
            // Simple WAV header writer
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);

            // RIFF header
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + audioData.Length);
            writer.Write("WAVE".ToCharArray());

            // fmt chunk
            writer.Write("fmt ".ToCharArray());
            writer.Write(16); // Chunk size
            writer.Write((short)1); // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2); // Byte rate
            writer.Write((short)(channels * 2)); // Block align
            writer.Write((short)16); // Bits per sample

            // data chunk
            writer.Write("data".ToCharArray());
            writer.Write(audioData.Length);
            writer.Write(audioData);
            writer.Flush();

            stream.Position = 0;
            return stream;
        }

        public void Dispose()
        {
            if (disposed) return;
            StopRecording();
            PortAudioNative.Pa_Terminate();
            disposed = true;
        }
    }
}
