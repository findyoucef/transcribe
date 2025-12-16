using NAudio.Wave;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;

namespace Transcribe
{
    public class MicrophoneRecorder : IAudioRecorder
    {
        private WaveInEvent? waveIn;
        private WasapiCapture? wasapiCapture;
        private WaveFormat? captureFormat;
        private readonly BlockingCollection<byte[]> audioChunks;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int deviceNumber;
        private readonly int bufferMilliseconds;
        private readonly bool useWasapiExclusive;
        private bool isRecording;
        private float currentAudioLevel;

        public MicrophoneRecorder(int sampleRate = 16000, int channels = 1, int deviceNumber = 0, int bufferMilliseconds = 50, bool useWasapiExclusive = false)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.deviceNumber = deviceNumber;
            this.bufferMilliseconds = bufferMilliseconds;
            this.useWasapiExclusive = useWasapiExclusive;
            audioChunks = new BlockingCollection<byte[]>();
        }

        public static void ListMicrophones()
        {
            Console.WriteLine("Available microphone devices:");
            Console.WriteLine();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                Console.WriteLine($"  [{i}] {capabilities.ProductName}");
                Console.WriteLine($"      Channels: {capabilities.Channels}");
                Console.WriteLine();
            }

            if (WaveInEvent.DeviceCount == 0)
            {
                Console.WriteLine("  No microphone devices found!");
            }
        }

        public void StartRecording()
        {
            if (isRecording)
                return;

            if (useWasapiExclusive)
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                if (deviceNumber >= devices.Count)
                {
                    throw new ArgumentException($"Mic device number {deviceNumber} not found. Available devices: 0-{devices.Count - 1}");
                }
                var device = devices[deviceNumber];

                wasapiCapture = new WasapiCapture(device, false, bufferMilliseconds);
                captureFormat = wasapiCapture.WaveFormat;
                wasapiCapture.DataAvailable += OnWasapiDataAvailable;
                wasapiCapture.StartRecording();
                isRecording = true;
                return;
            }

            waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(sampleRate, 16, channels),
                BufferMilliseconds = bufferMilliseconds,
                NumberOfBuffers = 3
            };

            captureFormat = waveIn.WaveFormat;
            waveIn.DataAvailable += OnWaveInDataAvailable;
            waveIn.StartRecording();
            isRecording = true;
        }

        public void StopRecording()
        {
            if (!isRecording)
                return;

            if (useWasapiExclusive)
            {
                wasapiCapture?.StopRecording();
            }
            else
            {
                waveIn?.StopRecording();
            }

            isRecording = false;
            audioChunks.CompleteAdding();
        }

        private void OnWaveInDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                audioChunks.TryAdd(buffer);
                currentAudioLevel = GetMaxPcm16Level(buffer);
            }
        }

        private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                audioChunks.TryAdd(buffer);
                currentAudioLevel = GetLevel(buffer, captureFormat);
            }
        }

        private float GetMaxPcm16Level(byte[] buffer)
        {
            float max = 0;
            for (int i = 0; i < buffer.Length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float normalized = Math.Abs(sample / 32768f);
                if (normalized > max)
                    max = normalized;
            }
            return max;
        }

        private float GetLevel(byte[] buffer, WaveFormat? format)
        {
            if (format == null) return 0;
            if (format.BitsPerSample == 16)
            {
                return GetMaxPcm16Level(buffer);
            }
            if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                float max = 0;
                for (int i = 0; i < buffer.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    float normalized = Math.Abs(sample);
                    if (normalized > max)
                        max = normalized;
                }
                return max;
            }
            return 0;
        }

        public float GetCurrentAudioLevel()
        {
            return currentAudioLevel;
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            return audioChunks.GetConsumingEnumerable();
        }

        public MemoryStream CreateWavStream(byte[] audioData)
        {
            if (useWasapiExclusive && captureFormat != null && (captureFormat.SampleRate != sampleRate || captureFormat.Channels != channels || captureFormat.Encoding != WaveFormatEncoding.Pcm))
            {
                var tempStream = new MemoryStream();
                var targetFormat = new WaveFormat(sampleRate, 16, channels);
                using (var sourceStream = new RawSourceWaveStream(new MemoryStream(audioData), captureFormat))
                using (var resampler = new MediaFoundationResampler(sourceStream, targetFormat))
                {
                    resampler.ResamplerQuality = 60;
                    using (var writer = new WaveFileWriter(tempStream, targetFormat))
                    {
                        var buffer = new byte[targetFormat.AverageBytesPerSecond];
                        int bytesRead;
                        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, bytesRead);
                        }
                        writer.Flush();
                    }
                }
                var finalStream = new MemoryStream(tempStream.ToArray());
                finalStream.Position = 0;
                return finalStream;
            }
            else
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
        }

        public void Dispose()
        {
            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnWaveInDataAvailable;
                waveIn.Dispose();
            }
            if (wasapiCapture != null)
            {
                wasapiCapture.DataAvailable -= OnWasapiDataAvailable;
                wasapiCapture.Dispose();
            }
            audioChunks.Dispose();
        }

        public int GetBytesPerSecond()
        {
            return sampleRate * 2 * channels;
        }
    }
}
