using NAudio.Wave;

namespace Transcribe
{
    public sealed class FileAudioRecorder : IAudioRecorder
    {
        private readonly string path;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int chunkMilliseconds;
        private byte[]? pcmData;
        private bool isRecording;
        private bool isStopped;
        private float currentAudioLevel;

        public FileAudioRecorder(string path, int sampleRate = 16000, int channels = 1, int chunkMilliseconds = 50)
        {
            this.path = path;
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.chunkMilliseconds = Math.Max(10, chunkMilliseconds);
        }

        public void StartRecording()
        {
            if (isRecording)
            {
                return;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Audio file not found.", path);
            }

            var samples = AudioUtils.ReadAudioFileAsMono(path, sampleRate);
            pcmData = ConvertToPcm16(samples);
            isRecording = true;
            isStopped = false;
        }

        public void StopRecording()
        {
            isStopped = true;
        }

        public float GetCurrentAudioLevel()
        {
            return currentAudioLevel;
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            if (!isRecording)
            {
                throw new InvalidOperationException("Recording has not been started.");
            }

            if (pcmData == null || pcmData.Length == 0)
            {
                yield break;
            }

            int bytesPerSecond = GetBytesPerSecond();
            int chunkBytes = Math.Max(1, bytesPerSecond * chunkMilliseconds / 1000);
            int offset = 0;

            while (offset < pcmData.Length && !isStopped)
            {
                int size = Math.Min(chunkBytes, pcmData.Length - offset);
                var chunk = new byte[size];
                Array.Copy(pcmData, offset, chunk, 0, size);
                offset += size;

                currentAudioLevel = GetMaxPcm16Level(chunk);
                yield return chunk;
            }
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

        public int GetBytesPerSecond()
        {
            return sampleRate * 2 * channels;
        }

        public bool IsMicrophone => false;

        public void Dispose()
        {
            pcmData = null;
        }

        private static byte[] ConvertToPcm16(float[] samples)
        {
            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float value = Math.Clamp(samples[i], -1f, 1f);
                short sample = (short)Math.Round(value * short.MaxValue);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            return pcm;
        }

        private static float GetMaxPcm16Level(byte[] buffer)
        {
            float max = 0;
            for (int i = 0; i < buffer.Length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float normalized = Math.Abs(sample / 32768f);
                if (normalized > max)
                {
                    max = normalized;
                }
            }
            return max;
        }
    }
}
