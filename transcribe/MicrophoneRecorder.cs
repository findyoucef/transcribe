using NAudio.Wave;
using System.Collections.Concurrent;

namespace Transcribe
{
    public class MicrophoneRecorder : IAudioRecorder
    {
        private WaveInEvent? waveIn;
        private readonly BlockingCollection<byte[]> audioChunks;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int deviceNumber;
        private bool isRecording;
        private float currentAudioLevel;

        public MicrophoneRecorder(int sampleRate = 16000, int channels = 1, int deviceNumber = 0)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.deviceNumber = deviceNumber;
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

            waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(sampleRate, 16, channels),
                BufferMilliseconds = 100
            };

            waveIn.DataAvailable += OnDataAvailable;
            waveIn.StartRecording();
            isRecording = true;
        }

        public void StopRecording()
        {
            if (!isRecording)
                return;

            waveIn?.StopRecording();
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
            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            
            // Create a temporary stream for the WaveFileWriter
            var tempStream = new MemoryStream();
            
            using (var writer = new WaveFileWriter(tempStream, waveFormat))
            {
                writer.Write(audioData, 0, audioData.Length);
                writer.Flush();
            }
            
            // Copy to a new stream since WaveFileWriter closed the original
            var finalStream = new MemoryStream(tempStream.ToArray());
            finalStream.Position = 0;
            
            return finalStream;
        }

        public void Dispose()
        {
            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.Dispose();
            }
            audioChunks.Dispose();
        }
    }
}
