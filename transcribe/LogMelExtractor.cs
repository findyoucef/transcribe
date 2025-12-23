using System;
using System.Linq;
using NAudio.Dsp;

namespace Transcribe
{
    public sealed class LogMelExtractor
    {
        private readonly SpeakerEmbeddingModelConfig config;
        private readonly int frameSize;
        private readonly int hopSize;
        private readonly int fftSize;
        private readonly int fftBins;
        private readonly float[] window;
        private readonly float[,] melFilterBank;
        private readonly int fftLog2;

        public LogMelExtractor(SpeakerEmbeddingModelConfig config)
        {
            this.config = config;
            frameSize = (int)Math.Round(config.SampleRate * (config.FrameMs / 1000.0));
            hopSize = (int)Math.Round(config.SampleRate * (config.HopMs / 1000.0));
            fftSize = NextPowerOfTwo(frameSize);
            fftBins = (fftSize / 2) + 1;
            fftLog2 = (int)Math.Round(Math.Log(fftSize, 2));
            window = BuildHammingWindow(frameSize);
            melFilterBank = BuildMelFilterBank(config.MelBins, fftBins, config.SampleRate);
        }

        public float[] Extract(float[] samples, out int frameCount)
        {
            if (samples.Length < frameSize)
            {
                frameCount = 0;
                return Array.Empty<float>();
            }

            var processed = config.ApplyPreEmphasis
                ? ApplyPreEmphasis(samples, config.PreEmphasis)
                : samples;

            frameCount = 1 + (processed.Length - frameSize) / hopSize;
            var features = new float[frameCount * config.MelBins];
            var fftBuffer = new Complex[fftSize];
            var power = new float[fftBins];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int sampleOffset = frame * hopSize;
                Array.Clear(fftBuffer, 0, fftBuffer.Length);

                for (int i = 0; i < frameSize; i++)
                {
                    float sample = processed[sampleOffset + i];
                    fftBuffer[i].X = sample * window[i];
                    fftBuffer[i].Y = 0f;
                }

                FastFourierTransform.FFT(true, fftLog2, fftBuffer);

                for (int i = 0; i < fftBins; i++)
                {
                    float real = fftBuffer[i].X;
                    float imag = fftBuffer[i].Y;
                    power[i] = (real * real) + (imag * imag);
                }

                for (int m = 0; m < config.MelBins; m++)
                {
                    float sum = 0f;
                    for (int k = 0; k < fftBins; k++)
                    {
                        sum += power[k] * melFilterBank[m, k];
                    }

                    features[(frame * config.MelBins) + m] = MathF.Log10(sum + 1e-10f);
                }
            }

            if (config.ApplyMeanNormalization || config.ApplyVarianceNormalization)
            {
                ApplyNormalization(features, frameCount, config.MelBins, config.ApplyVarianceNormalization);
            }

            return features;
        }

        private static float[] ApplyPreEmphasis(float[] samples, float preEmphasis)
        {
            var output = new float[samples.Length];
            if (samples.Length == 0)
            {
                return output;
            }

            output[0] = samples[0];
            for (int i = 1; i < samples.Length; i++)
            {
                output[i] = samples[i] - (preEmphasis * samples[i - 1]);
            }

            return output;
        }

        private static float[] BuildHammingWindow(int length)
        {
            var window = new float[length];
            if (length <= 1)
            {
                for (int i = 0; i < length; i++)
                {
                    window[i] = 1f;
                }
                return window;
            }

            for (int i = 0; i < length; i++)
            {
                window[i] = 0.54f - (0.46f * MathF.Cos((2f * MathF.PI * i) / (length - 1)));
            }

            return window;
        }

        private static float[,] BuildMelFilterBank(int melBins, int fftBins, int sampleRate)
        {
            var filterBank = new float[melBins, fftBins];
            double melMin = HertzToMel(0);
            double melMax = HertzToMel(sampleRate / 2.0);
            var melPoints = new double[melBins + 2];
            for (int i = 0; i < melPoints.Length; i++)
            {
                melPoints[i] = melMin + (i * (melMax - melMin) / (melBins + 1));
            }

            var hzPoints = melPoints.Select(MelToHertz).ToArray();
            var binPoints = hzPoints.Select(hz => (int)Math.Floor((fftBins - 1) * hz / (sampleRate / 2.0))).ToArray();

            for (int m = 1; m <= melBins; m++)
            {
                int left = binPoints[m - 1];
                int center = binPoints[m];
                int right = binPoints[m + 1];

                if (center == left)
                {
                    center = Math.Min(left + 1, fftBins - 1);
                }
                if (right == center)
                {
                    right = Math.Min(center + 1, fftBins - 1);
                }

                for (int k = left; k < center && k < fftBins; k++)
                {
                    if (k >= 0)
                    {
                        filterBank[m - 1, k] = (float)(k - left) / (center - left);
                    }
                }

                for (int k = center; k < right && k < fftBins; k++)
                {
                    if (k >= 0)
                    {
                        filterBank[m - 1, k] = (float)(right - k) / (right - center);
                    }
                }
            }

            return filterBank;
        }

        private static double HertzToMel(double hz)
        {
            return 2595.0 * Math.Log10(1.0 + (hz / 700.0));
        }

        private static double MelToHertz(double mel)
        {
            return 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
        }

        private static int NextPowerOfTwo(int value)
        {
            int power = 1;
            while (power < value)
            {
                power <<= 1;
            }
            return power;
        }

        private static void ApplyNormalization(float[] features, int frameCount, int melBins, bool applyVariance)
        {
            if (frameCount <= 0 || melBins <= 0)
            {
                return;
            }

            var means = new float[melBins];
            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * melBins;
                for (int m = 0; m < melBins; m++)
                {
                    means[m] += features[offset + m];
                }
            }

            var invFrames = 1f / frameCount;
            for (int m = 0; m < melBins; m++)
            {
                means[m] *= invFrames;
            }

            var variances = applyVariance ? new float[melBins] : Array.Empty<float>();
            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * melBins;
                for (int m = 0; m < melBins; m++)
                {
                    var centered = features[offset + m] - means[m];
                    features[offset + m] = centered;
                    if (applyVariance)
                    {
                        variances[m] += centered * centered;
                    }
                }
            }

            if (!applyVariance)
            {
                return;
            }

            for (int m = 0; m < melBins; m++)
            {
                variances[m] = MathF.Sqrt((variances[m] * invFrames) + 1e-6f);
            }

            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * melBins;
                for (int m = 0; m < melBins; m++)
                {
                    features[offset + m] /= variances[m];
                }
            }
        }
    }
}
