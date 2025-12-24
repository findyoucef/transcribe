using System;
using System.IO;

namespace transcribe.tests
{
    public static class TestHelpers
    {
        public static string CreateTempWavFile(int sampleRate, int channels, int seconds)
        {
            var filePath = Path.GetTempFileName();
            // Rename to .wav to satisfy checks
            var wavPath = Path.ChangeExtension(filePath, ".wav");
            if (File.Exists(filePath)) File.Delete(filePath);

            var numSamples = sampleRate * seconds * channels;
            var dataSize = numSamples * 2; // 16-bit
            var fileSize = 36 + dataSize;

            using (var fs = File.Create(wavPath))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write("RIFF".ToCharArray());
                writer.Write(fileSize);
                writer.Write("WAVE".ToCharArray());
                writer.Write("fmt ".ToCharArray());
                writer.Write(16); // Chunk size
                writer.Write((short)1); // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2); // Byte rate
                writer.Write((short)(channels * 2)); // Block align
                writer.Write((short)16); // Bits per sample
                writer.Write("data".ToCharArray());
                writer.Write(dataSize);

                for (int i = 0; i < numSamples; i++)
                {
                    // Generate a sine wave or just silence/noise
                    short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 16000);
                    writer.Write(sample);
                }
            }

            return wavPath;
        }
    }
}
