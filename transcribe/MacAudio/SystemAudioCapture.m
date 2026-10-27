#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/AudioHardwareTapping.h>
#import <CoreAudio/CATapDescription.h>
#import <AudioToolbox/AudioToolbox.h>
#include <stdatomic.h>
#include <math.h>

// A single producer (Core Audio) and single consumer (.NET). Never call managed
// code, allocate, or wait for a lock on the real-time audio thread.
typedef struct {
    AudioObjectID tap;
    AudioObjectID device;
    AudioDeviceIOProcID ioProc;
    AudioUnit voiceUnit;
    float *voiceBuffer;
    UInt32 voiceMaxFrames;
    AudioStreamBasicDescription format;
    int16_t *samples;
    size_t capacity;
    _Atomic size_t written;
    _Atomic size_t read;
    _Atomic int error;
} TXAudioCapture;

enum { TXAudioOverflow = -1, TXAudioFormatChanged = -2, TXAudioInputFailed = -3 };

static const AudioObjectPropertyAddress defaultOutput = {
    kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
};

static OSStatus outputChanged(AudioObjectID object, UInt32 count,
                              const AudioObjectPropertyAddress *addresses, void *context)
{
    TXAudioCapture *capture = context;
    atomic_store(&capture->error, TXAudioFormatChanged);
    return noErr;
}

static OSStatus captureAudio(AudioObjectID device, const AudioTimeStamp *now,
                            const AudioBufferList *input, const AudioTimeStamp *inputTime,
                            AudioBufferList *output, const AudioTimeStamp *outputTime, void *context)
{
    TXAudioCapture *capture = context;
    if (atomic_load(&capture->error) != 0 || input->mNumberBuffers == 0) return noErr;

    UInt32 channels = 0;
    UInt32 frames = 0;
    for (UInt32 b = 0; b < input->mNumberBuffers; b++) {
        const AudioBuffer *buffer = &input->mBuffers[b];
        if (buffer->mNumberChannels == 0) continue;
        UInt32 count = buffer->mDataByteSize / (sizeof(float) * buffer->mNumberChannels);
        if (channels == 0) frames = count;
        if (count != frames) {
            atomic_store(&capture->error, TXAudioFormatChanged);
            return noErr;
        }
        channels += buffer->mNumberChannels;
    }
    if (channels != capture->format.mChannelsPerFrame) {
        atomic_store(&capture->error, TXAudioFormatChanged);
        return noErr;
    }

    size_t written = atomic_load_explicit(&capture->written, memory_order_relaxed);
    size_t read = atomic_load_explicit(&capture->read, memory_order_acquire);
    if (frames > capture->capacity - (written - read)) {
        atomic_store(&capture->error, TXAudioOverflow);
        return noErr;
    }
    for (UInt32 frame = 0; frame < frames; frame++) {
        float sample = 0;
        for (UInt32 b = 0; b < input->mNumberBuffers; b++) {
            const AudioBuffer *buffer = &input->mBuffers[b];
            const float *data = buffer->mData;
            if (data == NULL) continue; // Core Audio may supply silence without storage.
            for (UInt32 ch = 0; ch < buffer->mNumberChannels; ch++) {
                sample += data[frame * buffer->mNumberChannels + ch];
            }
        }
        sample /= channels;
        if (!isfinite(sample)) sample = 0;
        sample = fmaxf(-1, fminf(1, sample));
        capture->samples[(written + frame) % capture->capacity] = (int16_t)lrintf(sample * 32767);
    }
    atomic_store_explicit(&capture->written, written + frames, memory_order_release);
    return noErr;
}

static OSStatus formatChanged(AudioObjectID object, UInt32 count,
                              const AudioObjectPropertyAddress *addresses, void *context)
{
    TXAudioCapture *capture = context;
    // A different sample rate would invalidate the transcript's audio clock.
    // Stop with an actionable error rather than silently using the old format.
    AudioStreamBasicDescription format = {0};
    UInt32 size = sizeof(format);
    AudioObjectPropertyAddress address = { kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    OSStatus status = AudioObjectGetPropertyData(object, &address, 0, NULL, &size, &format);
    if (status != noErr || memcmp(&format, &capture->format, sizeof(format)) != 0)
        atomic_store(&capture->error, TXAudioFormatChanged);
    return noErr;
}

static const AudioObjectPropertyAddress tapFormat = {
    kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
};

void tx_audio_stop(TXAudioCapture *capture)
{
    if (capture == NULL) return;
    if (capture->voiceUnit != NULL) {
        AudioObjectRemovePropertyListener(kAudioObjectSystemObject, &defaultOutput, outputChanged, capture);
        AudioOutputUnitStop(capture->voiceUnit);
        AudioUnitUninitialize(capture->voiceUnit);
        AudioComponentInstanceDispose(capture->voiceUnit);
    }
    if (capture->ioProc != NULL) {
        AudioDeviceStop(capture->device, capture->ioProc);
        AudioDeviceDestroyIOProcID(capture->device, capture->ioProc);
    }
    if (capture->device != kAudioObjectUnknown) AudioHardwareDestroyAggregateDevice(capture->device);
    if (capture->tap != kAudioObjectUnknown) {
        AudioObjectRemovePropertyListener(capture->tap, &tapFormat, formatChanged, capture);
        AudioHardwareDestroyProcessTap(capture->tap);
    }
    free(capture->samples);
    free(capture->voiceBuffer);
    free(capture);
}

static OSStatus voiceInput(void *context, AudioUnitRenderActionFlags *flags,
                          const AudioTimeStamp *time, UInt32 bus, UInt32 frames, AudioBufferList *unused)
{
    TXAudioCapture *capture = context;
    if (frames > capture->voiceMaxFrames) {
        atomic_store(&capture->error, TXAudioInputFailed);
        return kAudioUnitErr_TooManyFramesToProcess;
    }
    AudioBufferList input = { .mNumberBuffers = 1,
        .mBuffers = {{1, frames * sizeof(float), capture->voiceBuffer}} };
    OSStatus status = AudioUnitRender(capture->voiceUnit, flags, time, 1, frames, &input);
    if (status != noErr) {
        atomic_store(&capture->error, TXAudioInputFailed);
        return status;
    }
    return captureAudio(0, time, &input, time, NULL, NULL, capture);
}

static OSStatus silentOutput(void *context, AudioUnitRenderActionFlags *flags,
                             const AudioTimeStamp *time, UInt32 bus, UInt32 frames, AudioBufferList *output)
{
    // AUVoiceIO monitors the Mac's output for its echo reference. Never feed the
    // microphone back to the speakers or replay the system audio tap.
    for (UInt32 i = 0; i < output->mNumberBuffers; i++)
        if (output->mBuffers[i].mData != NULL)
            memset(output->mBuffers[i].mData, 0, output->mBuffers[i].mDataByteSize);
    *flags |= kAudioUnitRenderAction_OutputIsSilence;
    return noErr;
}

TXAudioCapture *tx_microphone_start(AudioObjectID inputDevice, int *sampleRate, char *error, int errorCapacity)
{
    TXAudioCapture *capture = calloc(1, sizeof(TXAudioCapture));
    if (capture == NULL) {
        snprintf(error, errorCapacity, "Unable to allocate microphone capture.");
        return NULL;
    }
    const char *operation = "find the current output device";
    AudioObjectID outputDevice = kAudioObjectUnknown;
    UInt32 size = sizeof(outputDevice);
    OSStatus status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &defaultOutput, 0, NULL, &size, &outputDevice);
    if (status != noErr) goto failed;

    AudioObjectID devices[] = { inputDevice, outputDevice };
    for (int i = 0; i < 2; i++) {
        UInt32 transport = 0;
        size = sizeof(transport);
        AudioObjectPropertyAddress address = { kAudioDevicePropertyTransportType, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
        operation = "check the microphone and output devices";
        status = AudioObjectGetPropertyData(devices[i], &address, 0, NULL, &size, &transport);
        if (status != noErr) goto failed;
        if (transport == kAudioDeviceTransportTypeVirtual || transport == kAudioDeviceTransportTypeAggregate) {
            snprintf(error, errorCapacity, "Echo cancellation needs a hardware microphone and speaker/headphone output. Select your normal devices or turn echo cancellation off.");
            tx_audio_stop(capture);
            return NULL;
        }
    }

    operation = "create Apple's voice processing unit";
    AudioComponentDescription description = {
        .componentType = kAudioUnitType_Output,
        .componentSubType = kAudioUnitSubType_VoiceProcessingIO,
        .componentManufacturer = kAudioUnitManufacturer_Apple
    };
    AudioComponent component = AudioComponentFindNext(NULL, &description);
    status = component ? AudioComponentInstanceNew(component, &capture->voiceUnit) : kAudioUnitErr_InvalidProperty;
    if (status != noErr) goto failed;
    operation = "select the microphone";
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioOutputUnitProperty_CurrentDevice,
                                 kAudioUnitScope_Global, 1, &inputDevice, sizeof(inputDevice));
    if (status != noErr) goto failed;
    operation = "select the echo reference output";
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioOutputUnitProperty_CurrentDevice,
                                 kAudioUnitScope_Global, 0, &outputDevice, sizeof(outputDevice));
    if (status != noErr) goto failed;

    UInt32 enabled = 1, disabled = 0;
    operation = "enable microphone input";
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioOutputUnitProperty_EnableIO,
                                 kAudioUnitScope_Input, 1, &enabled, sizeof(enabled));
    if (status != noErr) goto failed;
    operation = "enable echo cancellation";
    status = AudioUnitSetProperty(capture->voiceUnit, kAUVoiceIOProperty_BypassVoiceProcessing,
                                 kAudioUnitScope_Global, 0, &disabled, sizeof(disabled));
    if (status != noErr) goto failed;
    // Keep levels stable for transcription; echo/noise processing stays enabled.
    operation = "disable automatic microphone gain";
    status = AudioUnitSetProperty(capture->voiceUnit, kAUVoiceIOProperty_VoiceProcessingEnableAGC,
                                 kAudioUnitScope_Global, 0, &disabled, sizeof(disabled));
    if (status != noErr) goto failed;
    AUVoiceIOOtherAudioDuckingConfiguration ducking = {
        .mEnableAdvancedDucking = false, .mDuckingLevel = kAUVoiceIOOtherAudioDuckingLevelMin
    };
    operation = "minimize playback ducking";
    status = AudioUnitSetProperty(capture->voiceUnit, kAUVoiceIOProperty_OtherAudioDuckingConfiguration,
                                 kAudioUnitScope_Global, 0, &ducking, sizeof(ducking));
    if (status != noErr) goto failed;

    capture->format = (AudioStreamBasicDescription){
        .mSampleRate = 48000, .mFormatID = kAudioFormatLinearPCM,
        .mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        .mBytesPerPacket = sizeof(float), .mFramesPerPacket = 1,
        .mBytesPerFrame = sizeof(float), .mChannelsPerFrame = 1, .mBitsPerChannel = 32
    };
    operation = "configure voice processing audio format";
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioUnitProperty_StreamFormat,
                                 kAudioUnitScope_Output, 1, &capture->format, sizeof(capture->format));
    if (status != noErr) goto failed;
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioUnitProperty_StreamFormat,
                                 kAudioUnitScope_Input, 0, &capture->format, sizeof(capture->format));
    if (status != noErr) goto failed;
    AURenderCallbackStruct inputCallback = { voiceInput, capture };
    AURenderCallbackStruct outputCallback = { silentOutput, capture };
    operation = "connect voice processing callbacks";
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioOutputUnitProperty_SetInputCallback,
                                 kAudioUnitScope_Global, 1, &inputCallback, sizeof(inputCallback));
    if (status != noErr) goto failed;
    status = AudioUnitSetProperty(capture->voiceUnit, kAudioUnitProperty_SetRenderCallback,
                                 kAudioUnitScope_Input, 0, &outputCallback, sizeof(outputCallback));
    if (status != noErr) goto failed;
    operation = "initialize voice processing";
    status = AudioUnitInitialize(capture->voiceUnit);
    if (status != noErr) goto failed;
    size = sizeof(capture->voiceMaxFrames);
    status = AudioUnitGetProperty(capture->voiceUnit, kAudioUnitProperty_MaximumFramesPerSlice,
                                 kAudioUnitScope_Global, 0, &capture->voiceMaxFrames, &size);
    if (status != noErr) goto failed;
    capture->capacity = 48000 * 10;
    capture->samples = calloc(capture->capacity, sizeof(int16_t));
    capture->voiceBuffer = calloc(capture->voiceMaxFrames, sizeof(float));
    if (capture->samples == NULL || capture->voiceBuffer == NULL) {
        snprintf(error, errorCapacity, "Unable to allocate microphone buffers.");
        tx_audio_stop(capture);
        return NULL;
    }
    operation = "monitor the echo reference output";
    status = AudioObjectAddPropertyListener(kAudioObjectSystemObject, &defaultOutput, outputChanged, capture);
    if (status != noErr) goto failed;
    operation = "start echo-cancelled microphone recording";
    status = AudioOutputUnitStart(capture->voiceUnit);
    if (status != noErr) goto failed;
    *sampleRate = 48000;
    return capture;

failed:
    snprintf(error, errorCapacity, "Could not %s (Core Audio error %d). Check Microphone permission in System Settings > Privacy & Security, or turn echo cancellation off for this device.", operation, (int)status);
    tx_audio_stop(capture);
    return NULL;
}

// Returns a handle only after all resources are initialized. On failure, the
// caller receives a useful error and no tap, aggregate device, or callback survives.
TXAudioCapture *tx_audio_start(int *sampleRate, char *error, int errorCapacity)
{
    @autoreleasepool {
        TXAudioCapture *capture = calloc(1, sizeof(TXAudioCapture));
        if (capture == NULL) {
            snprintf(error, errorCapacity, "Unable to allocate system audio capture.");
            return NULL;
        }
        const char *operation = "create system audio tap";
        NSDictionary *deviceDescription = nil;
        CATapDescription *description = [[CATapDescription alloc] initMonoGlobalTapButExcludeProcesses:@[]];
        description.name = @"Transcribe System Audio";
        description.UUID = [NSUUID UUID];
        description.privateTap = YES;
        description.muteBehavior = CATapUnmuted;
        OSStatus status = AudioHardwareCreateProcessTap(description, &capture->tap);
        if (status != noErr) goto failed;

        operation = "read system audio format";
        UInt32 size = sizeof(capture->format);
        status = AudioObjectGetPropertyData(capture->tap, &tapFormat, 0, NULL, &size, &capture->format);
        if (status != noErr) goto failed;
        AudioStreamBasicDescription format = capture->format;
        if (format.mFormatID != kAudioFormatLinearPCM || !(format.mFormatFlags & kAudioFormatFlagIsFloat)
            || (format.mFormatFlags & kAudioFormatFlagIsBigEndian) || format.mBitsPerChannel != 32
            || format.mChannelsPerFrame == 0 || format.mSampleRate < 8000 || format.mSampleRate > 192000
            || format.mBytesPerFrame != sizeof(float) * ((format.mFormatFlags & kAudioFormatFlagIsNonInterleaved) ? 1 : format.mChannelsPerFrame)) {
            snprintf(error, errorCapacity, "Unsupported Core Audio tap format. Select a standard audio output and try again.");
            tx_audio_stop(capture);
            return NULL;
        }
        capture->capacity = (size_t)format.mSampleRate * 10;
        capture->samples = calloc(capture->capacity, sizeof(int16_t));
        if (capture->samples == NULL) {
            snprintf(error, errorCapacity, "Unable to allocate system audio buffer.");
            tx_audio_stop(capture);
            return NULL;
        }

        operation = "create system audio capture device";
        deviceDescription = @{
            @kAudioAggregateDeviceNameKey: @"Transcribe System Audio",
            @kAudioAggregateDeviceUIDKey: [[NSUUID UUID] UUIDString],
            @kAudioAggregateDeviceIsPrivateKey: @YES,
            @kAudioAggregateDeviceTapAutoStartKey: @NO,
            @kAudioAggregateDeviceTapListKey: @[@{
                @kAudioSubTapUIDKey: [description.UUID UUIDString],
                @kAudioSubTapDriftCompensationKey: @YES
            }]
        };
        status = AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)deviceDescription, &capture->device);
        if (status != noErr) goto failed;
        operation = "monitor system audio format";
        status = AudioObjectAddPropertyListener(capture->tap, &tapFormat, formatChanged, capture);
        if (status != noErr) goto failed;
        operation = "create system audio callback";
        status = AudioDeviceCreateIOProcID(capture->device, captureAudio, capture, &capture->ioProc);
        if (status != noErr) goto failed;
        operation = "start system audio recording";
        status = AudioDeviceStart(capture->device, capture->ioProc);
        if (status != noErr) goto failed;
        *sampleRate = (int)lrint(format.mSampleRate);
        return capture;

    failed:
        snprintf(error, errorCapacity,
                 "Could not %s (Core Audio error %d). Allow system audio recording in System Settings > Privacy & Security > Screen & System Audio Recording, then restart Transcribe. For command-line runs, check permission for your terminal app.",
                 operation, (int)status);
        tx_audio_stop(capture);
        return NULL;
    }
}

// Nonblocking read, in mono PCM16 frames. Negative values indicate capture failure.
int tx_audio_read(TXAudioCapture *capture, int16_t *destination, int maxFrames)
{
    int error = atomic_load(&capture->error);
    if (error != 0) return error;
    size_t read = atomic_load_explicit(&capture->read, memory_order_relaxed);
    size_t written = atomic_load_explicit(&capture->written, memory_order_acquire);
    size_t count = MIN(written - read, (size_t)maxFrames);
    size_t first = MIN(count, capture->capacity - read % capture->capacity);
    memcpy(destination, capture->samples + read % capture->capacity, first * sizeof(int16_t));
    memcpy(destination + first, capture->samples, (count - first) * sizeof(int16_t));
    atomic_store_explicit(&capture->read, read + count, memory_order_release);
    return (int)count;
}
