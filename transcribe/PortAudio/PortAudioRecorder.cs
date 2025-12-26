using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Transcribe.PortAudio
{
    public class PortAudioRecorder : IAudioRecorder
    {
        private IntPtr stream = IntPtr.Zero;
        private readonly int deviceIndex;
        private readonly int targetSampleRate;
        private readonly int targetChannels;
        private readonly bool isLoopback;
        private readonly BlockingCollection<byte[]> audioChunks = new();
        private bool isRecording;
        private CancellationTokenSource? cts;
        private Task? captureTask;
        private float currentLevel;
        private bool disposed;
        private int actualSampleRate;
        private int actualChannels;
        private bool needsResample;

        public PortAudioRecorder(int deviceIndex, int sampleRate = 16000, int channels = 1, bool isLoopback = false)
        {
            this.deviceIndex = deviceIndex;
            targetSampleRate = sampleRate;
            targetChannels = channels;
            this.isLoopback = isLoopback;
            actualSampleRate = sampleRate;
            actualChannels = channels;

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

            needsResample = false;
            var err = TryOpenStream(deviceIndex, targetSampleRate, targetChannels, out stream);
            if (err == PortAudioNative.PaError.paNoError)
            {
                actualSampleRate = targetSampleRate;
                actualChannels = targetChannels;
            }
            else
            {
                var infoPtr = PortAudioNative.Pa_GetDeviceInfo(deviceIndex);
                if (infoPtr == IntPtr.Zero)
                {
                    throw new Exception($"Failed to open PortAudio stream: {err}");
                }

                var info = Marshal.PtrToStructure<PortAudioNative.PaDeviceInfo>(infoPtr);
                var fallbackSampleRate = (int)Math.Round(info.defaultSampleRate);
                var fallbackChannels = ResolveFallbackChannels(info);

                err = TryOpenStream(deviceIndex, fallbackSampleRate, fallbackChannels, out stream);
                if (err != PortAudioNative.PaError.paNoError)
                {
                    throw new Exception($"Failed to open PortAudio stream: {err}");
                }

                actualSampleRate = fallbackSampleRate;
                actualChannels = fallbackChannels;
                needsResample = actualSampleRate != targetSampleRate || actualChannels != targetChannels;
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
            int frameSize = actualChannels * 2; // 16-bit = 2 bytes
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
            var sr = actualSampleRate > 0 ? actualSampleRate : targetSampleRate;
            var ch = actualChannels > 0 ? actualChannels : targetChannels;
            return sr * ch * 2;
        }

        public MemoryStream CreateWavStream(byte[] audioData)
        {
            if (!needsResample)
            {
                // Simple WAV header writer for native capture format
                var stream = new MemoryStream();
                var writer = new BinaryWriter(stream);

                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + audioData.Length);
                writer.Write("WAVE".ToCharArray());

                writer.Write("fmt ".ToCharArray());
                writer.Write(16); // Chunk size
                writer.Write((short)1); // PCM
                writer.Write((short)actualChannels);
                writer.Write(actualSampleRate);
                writer.Write(actualSampleRate * actualChannels * 2); // Byte rate
                writer.Write((short)(actualChannels * 2)); // Block align
                writer.Write((short)16); // Bits per sample

                writer.Write("data".ToCharArray());
                writer.Write(audioData.Length);
                writer.Write(audioData);
                writer.Flush();

                stream.Position = 0;
                return stream;
            }

            var streamResampled = new MemoryStream();
            var sourceFormat = new WaveFormat(actualSampleRate, 16, actualChannels);
            using (var sourceMemoryStream = new MemoryStream(audioData))
            using (var sourceStream = new RawSourceWaveStream(sourceMemoryStream, sourceFormat))
            {
                ISampleProvider provider = sourceStream.ToSampleProvider();
                provider = EnsureChannelCount(provider, targetChannels);
                if (provider.WaveFormat.SampleRate != targetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
                }

                var waveProvider = new SampleToWaveProvider16(provider);
                using (var writer = new WaveFileWriter(streamResampled, waveProvider.WaveFormat))
                {
                    var buffer = new byte[waveProvider.WaveFormat.AverageBytesPerSecond];
                    int bytesRead;
                    while ((bytesRead = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }
                    writer.Flush();
                }
            }

            streamResampled.Position = 0;
            return streamResampled;
        }

        public void Dispose()
        {
            if (disposed) return;
            StopRecording();
            PortAudioNative.Pa_Terminate();
            disposed = true;
        }

        private PortAudioNative.PaError TryOpenStream(int device, int sampleRate, int channels, out IntPtr openedStream)
        {
            var inputParams = new PortAudioNative.PaStreamParameters
            {
                device = device,
                channelCount = Math.Max(1, channels),
                sampleFormat = PortAudioNative.PaSampleFormat.paInt16,
                suggestedLatency = 0.1,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            return PortAudioNative.Pa_OpenStream(
                out openedStream,
                ref inputParams,
                IntPtr.Zero,
                sampleRate,
                0,
                PortAudioNative.PaStreamFlags.paClipOff,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        private int ResolveFallbackChannels(PortAudioNative.PaDeviceInfo info)
        {
            if (info.maxInputChannels <= 0)
            {
                return targetChannels;
            }

            if (isLoopback)
            {
                return info.maxInputChannels >= 2 ? 2 : 1;
            }

            return 1;
        }

        private static ISampleProvider EnsureChannelCount(ISampleProvider source, int targetChannels)
        {
            if (source.WaveFormat.Channels == targetChannels)
            {
                return source;
            }

            if (targetChannels == 1)
            {
                return new ToMonoSampleProvider(source);
            }

            if (source.WaveFormat.Channels == 1 && targetChannels > 1)
            {
                return new MonoToMultiSampleProvider(source, targetChannels);
            }

            return source;
        }

        private sealed class ToMonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly int channels;
            private float[] sourceBuffer = Array.Empty<float>();

            public ToMonoSampleProvider(ISampleProvider source)
            {
                this.source = source;
                channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int required = count * channels;
                if (sourceBuffer.Length < required)
                {
                    sourceBuffer = new float[required];
                }

                int read = source.Read(sourceBuffer, 0, required);
                int frames = read / channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0f;
                    int baseIndex = frame * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += sourceBuffer[baseIndex + ch];
                    }
                    buffer[offset + frame] = sum / channels;
                }

                return frames;
            }
        }

        private sealed class MonoToMultiSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly int targetChannels;
            private float[] sourceBuffer = Array.Empty<float>();

            public MonoToMultiSampleProvider(ISampleProvider source, int targetChannels)
            {
                this.source = source;
                this.targetChannels = Math.Max(1, targetChannels);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, this.targetChannels);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int framesRequested = count / targetChannels;
                if (framesRequested <= 0)
                {
                    return 0;
                }

                if (sourceBuffer.Length < framesRequested)
                {
                    sourceBuffer = new float[framesRequested];
                }

                int framesRead = source.Read(sourceBuffer, 0, framesRequested);
                for (int frame = 0; frame < framesRead; frame++)
                {
                    float sample = sourceBuffer[frame];
                    int baseIndex = offset + (frame * targetChannels);
                    for (int ch = 0; ch < targetChannels; ch++)
                    {
                        buffer[baseIndex + ch] = sample;
                    }
                }

                return framesRead * targetChannels;
            }
        }
    }
}
