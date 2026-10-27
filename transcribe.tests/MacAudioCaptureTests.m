// Exercise the actual callback and ring buffer without recording or requesting permission.
#include "../transcribe/MacAudio/SystemAudioCapture.m"
#include <assert.h>

int main(void)
{
    TXAudioCapture capture = {0};
    int16_t storage[8] = {0};
    int16_t result[8] = {0};
    capture.samples = storage;
    capture.capacity = 8;
    capture.format.mChannelsPerFrame = 2;

    // Stereo downmix, clipping, and nonfinite values. Reading partial chunks
    // must preserve order when producer and consumer wrap around the ring.
    float stereo[] = {1, 0, -1, 0, 2, 2, NAN, 0, -2, -2};
    AudioBufferList input = { .mNumberBuffers = 1, .mBuffers = {{2, sizeof(stereo), stereo}} };
    captureAudio(0, NULL, &input, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 3) == 3);
    assert(result[0] == 16384 && result[1] == -16384 && result[2] == 32767);
    captureAudio(0, NULL, &input, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 8) == 7);
    assert(result[0] == 0 && result[1] == -32767 && result[2] == 16384 && result[6] == -32767);
    assert(tx_audio_read(&capture, result, 8) == 0);

    // Noninterleaved channels must produce the same mix as interleaved input.
    struct { UInt32 count; AudioBuffer buffers[2]; } planar;
    float left[] = {0.25, -0.25};
    float right[] = {0.75, -0.75};
    planar.count = 2;
    planar.buffers[0] = (AudioBuffer){1, sizeof(left), left};
    planar.buffers[1] = (AudioBuffer){1, sizeof(right), right};
    captureAudio(0, NULL, (AudioBufferList *)&planar, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 8) == 2);
    assert(result[0] == 16384 && result[1] == -16384);

    // Core Audio can provide a null data pointer to represent silence.
    input.mBuffers[0] = (AudioBuffer){2, 4 * sizeof(float), NULL};
    captureAudio(0, NULL, &input, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 8) == 2);
    assert(result[0] == 0 && result[1] == 0);

    // Overflow and incompatible layouts fail instead of silently skipping audio
    // or reading past the end of a channel buffer.
    input.mBuffers[0] = (AudioBuffer){2, sizeof(stereo), stereo};
    captureAudio(0, NULL, &input, NULL, NULL, NULL, &capture);
    captureAudio(0, NULL, &input, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 8) == TXAudioOverflow);
    atomic_store(&capture.error, 0);
    planar.buffers[1].mDataByteSize = sizeof(float);
    captureAudio(0, NULL, (AudioBufferList *)&planar, NULL, NULL, NULL, &capture);
    assert(tx_audio_read(&capture, result, 8) == TXAudioFormatChanged);

    // The voice output callback must never replay microphone/system audio.
    float outputSamples[] = {1, -1, 0.5, -0.5};
    AudioBufferList output = { .mNumberBuffers = 1, .mBuffers = {{1, sizeof(outputSamples), outputSamples}} };
    AudioUnitRenderActionFlags flags = 0;
    silentOutput(NULL, &flags, NULL, 0, 4, &output);
    assert(flags & kAudioUnitRenderAction_OutputIsSilence);
    for (int i = 0; i < 4; i++) assert(outputSamples[i] == 0);
    atomic_store(&capture.error, 0);
    capture.voiceMaxFrames = 4;
    assert(voiceInput(&capture, &flags, NULL, 1, 5, NULL) == kAudioUnitErr_TooManyFramesToProcess);
    assert(tx_audio_read(&capture, result, 8) == TXAudioInputFailed);
    tx_audio_stop(NULL);
    puts("Native audio buffer tests passed.");
    return 0;
}
