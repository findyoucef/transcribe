using System.Runtime.InteropServices;
using System.IO;

namespace Transcribe.PortAudio
{
    internal static class PortAudioNative
    {
        private const string LibraryName = "portaudio";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_Initialize();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_Terminate();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Pa_GetDeviceCount();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Pa_GetDefaultInputDevice();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Pa_GetDefaultOutputDevice();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Pa_GetDeviceInfo(int deviceIndex);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_OpenStream(
            out IntPtr stream,
            ref PaStreamParameters inputParameters,
            IntPtr outputParameters, // Pass IntPtr.Zero if unused
            double sampleRate,
            uint framesPerBuffer,
            PaStreamFlags streamFlags,
            IntPtr streamCallback,
            IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_StartStream(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_StopStream(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_CloseStream(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_ReadStream(IntPtr stream, byte[] buffer, uint frames);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PaError Pa_ReadStream(IntPtr stream, IntPtr buffer, uint frames);

        [StructLayout(LayoutKind.Sequential)]
        public struct PaStreamParameters
        {
            public int device;
            public int channelCount;
            public PaSampleFormat sampleFormat;
            public double suggestedLatency;
            public IntPtr hostApiSpecificStreamInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PaDeviceInfo
        {
            public int structVersion;
            public IntPtr name;
            public int hostApi;
            public int maxInputChannels;
            public int maxOutputChannels;
            public double defaultLowInputLatency;
            public double defaultLowOutputLatency;
            public double defaultHighInputLatency;
            public double defaultHighOutputLatency;
            public double defaultSampleRate;
        }

        public enum PaError
        {
            paNoError = 0,
            paNotInitialized = -10000,
            paUnanticipatedHostError,
            paInvalidChannelCount,
            paInvalidSampleRate,
            paInvalidDevice,
            paInvalidFlag,
            paSampleFormatNotSupported,
            paBadIODeviceCombination,
            paInsufficientMemory,
            paBufferTooBig,
            paBufferTooSmall,
            paNullCallback,
            paBadStreamPtr,
            paTimedOut,
            paInternalError,
            paDeviceUnavailable,
            paIncompatibleHostApiSpecificStreamInfo,
            paStreamIsStopped,
            paStreamIsNotStopped,
            paInputOverflowed,
            paOutputUnderflowed,
            paHostApiNotFound,
            paInvalidHostApi,
            paCanNotReadFromACallbackStream,
            paCanNotWriteToACallbackStream,
            paCanNotReadFromAnOutputOnlyStream,
            paCanNotWriteToAnInputOnlyStream,
            paIncompatibleStreamHostApi,
            paBadBufferPtr
        }

        public enum PaSampleFormat : ulong
        {
            paFloat32 = 0x00000001,
            paInt32 = 0x00000002,
            paInt24 = 0x00000004,
            paInt16 = 0x00000008,
            paInt8 = 0x00000010,
            paUInt8 = 0x00000020,
            paCustomFormat = 0x00010000,
            paNonInterleaved = 0x80000000
        }

        public enum PaStreamFlags : ulong
        {
            paNoFlag = 0,
            paClipOff = 0x00000001,
            paDitherOff = 0x00000002,
            paNeverDropInput = 0x00000004,
            paPrimeOutputBuffersUsingStreamCallback = 0x00000008,
            paPlatformSpecificFlags = 0xFFFF0000
        }

        static PortAudioNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(PortAudioNative).Assembly, ImportResolver);
        }

        private static IntPtr ImportResolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != LibraryName)
            {
                return IntPtr.Zero;
            }

            if (NativeLibrary.TryLoad("libportaudio.2.dylib", assembly, searchPath, out var handle))
            {
                return handle;
            }
            if (NativeLibrary.TryLoad("libportaudio.dylib", assembly, searchPath, out handle))
            {
                return handle;
            }
            if (NativeLibrary.TryLoad("libportaudio.so.2", assembly, searchPath, out handle))
            {
                return handle;
            }
            if (NativeLibrary.TryLoad("libportaudio.so", assembly, searchPath, out handle))
            {
                return handle;
            }
            if (NativeLibrary.TryLoad("portaudio.dll", assembly, searchPath, out handle))
            {
                return handle;
            }

            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var candidates = new[]
                {
                    "libportaudio.2.dylib",
                    "libportaudio.dylib",
                    "libportaudio.so.2",
                    "libportaudio.so",
                    "portaudio.dll"
                };

                foreach (var candidate in candidates)
                {
                    var path = Path.Combine(baseDir, candidate);
                    if (NativeLibrary.TryLoad(path, assembly, searchPath, out handle))
                    {
                        return handle;
                    }
                }
            }

            // Fallback for direct linkage or system path
            return NativeLibrary.Load(LibraryName, assembly, searchPath);
        }
    }
}
