namespace Transcribe
{
    public static class AudioCaptureFactory
    {
        public static IAudioRecorder CreateMicrophoneRecorder(int deviceIndex = 0, int sampleRate = 16000, int channels = 1, bool echoCancellation = false, string? recordingPath = null)
        {
            if (echoCancellation)
            {
                if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
                    throw new PlatformNotSupportedException("Echo cancellation currently requires macOS 14.2 or later.");
                if (sampleRate != 16000 || channels != 1)
                    throw new ArgumentException("Echo-cancelled transcription requires 16 kHz mono output.");
                return new MacAudio.MacAudioRecorder(deviceIndex, recordingPath);
            }
            if (OperatingSystem.IsWindows())
            {
                return new MicrophoneRecorder(sampleRate, channels, deviceIndex);
            }
            
            return new PortAudio.PortAudioRecorder(deviceIndex, sampleRate, channels, recordingPath: recordingPath);
        }

        public static IAudioRecorder CreateSpeakerRecorder(int deviceIndex = 0, string? recordingPath = null)
        {
            if (OperatingSystem.IsWindows())
            {
                return new SpeakerRecorder(deviceIndex);
            }

            if (OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            {
                if (deviceIndex != 0)
                    throw new ArgumentOutOfRangeException(nameof(deviceIndex), "Native macOS capture uses device 0 (System Audio). Use list-speakers to see available devices.");
                return new MacAudio.MacAudioRecorder(recordingPath: recordingPath);
            }

            // Older Macs and Linux use a loopback/monitor input.
            return new PortAudio.PortAudioRecorder(deviceIndex, 16000, 1, isLoopback: true, recordingPath: recordingPath);
        }

        public static IEnumerable<AudioDeviceInfo> GetMicrophones()
        {
            if (OperatingSystem.IsWindows())
            {
                return MicrophoneRecorder.GetMicrophones();
            }
            return PortAudio.PortAudioRecorder.GetDevices(input: true);
        }

        public static IEnumerable<AudioDeviceInfo> GetSpeakers()
        {
            if (OperatingSystem.IsWindows())
            {
                return SpeakerRecorder.GetSpeakers();
            }
            if (OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            {
                return new[] { new AudioDeviceInfo(0, "System Audio (Native)", 1) };
            }
            return PortAudio.PortAudioRecorder.GetDevices(input: false);
        }

        public static void ListMicrophones()
        {
            var devices = GetMicrophones();
            Console.WriteLine("Available microphone devices:");
            foreach (var device in devices)
            {
                Console.WriteLine($"  [{device.Index}] {device.Name} ({device.Channels}ch)");
            }
        }

        public static void ListSpeakers()
        {
            var devices = GetSpeakers();
            Console.WriteLine("Available speaker/loopback devices:");
            foreach (var device in devices)
            {
                Console.WriteLine($"  [{device.Index}] {device.Name} ({device.Channels}ch)");
            }
        }
    }
}
