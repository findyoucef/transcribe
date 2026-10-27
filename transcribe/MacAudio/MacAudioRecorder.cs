using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Transcribe.MacAudio
{
    public sealed class MacAudioRecorder : IAudioRecorder
    {
        private const string LibraryName = "transcribe_audio";
        private readonly int? microphoneDeviceIndex;
        private readonly BlockingCollection<byte[]> audioChunks = new(1200);
        private readonly CancellationTokenSource cancellation = new();
        private IntPtr capture;
        private Task? captureTask;
        private Exception? captureError;
        private int sampleRate = 48000;
        private float currentLevel;
        private bool stopped;
        private bool disposed;

        public MacAudioRecorder(int? microphoneDeviceIndex = null)
        {
            this.microphoneDeviceIndex = microphoneDeviceIndex;
        }

        public bool IsMicrophone => microphoneDeviceIndex.HasValue;

        public void StartRecording()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopped) throw new InvalidOperationException("Create a new recorder to start another recording.");
            if (captureTask != null) return;
            if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
                throw new PlatformNotSupportedException("Native audio capture requires macOS 14.2 or later.");

            var error = new StringBuilder(1024);
            uint inputDevice = microphoneDeviceIndex.HasValue
                ? PortAudio.PortAudioRecorder.GetMacInputDeviceId(microphoneDeviceIndex.Value) : 0;
            try
            {
                capture = microphoneDeviceIndex.HasValue
                    ? tx_microphone_start(inputDevice, out sampleRate, error, error.Capacity)
                    : tx_audio_start(out sampleRate, error, error.Capacity);
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("The native macOS audio library is missing. Rebuild on macOS with Xcode Command Line Tools installed, or reinstall the macOS release.", ex);
            }
            if (capture == IntPtr.Zero)
                throw new InvalidOperationException(error.ToString());

            captureTask = Task.Run(CaptureLoop);
        }

        private async Task CaptureLoop()
        {
            // Read at most 50 ms at a time. Both native and managed queues are bounded;
            // falling behind fails explicitly rather than losing transcript time.
            var buffer = new byte[sampleRate / 20 * 2];
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    int frames = tx_audio_read(capture, buffer, buffer.Length / 2);
                    if (frames < 0)
                        throw new InvalidOperationException(frames switch
                        {
                            -2 => "The audio format or output device changed. Stop and start recording again to use the new audio output.",
                            -3 => "Microphone capture failed. Check the input device and microphone permission, then restart recording.",
                            _ => "Audio capture fell behind. Stop recording and try a smaller Whisper model."
                        });
                    if (frames == 0)
                    {
                        await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    var chunk = buffer.AsSpan(0, frames * 2).ToArray();
                    Volatile.Write(ref currentLevel, AudioUtils.CalculateMaxLevel(chunk));
                    if (!audioChunks.TryAdd(chunk))
                        throw new InvalidOperationException("Transcription cannot keep up with audio capture. Stop recording and try a smaller Whisper model.");
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                captureError = ex;
            }
            finally
            {
                tx_audio_stop(capture);
                capture = IntPtr.Zero;
                audioChunks.CompleteAdding();
            }
        }

        public void StopRecording()
        {
            if (stopped) return;
            stopped = true;
            cancellation.Cancel();
            captureTask?.GetAwaiter().GetResult();
            audioChunks.CompleteAdding();
            Volatile.Write(ref currentLevel, 0);
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            foreach (var chunk in audioChunks.GetConsumingEnumerable()) yield return chunk;
            if (captureError != null) throw new InvalidOperationException("Audio capture stopped: " + captureError.Message, captureError);
        }

        public float GetCurrentAudioLevel() => Volatile.Read(ref currentLevel);
        public int GetBytesPerSecond() => sampleRate * 2;

        public MemoryStream CreateWavStream(byte[] audioData)
        {
            using var source = new RawSourceWaveStream(new MemoryStream(audioData), new WaveFormat(sampleRate, 16, 1));
            ISampleProvider samples = source.ToSampleProvider();
            if (sampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
            var pcm = new SampleToWaveProvider16(samples);
            using var output = new MemoryStream();
            using (var writer = new WaveFileWriter(output, pcm.WaveFormat))
            {
                var buffer = new byte[32000];
                int count;
                while ((count = pcm.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, count);
            }
            return new MemoryStream(output.ToArray());
        }

        public void Dispose()
        {
            if (disposed) return;
            StopRecording();
            cancellation.Dispose();
            disposed = true;
        }

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tx_audio_start(out int sampleRate, StringBuilder error, int errorCapacity);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tx_microphone_start(uint device, out int sampleRate, StringBuilder error, int errorCapacity);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int tx_audio_read(IntPtr capture, [Out] byte[] buffer, int maxFrames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void tx_audio_stop(IntPtr capture);
    }
}
