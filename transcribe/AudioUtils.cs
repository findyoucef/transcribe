using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Transcribe
{
    public static class AudioUtils
    {
        public static float[] ReadAudioFileAsMono(string path, int targetSampleRate)
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new WaveFileReader(path);
                var provider = EnsureMono(reader.ToSampleProvider());
                if (provider.WaveFormat.SampleRate != targetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
                }
                return ReadAllSamples(provider);
            }
            else
            {
                using var reader = new AudioFileReader(path);
                var provider = EnsureMono(reader);
                if (provider.WaveFormat.SampleRate != targetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
                }
                return ReadAllSamples(provider);
            }
        }

        public static float[] ReadWavBytesAsMono(byte[] wavBytes, int targetSampleRate)
        {
            using var stream = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(stream);
            ISampleProvider provider = EnsureMono(reader.ToSampleProvider());
            if (provider.WaveFormat.SampleRate != targetSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
            }

            return ReadAllSamples(provider);
        }

        public static float CalculateMaxLevel(byte[] buffer)
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

        private static ISampleProvider EnsureMono(ISampleProvider source)
        {
            if (source.WaveFormat.Channels == 1)
            {
                return source;
            }

            return new ToMonoSampleProvider(source);
        }

        private static float[] ReadAllSamples(ISampleProvider provider)
        {
            var samples = new List<float>();
            var buffer = new float[provider.WaveFormat.SampleRate];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    samples.Add(buffer[i]);
                }
            }

            return samples.ToArray();
        }

        private sealed class ToMonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly int channels;
            private float[] sourceBuffer;

            public ToMonoSampleProvider(ISampleProvider source)
            {
                this.source = source;
                channels = source.WaveFormat.Channels;
                sourceBuffer = Array.Empty<float>();
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
    }
}
