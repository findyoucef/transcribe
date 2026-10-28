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
        private AudioSpool? audioChunks;
        private readonly string? recordingPath;
        private readonly CancellationTokenSource cancellation = new();
        private IntPtr capture;
        private Task? captureTask;
        private Exception? captureError;
        private int sampleRate = 48000;
        private float currentLevel;
        private bool stopped;
        private bool disposed;

        public MacAudioRecorder(int? microphoneDeviceIndex = null, string? recordingPath = null)
        {
            this.microphoneDeviceIndex = microphoneDeviceIndex;
            this.recordingPath = recordingPath;
        }

        public bool IsMicrophone => microphoneDeviceIndex.HasValue;
        public TimeSpan? Backlog => audioChunks?.Backlog;

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

            try { audioChunks = new AudioSpool(recordingPath, new WaveFormat(sampleRate, 16, 1)); }
            catch { tx_audio_stop(capture); capture = IntPtr.Zero; throw; }
            captureTask = Task.Run(CaptureLoop);
        }

        private async Task CaptureLoop()
        {
            // Drain native capture promptly. Inference reads its backlog from disk.
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
                    audioChunks!.Write(chunk);
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
                audioChunks!.Complete(captureError);
            }
        }

        public void StopRecording()
        {
            if (stopped) return;
            stopped = true;
            cancellation.Cancel();
            captureTask?.GetAwaiter().GetResult();
            audioChunks?.Complete(captureError);
            Volatile.Write(ref currentLevel, 0);
        }

        public IEnumerable<byte[]> GetAudioChunks()
        {
            if (audioChunks == null) yield break;
            foreach (var chunk in audioChunks.ReadChunks()) yield return chunk;
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
            audioChunks?.Dispose();
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
