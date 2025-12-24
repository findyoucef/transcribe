namespace Transcribe
{
    public static class AudioCaptureFactory
    {
        public static IAudioRecorder CreateMicrophoneRecorder(int deviceIndex = 0, int sampleRate = 16000, int channels = 1)
        {
            if (OperatingSystem.IsWindows())
            {
                return new MicrophoneRecorder(sampleRate, channels, deviceIndex);
            }
            
            // Fallback to PortAudio for non-Windows platforms (or if forced)
            // Note: PortAudioRecorder implementation to follow
            return new PortAudio.PortAudioRecorder(deviceIndex, sampleRate, channels);
        }

        public static IAudioRecorder CreateSpeakerRecorder(int deviceIndex = 0)
        {
            if (OperatingSystem.IsWindows())
            {
                return new SpeakerRecorder(deviceIndex);
            }

            // On macOS/Linux, "speaker" capture often means capturing a loopback/monitor device
            // We'll use PortAudio to find such devices.
            return new PortAudio.PortAudioRecorder(deviceIndex, 16000, 1, isLoopback: true);
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
