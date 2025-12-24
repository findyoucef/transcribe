namespace Transcribe
{
    public interface IAudioRecorder : IDisposable
    {
        void StartRecording();
        void StopRecording();
        float GetCurrentAudioLevel();
        IEnumerable<byte[]> GetAudioChunks();
        MemoryStream CreateWavStream(byte[] audioData);
        int GetBytesPerSecond();
        bool IsMicrophone { get; }
    }
}
