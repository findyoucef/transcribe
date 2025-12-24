using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;

namespace Transcribe
{
    public class SpeakerRecorder : IAudioRecorder
    {
        private WasapiLoopbackCapture? loopbackCapture;
        private readonly BlockingCollection<byte[]> audioChunks;
        private readonly int deviceNumber;
        private bool isRecording;
        private float currentAudioLevel;
        private WaveFormat? captureFormat;

        public SpeakerRecorder(int deviceNumber = 0)
        {
            this.deviceNumber = deviceNumber;
            audioChunks = new BlockingCollection<byte[]>();
        }

        public static void ListSpeakers()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Speaker/output device listing is only supported on Windows.");
                return;
            }

            Console.WriteLine("Available speaker/output devices:");
            Console.WriteLine();
            
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                Console.WriteLine($"  [{i}] {device.FriendlyName}");
                Console.WriteLine($"      Channels: {device.AudioClient.MixFormat.Channels}");
                Console.WriteLine();
            }

            if (devices.Count == 0)
            {
                Console.WriteLine("  No speaker/output devices found!");
            }
        }

        public static IReadOnlyList<AudioDeviceInfo> GetSpeakers()
        {
            if (!OperatingSystem.IsWindows())
            {
                return Array.Empty<AudioDeviceInfo>();
            }

            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var result = new List<AudioDeviceInfo>(devices.Count);
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                result.Add(new AudioDeviceInfo(i, device.FriendlyName, device.AudioClient.MixFormat.Channels));
            }

            return result;
        }

        public void StartRecording()
        {
            if (isRecording)
                return;

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Live speaker/output capture is only supported on Windows.");
            }

            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            if (deviceNumber >= devices.Count)
            {
                throw new ArgumentException($"Device number {deviceNumber} not found. Available devices: 0-{devices.Count - 1}");
            }

            var device = devices[deviceNumber];
            loopbackCapture = new WasapiLoopbackCapture(device);
            captureFormat = loopbackCapture.WaveFormat;
            
            loopbackCapture.DataAvailable += OnDataAvailable;
            loopbackCapture.StartRecording();
            isRecording = true;
        }

        public void StopRecording()
        {
            if (!isRecording)
                return;

            loopbackCapture?.StopRecording();
            isRecording = false;
            audioChunks.CompleteAdding();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                audioChunks.TryAdd(buffer);
                
                currentAudioLevel = GetMaxAudioLevel(buffer);
            }
        }

        private float GetMaxAudioLevel(byte[] buffer)
        {
            if (captureFormat == null)
                return 0;

            float max = 0;
            
            // Handle different bit depths
            if (captureFormat.BitsPerSample == 16)
            {
                for (int i = 0; i < buffer.Length - 1; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    float normalized = Math.Abs(sample / 32768f);
                    if (normalized > max)
                        max = normalized;
                }
            }
            else if (captureFormat.BitsPerSample == 32 && captureFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < buffer.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    float normalized = Math.Abs(sample);
                    if (normalized > max)
                        max = normalized;
                }
            }
            
            return max;
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
            if (captureFormat == null)
                throw new InvalidOperationException("Capture format not initialized");

            var tempStream = new MemoryStream();
            
            // Convert to 16kHz mono 16-bit for Whisper if needed
            var targetFormat = new WaveFormat(16000, 16, 1);
            
            using (var sourceStream = new RawSourceWaveStream(new MemoryStream(audioData), captureFormat))
            {
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
            }
            
            var finalStream = new MemoryStream(tempStream.ToArray());
            finalStream.Position = 0;
            
            return finalStream;
        }

        public int GetBytesPerSecond()
        {
            if (captureFormat == null) return 0;
            return captureFormat.AverageBytesPerSecond;
        }

        public bool IsMicrophone => false;

        public void Dispose()
        {
            if (loopbackCapture != null)
            {
                loopbackCapture.DataAvailable -= OnDataAvailable;
                loopbackCapture.Dispose();
            }
            audioChunks.Dispose();
        }
    }
}
