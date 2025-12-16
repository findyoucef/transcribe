using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;

namespace Transcribe
{
    public class CombinedRecorder : IAudioRecorder
    {
        private WasapiLoopbackCapture? loopbackCapture;
        private WaveInEvent? waveIn;
        private readonly BlockingCollection<byte[]> combinedAudioChunks;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int speakerDeviceNumber;
        private readonly int micDeviceNumber;
        private bool isRecording;
        private float currentAudioLevel;
        private WaveFormat? speakerCaptureFormat;
        private WaveFormat? micCaptureFormat;
        
        private readonly BlockingCollection<byte[]> speakerRawChunks;
        private readonly BlockingCollection<byte[]> micRawChunks;
        private Task? mixingTask;
        private CancellationTokenSource? cancellationTokenSource;

        public CombinedRecorder(int speakerDeviceNumber = 0, int micDeviceNumber = 0, int sampleRate = 16000, int channels = 1)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.speakerDeviceNumber = speakerDeviceNumber;
            this.micDeviceNumber = micDeviceNumber;
            
            combinedAudioChunks = new BlockingCollection<byte[]>();
            speakerRawChunks = new BlockingCollection<byte[]>();
            micRawChunks = new BlockingCollection<byte[]>();
        }

        public void StartRecording()
        {
            if (isRecording)
                return;

            // Start speaker capture
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            if (speakerDeviceNumber >= devices.Count)
            {
                throw new ArgumentException($"Speaker device number {speakerDeviceNumber} not found. Available devices: 0-{devices.Count - 1}");
            }

            var device = devices[speakerDeviceNumber];
            loopbackCapture = new WasapiLoopbackCapture(device);
            speakerCaptureFormat = loopbackCapture.WaveFormat;
            loopbackCapture.DataAvailable += OnSpeakerDataAvailable;
            loopbackCapture.StartRecording();

            // Start microphone capture
            waveIn = new WaveInEvent
            {
                DeviceNumber = micDeviceNumber,
                WaveFormat = new WaveFormat(sampleRate, 16, channels),
                BufferMilliseconds = 50
            };
            micCaptureFormat = waveIn.WaveFormat;
            waveIn.DataAvailable += OnMicDataAvailable;
            waveIn.StartRecording();

            isRecording = true;
            cancellationTokenSource = new CancellationTokenSource();
            
            // Start mixing task
            mixingTask = Task.Run(() => MixAudioStreams(cancellationTokenSource.Token));
        }

        public void StopRecording()
        {
            if (!isRecording)
                return;

            isRecording = false;
            
            loopbackCapture?.StopRecording();
            waveIn?.StopRecording();
            
            speakerRawChunks.CompleteAdding();
            micRawChunks.CompleteAdding();
            
            cancellationTokenSource?.Cancel();
            mixingTask?.Wait();
            
            combinedAudioChunks.CompleteAdding();
        }

        private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                speakerRawChunks.TryAdd(buffer);
                UpdateAudioLevel(buffer, speakerCaptureFormat);
            }
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                micRawChunks.TryAdd(buffer);
                UpdateAudioLevel(buffer, micCaptureFormat);
            }
        }

        private void UpdateAudioLevel(byte[] buffer, WaveFormat? format)
        {
            if (format == null) return;

            float max = 0;
            
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < buffer.Length - 1; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    float normalized = Math.Abs(sample / 32768f);
                    if (normalized > max)
                        max = normalized;
                }
            }
            else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < buffer.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    float normalized = Math.Abs(sample);
                    if (normalized > max)
                        max = normalized;
                }
            }
            
            if (max > currentAudioLevel)
                currentAudioLevel = max;
        }

        private void MixAudioStreams(CancellationToken cancellationToken)
        {
            var targetFormat = new WaveFormat(sampleRate, 16, channels);
            
            var speakerQueue = new Queue<byte[]>();
            var micQueue = new Queue<byte[]>();
            
            var speakerEnumerator = speakerRawChunks.GetConsumingEnumerable().GetEnumerator();
            var micEnumerator = micRawChunks.GetConsumingEnumerable().GetEnumerator();

            // Lower buffer to reduce latency
            const int targetBufferSize = 16000; // ~0.5 seconds at 16kHz mono
            const int minBufferSize = 8000;     // ~0.25 seconds
            
            bool speakerDone = false;
            bool micDone = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested && (!speakerDone || !micDone || speakerQueue.Count > 0 || micQueue.Count > 0))
                {
                    bool anyDataReceived = false;

                    // Read from speaker
                    if (!speakerDone)
                    {
                        try
                        {
                            if (speakerEnumerator.MoveNext())
                            {
                                speakerQueue.Enqueue(speakerEnumerator.Current);
                                anyDataReceived = true;
                            }
                            else
                            {
                                speakerDone = true;
                            }
                        }
                        catch
                        {
                            speakerDone = true;
                        }
                    }

                    // Read from microphone
                    if (!micDone)
                    {
                        try
                        {
                            if (micEnumerator.MoveNext())
                            {
                                micQueue.Enqueue(micEnumerator.Current);
                                anyDataReceived = true;
                            }
                            else
                            {
                                micDone = true;
                            }
                        }
                        catch
                        {
                            micDone = true;
                        }
                    }

                    int speakerSize = speakerQueue.Sum(b => b.Length);
                    int micSize = micQueue.Sum(b => b.Length);

                    // Process sooner when we have at least min data from any source
                    bool shouldProcess = (speakerSize >= targetBufferSize || micSize >= targetBufferSize) ||
                                         (speakerSize >= minBufferSize && micSize >= minBufferSize) ||
                                         ((speakerDone || micDone) && (speakerSize >= minBufferSize || micSize >= minBufferSize)) ||
                                         (speakerDone && micDone && (speakerSize > 0 || micSize > 0));

                    if (shouldProcess)
                    {
                        var speakerList = new List<byte[]>();
                        var micList = new List<byte[]>();

                        int speakerDequeued = 0;
                        while (speakerQueue.Count > 0 && speakerDequeued < targetBufferSize)
                        {
                            var chunk = speakerQueue.Dequeue();
                            speakerList.Add(chunk);
                            speakerDequeued += chunk.Length;
                        }

                        int micDequeued = 0;
                        while (micQueue.Count > 0 && micDequeued < targetBufferSize)
                        {
                            var chunk = micQueue.Dequeue();
                            micList.Add(chunk);
                            micDequeued += chunk.Length;
                        }

                        var mixed = MixBuffers(speakerList, micList, targetFormat);
                        if (mixed.Length > 0)
                        {
                            combinedAudioChunks.TryAdd(mixed);
                        }
                    }

                    if (!anyDataReceived)
                    {
                        Thread.Sleep(5);
                    }
                }
            }
            finally
            {
                speakerEnumerator.Dispose();
                micEnumerator.Dispose();
            }
        }

        private byte[] MixBuffers(List<byte[]> speakerBuffers, List<byte[]> micBuffers, WaveFormat targetFormat)
        {
            // Convert speaker audio to target format
            var speakerConverted = ConvertToTargetFormat(speakerBuffers, speakerCaptureFormat, targetFormat);
            
            // Microphone is already in target format, just flatten
            var micData = FlattenBuffers(micBuffers);

            // Use the longer length
            int maxLength = Math.Max(speakerConverted.Length, micData.Length);
            if (maxLength == 0) return Array.Empty<byte>();
            
            // Make sure length is even (for 16-bit samples)
            if (maxLength % 2 != 0) maxLength--;
            
            var mixed = new byte[maxLength];

            // Mix 16-bit PCM samples
            for (int i = 0; i < maxLength - 1; i += 2)
            {
                short speakerSample = i < speakerConverted.Length - 1 ? BitConverter.ToInt16(speakerConverted, i) : (short)0;
                short micSample = i < micData.Length - 1 ? BitConverter.ToInt16(micData, i) : (short)0;

                // Mix by adding
                int mixedSample = speakerSample + micSample;
                
                // Clamp to prevent overflow
                mixedSample = Math.Clamp(mixedSample, short.MinValue, short.MaxValue);
                
                byte[] mixedBytes = BitConverter.GetBytes((short)mixedSample);
                mixed[i] = mixedBytes[0];
                mixed[i + 1] = mixedBytes[1];
            }

            return mixed;
        }

        private byte[] ConvertToTargetFormat(List<byte[]> buffers, WaveFormat? sourceFormat, WaveFormat targetFormat)
        {
            if (buffers.Count == 0 || sourceFormat == null)
                return Array.Empty<byte>();

            var rawData = FlattenBuffers(buffers);
            if (rawData.Length == 0)
                return Array.Empty<byte>();

            try
            {
                using (var sourceStream = new RawSourceWaveStream(new MemoryStream(rawData), sourceFormat))
                using (var resampler = new MediaFoundationResampler(sourceStream, targetFormat))
                {
                    resampler.ResamplerQuality = 60;
                    
                    using (var outputStream = new MemoryStream())
                    {
                        var buffer = new byte[targetFormat.AverageBytesPerSecond];
                        int bytesRead;
                        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outputStream.Write(buffer, 0, bytesRead);
                        }
                        
                        return outputStream.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Audio conversion error: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private byte[] FlattenBuffers(List<byte[]> buffers)
        {
            if (buffers.Count == 0)
                return Array.Empty<byte>();

            int totalLength = buffers.Sum(b => b.Length);
            if (totalLength == 0)
                return Array.Empty<byte>();
                
            var result = new byte[totalLength];
            int offset = 0;

            foreach (var buffer in buffers)
            {
                Buffer.BlockCopy(buffer, 0, result, offset, buffer.Length);
                offset += buffer.Length;
            }

            return result;
        }

        public float GetCurrentAudioLevel()
        {
            var level = currentAudioLevel;
            currentAudioLevel = 0; // Reset for next measurement
            return level;
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            return combinedAudioChunks.GetConsumingEnumerable();
        }

        public MemoryStream CreateWavStream(byte[] audioData)
        {
            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            
            var tempStream = new MemoryStream();
            
            using (var writer = new WaveFileWriter(tempStream, waveFormat))
            {
                writer.Write(audioData, 0, audioData.Length);
                writer.Flush();
            }
            
            var finalStream = new MemoryStream(tempStream.ToArray());
            finalStream.Position = 0;
            
            return finalStream;
        }

        public void Dispose()
        {
            StopRecording();
            
            if (loopbackCapture != null)
            {
                loopbackCapture.DataAvailable -= OnSpeakerDataAvailable;
                loopbackCapture.Dispose();
            }
            
            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnMicDataAvailable;
                waveIn.Dispose();
            }
            
            combinedAudioChunks?.Dispose();
            speakerRawChunks?.Dispose();
            micRawChunks?.Dispose();
            cancellationTokenSource?.Dispose();
        }

        public int GetBytesPerSecond()
        {
            return 16000 * 2 * 1;
        }
    }
}
