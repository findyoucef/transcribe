using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

namespace Transcribe
{

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("Transcribe audio using Whisper");

            // File transcription command
            var fileCommand = CreateFileCommand();
            rootCommand.AddCommand(fileCommand);

            // Microphone transcription command
            var micCommand = CreateMicCommand();
            rootCommand.AddCommand(micCommand);

            // Speaker transcription command
            var speakerCommand = CreateSpeakerCommand();
            rootCommand.AddCommand(speakerCommand);

            // List devices command
            var listDevicesCommand = CreateListDevicesCommand();
            rootCommand.AddCommand(listDevicesCommand);

            // List speakers command
            var listSpeakersCommand = CreateListSpeakersCommand();
            rootCommand.AddCommand(listSpeakersCommand);

            return await rootCommand.InvokeAsync(args);
        }

        private static Command CreateFileCommand()
        {
            var wavArgument = new Argument<string>(
                name: "wav",
                description: "Path to the WAV file to transcribe")
            {
                Arity = ArgumentArity.ExactlyOne
            };

            var modelOption = new Option<string>(
                name: "--model",
                description: $"Whisper model to use: {string.Join(", ", TranscribeDefaults.SupportedModels)} (default: {TranscribeDefaults.DefaultModel})",
                getDefaultValue: () => TranscribeDefaults.DefaultModel);

            var languageOption = new Option<string>(
                name: "--language",
                description: $"Language code for transcription (e.g., en, fr, es) or auto. Default: {TranscribeDefaults.DefaultLanguage}",
                getDefaultValue: () => TranscribeDefaults.DefaultLanguage);

            var outputDirOption = new Option<string?>(
                name: "--output-dir",
                description: "Directory to write the transcript file. Default: current working directory.",
                getDefaultValue: () => null);

            var fileCommand = new Command("file", "Transcribe a WAV file")
            {
                wavArgument,
                modelOption,
                languageOption,
                outputDirOption
            };

            fileCommand.SetHandler(async (string wavPath, string model, string language, string? outputDir) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(wavPath))
                    {
                        Console.Error.WriteLine("A WAV file path is required.");
                        return;
                    }

                    var stopwatch = Stopwatch.StartNew();

                    IProgress<int> progress = new Progress<int>(p =>
                    {
                        Console.Write($"\rTranscribing: {p}%");
                        if (p >= 100)
                        {
                            Console.WriteLine();
                        }
                    });

                    await WhisperClient.TranscribeAudioAsync(wavPath, progress, model, language, outputDirectory: outputDir);

                    stopwatch.Stop();
                    Console.WriteLine($"Transcription completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
            }, wavArgument, modelOption, languageOption, outputDirOption);

            return fileCommand;
        }

        private static Command CreateMicCommand()
        {
            var modelOption = new Option<string>(
                name: "--model",
                description: $"Whisper model to use: {string.Join(", ", TranscribeDefaults.SupportedModels)} (default: {TranscribeDefaults.DefaultModel})",
                getDefaultValue: () => TranscribeDefaults.DefaultModel);

            var languageOption = new Option<string>(
                name: "--language",
                description: $"Language code for transcription (e.g., en, fr, es) or auto. Default: {TranscribeDefaults.DefaultLanguage}",
                getDefaultValue: () => TranscribeDefaults.DefaultLanguage);

            var chunkSizeOption = new Option<int>(
                name: "--chunk-size",
                description: $"Audio chunk size in seconds for processing. Default: {TranscribeDefaults.DefaultChunkSeconds}",
                getDefaultValue: () => TranscribeDefaults.DefaultChunkSeconds);

            var deviceOption = new Option<int>(
                name: "--device",
                description: "Microphone device number to use (use 'list-devices' to see available devices). Default: 0",
                getDefaultValue: () => 0);

            var summarizeOption = new Option<bool>(
                name: "--summarize",
                description: $"Summarize the final transcript with Ollama ({TranscribeDefaults.DefaultOllamaUrl}, model {TranscribeDefaults.DefaultOllamaModel}).",
                getDefaultValue: () => false);

            var outputDirOption = new Option<string?>(
                name: "--output-dir",
                description: "Directory to write the transcript file. Default: current working directory.",
                getDefaultValue: () => null);

            var micCommand = new Command("mic", "Transcribe microphone input in real-time")
            {
                modelOption,
                languageOption,
                chunkSizeOption,
                deviceOption,
                summarizeOption,
                outputDirOption
            };

            micCommand.SetHandler(async (string model, string language, int chunkSize, int device, bool summarize, string? outputDir) =>
            {
                try
                {
                    var modelPath = await WhisperClient.EnsureModelDownloaded(model);
                    
                    Console.WriteLine($"Starting microphone transcription using device {device}. Press Ctrl+C to stop...");
                    Console.WriteLine();

                    var micTranscript = ResolveTranscriptPath($"{Guid.NewGuid():N}.txt", outputDir);
                    {
                        using var recorder = new MicrophoneRecorder(deviceNumber: device);
                        using var processor = new WhisperStreamProcessor(modelPath, language, recorder, chunkSize, transcriptFilePath: micTranscript);

                        recorder.StartRecording();
                        processor.StartProcessing();

                        // Wait for Ctrl+C
                        var tcs = new TaskCompletionSource<bool>();
                        Console.CancelKeyPress += (s, e) =>
                        {
                            e.Cancel = true;
                            tcs.TrySetResult(true);
                        };

                        await tcs.Task;

                        Console.WriteLine();
                        Console.WriteLine("Stopping transcription...");
                        
                        recorder.StopRecording();
                        await processor.StopProcessing();
                    }

                    // Summarize after resources are disposed
                    if (summarize)
                    {
                        try
                        {
                            var transcriptText = await File.ReadAllTextAsync(micTranscript);
                            var ollama = new transcribe.OllamaClient("http://localhost:11434/", "tx-agent:latest");
                            await ollama.SummarizeTranscript(transcriptText);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Summarization error: {ex.Message}");
                        }
                    }

                    Console.WriteLine("Transcription stopped.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
            }, modelOption, languageOption, chunkSizeOption, deviceOption, summarizeOption, outputDirOption);

            return micCommand;
        }

        private static Command CreateListDevicesCommand()
        {
            var listDevicesCommand = new Command("list-devices", "List available microphone devices");

            listDevicesCommand.SetHandler(() =>
            {
                MicrophoneRecorder.ListMicrophones();
            });

            return listDevicesCommand;
        }

        private static Command CreateSpeakerCommand()
        {
            var modelOption = new Option<string>(
                name: "--model",
                description: $"Whisper model to use: {string.Join(", ", TranscribeDefaults.SupportedModels)} (default: {TranscribeDefaults.DefaultModel})",
                getDefaultValue: () => TranscribeDefaults.DefaultModel);

            var languageOption = new Option<string>(
                name: "--language",
                description: $"Language code for transcription (e.g., en, fr, es) or auto. Default: {TranscribeDefaults.DefaultLanguage}",
                getDefaultValue: () => TranscribeDefaults.DefaultLanguage);

            var chunkSizeOption = new Option<int>(
                name: "--chunk-size",
                description: $"Audio chunk size in seconds for processing. Default: {TranscribeDefaults.DefaultChunkSeconds}",
                getDefaultValue: () => TranscribeDefaults.DefaultChunkSeconds);

            var deviceOption = new Option<int>(
                name: "--device",
                description: "Speaker/output device number to use (use 'list-speakers' to see available devices). Default: 0",
                getDefaultValue: () => 0);

            var micDeviceOption = new Option<int>(
                name: "--mic-device",
                description: "Microphone device number to capture your voice (use 'list-devices' to see available devices). Set to -1 to disable mic (default).",
                getDefaultValue: () => -1);

            var summarizeOption = new Option<bool>(
                name: "--summarize",
                description: $"Summarize the final transcript with Ollama ({TranscribeDefaults.DefaultOllamaUrl}, model {TranscribeDefaults.DefaultOllamaModel}).",
                getDefaultValue: () => false);

            var outputDirOption = new Option<string?>(
                name: "--output-dir",
                description: "Directory to write the transcript file. Default: current working directory.",
                getDefaultValue: () => null);

            var speakerCommand = new Command("speaker", "Transcribe speaker/output audio and microphone in real-time")
            {
                modelOption,
                languageOption,
                chunkSizeOption,
                deviceOption,
                micDeviceOption,
                summarizeOption,
                outputDirOption
            };

            speakerCommand.SetHandler(async (string model, string language, int chunkSize, int device, int micDevice, bool summarize, string? outputDir) =>
            {
                try
                {
                    var modelPath = await WhisperClient.EnsureModelDownloaded(model);
                    
                    Console.WriteLine($"Starting conversation transcription:");
                    Console.WriteLine($"  Speaker device: {device}");
                    Console.WriteLine(micDevice >= 0
                        ? $"  Microphone device: {micDevice}"
                        : "  Microphone device: disabled");
                    Console.WriteLine($"  Model: {model}");
                    Console.WriteLine($"  Language: {language}");
                    Console.WriteLine($"  Chunk size: {chunkSize} sec");
                    Console.WriteLine();
                    Console.WriteLine("Each source will be processed separately to minimize latency.");
                    Console.WriteLine("Press Ctrl+C to stop...");
                    Console.WriteLine();

                    using var speakerRecorder = new SpeakerRecorder(deviceNumber: device);

                    // Create shared transcript file path (GUID-based to match existing convention)
                    var sharedTranscript = ResolveTranscriptPath($"{Guid.NewGuid():N}.txt", outputDir);
                    using var merger = new TranscriptionMerger(sharedTranscript, TimeSpan.FromSeconds(3));

                    // Speaker processor
                    using var speakerProcessor = new WhisperStreamProcessor(modelPath, language, speakerRecorder, chunkSize, sourceLabel: "SPK", showLevels: false, transcriptFilePath: sharedTranscript, merger: merger);

                    // Optional mic
                    MicrophoneRecorder? micRecorder = null;
                    WhisperStreamProcessor? micProcessor = null;
                    if (micDevice >= 0)
                    {
                        micRecorder = new MicrophoneRecorder(deviceNumber: micDevice, bufferMilliseconds: 50, useWasapiExclusive: false);
                        micProcessor = new WhisperStreamProcessor(modelPath, language, micRecorder, chunkSize, sourceLabel: "MIC", showLevels: false, transcriptFilePath: sharedTranscript, merger: merger);
                    }

                    // Start speaker
                    speakerRecorder.StartRecording();
                    speakerProcessor.StartProcessing();
                    // Start mic if enabled
                    if (micRecorder != null && micProcessor != null)
                    {
                        micRecorder.StartRecording();
                        micProcessor.StartProcessing();
                    }

                    // Wait for Ctrl+C
                    var tcs = new TaskCompletionSource<bool>();
                    Console.CancelKeyPress += (s, e) =>
                    {
                        e.Cancel = true;
                        tcs.TrySetResult(true);
                    };

                    await tcs.Task;

                    Console.WriteLine();
                    Console.WriteLine("Stopping transcription...");
                    
                    speakerRecorder.StopRecording();
                    await speakerProcessor.StopProcessing();
                    if (micRecorder != null && micProcessor != null)
                    {
                        micRecorder.StopRecording();
                        await micProcessor.StopProcessing();
                    }
                    // Ensure merger closes its writer before reading the transcript
                    merger.Dispose();

                    if (summarize)
                    {
                        try
                        {
                            var transcriptText = await File.ReadAllTextAsync(sharedTranscript);
                            var ollama = new transcribe.OllamaClient("http://localhost:11434/", "tx-agent:latest");
                            await ollama.SummarizeTranscript(transcriptText);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Summarization error: {ex.Message}");
                        }
                    }

                    Console.WriteLine("Transcription stopped.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }, modelOption, languageOption, chunkSizeOption, deviceOption, micDeviceOption, summarizeOption, outputDirOption);

            return speakerCommand;
        }

        private static Command CreateListSpeakersCommand()
        {
            var listSpeakersCommand = new Command("list-speakers", "List available speaker/output devices");

            listSpeakersCommand.SetHandler(() =>
            {
                SpeakerRecorder.ListSpeakers();
            });

            return listSpeakersCommand;
        }

        private static string ResolveTranscriptPath(string fileName, string? outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return fileName;
            }

            Directory.CreateDirectory(outputDir);
            return Path.Combine(outputDir, fileName);
        }
    }
}
