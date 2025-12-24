using System.CommandLine;
using System.CommandLine.Invocation;
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

            // Stream file through live pipeline (for testing)
            var streamFileCommand = CreateStreamFileCommand();
            rootCommand.AddCommand(streamFileCommand);

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

            var diarizationOptions = AddDiarizationOptions(fileCommand);

            fileCommand.SetHandler(async (InvocationContext context) =>
            {
                var wavPath = context.ParseResult.GetValueForArgument(wavArgument);
                var model = context.ParseResult.GetValueForOption(modelOption) ?? TranscribeDefaults.DefaultModel;
                var language = context.ParseResult.GetValueForOption(languageOption) ?? TranscribeDefaults.DefaultLanguage;
                var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
                var diarize = context.ParseResult.GetValueForOption(diarizationOptions.Enable);
                var diarizationModel = context.ParseResult.GetValueForOption(diarizationOptions.ModelPath);
                var diarizationModelUrl = context.ParseResult.GetValueForOption(diarizationOptions.ModelUrl);
                var diarizationConfig = context.ParseResult.GetValueForOption(diarizationOptions.ConfigPath);
                var similarityThreshold = context.ParseResult.GetValueForOption(diarizationOptions.SimilarityThreshold);
                var energyThreshold = context.ParseResult.GetValueForOption(diarizationOptions.EnergyThreshold);
                var minSpeechMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeechMs);
                var minSilenceMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSilenceMs);
                var maxSegmentSeconds = context.ParseResult.GetValueForOption(diarizationOptions.MaxSegmentSeconds);
                var windowSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowSeconds);
                var windowHopSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowHopSeconds);
                var minSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeakerWindows);
                var activeMatchMargin = context.ParseResult.GetValueForOption(diarizationOptions.ActiveMatchMargin);
                var fallbackSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.FallbackSimilarityThreshold);
                var minNewSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinNewSpeakerWindows);
                var instantSwitchSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.InstantSwitchSimilarity);
                var logScores = context.ParseResult.GetValueForOption(diarizationOptions.LogScores);
                var overlapMinSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMinSimilarity);
                var overlapMaxGap = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMaxGap);

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

                    DiarizationOptions? diarizationOptions = null;
                    if (diarize)
                    {
                        diarizationOptions = BuildDiarizationOptions(
                            diarize,
                            diarizationModel,
                            diarizationModelUrl,
                            diarizationConfig,
                        similarityThreshold,
                        energyThreshold,
                        minSpeechMs,
                        minSilenceMs,
                        maxSegmentSeconds,
                        windowSeconds,
                        windowHopSeconds,
                        minSpeakerWindows,
                        activeMatchMargin,
                        fallbackSimilarity,
                        minNewSpeakerWindows,
                        instantSwitchSimilarity,
                        logScores,
                        overlapMinSimilarity,
                        overlapMaxGap);
                    }

                    await WhisperClient.TranscribeAudioAsync(
                        wavPath,
                        progress,
                        model,
                        language,
                        outputDirectory: outputDir,
                        log: Console.WriteLine,
                        diarizationOptions: diarizationOptions);

                    stopwatch.Stop();
                    Console.WriteLine($"Transcription completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
            });

            return fileCommand;
        }

        private static Command CreateStreamFileCommand()
        {
            var wavArgument = new Argument<string>(
                name: "wav",
                description: "Path to the WAV file to stream through the live pipeline")
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

            var chunkSizeOption = new Option<int>(
                name: "--chunk-size",
                description: $"Audio chunk size in seconds for processing. Default: {TranscribeDefaults.DefaultChunkSeconds}",
                getDefaultValue: () => TranscribeDefaults.DefaultChunkSeconds);

            var outputDirOption = new Option<string?>(
                name: "--output-dir",
                description: "Directory to write the transcript file. Default: current working directory.",
                getDefaultValue: () => null);

            var streamCommand = new Command("stream-file", "Stream a WAV file through the live transcription pipeline")
            {
                wavArgument,
                modelOption,
                languageOption,
                chunkSizeOption,
                outputDirOption
            };

            var diarizationOptions = AddDiarizationOptions(streamCommand);

            streamCommand.SetHandler(async (InvocationContext context) =>
            {
                var wavPath = context.ParseResult.GetValueForArgument(wavArgument);
                var model = context.ParseResult.GetValueForOption(modelOption) ?? TranscribeDefaults.DefaultModel;
                var language = context.ParseResult.GetValueForOption(languageOption) ?? TranscribeDefaults.DefaultLanguage;
                var chunkSize = context.ParseResult.GetValueForOption(chunkSizeOption);
                var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
                var diarize = context.ParseResult.GetValueForOption(diarizationOptions.Enable);
                var diarizationModel = context.ParseResult.GetValueForOption(diarizationOptions.ModelPath);
                var diarizationModelUrl = context.ParseResult.GetValueForOption(diarizationOptions.ModelUrl);
                var diarizationConfig = context.ParseResult.GetValueForOption(diarizationOptions.ConfigPath);
                var similarityThreshold = context.ParseResult.GetValueForOption(diarizationOptions.SimilarityThreshold);
                var energyThreshold = context.ParseResult.GetValueForOption(diarizationOptions.EnergyThreshold);
                var minSpeechMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeechMs);
                var minSilenceMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSilenceMs);
                var maxSegmentSeconds = context.ParseResult.GetValueForOption(diarizationOptions.MaxSegmentSeconds);
                var windowSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowSeconds);
                var windowHopSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowHopSeconds);
                var minSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeakerWindows);
                var activeMatchMargin = context.ParseResult.GetValueForOption(diarizationOptions.ActiveMatchMargin);
                var fallbackSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.FallbackSimilarityThreshold);
                var minNewSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinNewSpeakerWindows);
                var instantSwitchSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.InstantSwitchSimilarity);
                var logScores = context.ParseResult.GetValueForOption(diarizationOptions.LogScores);
                var overlapMinSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMinSimilarity);
                var overlapMaxGap = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMaxGap);

                try
                {
                    if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
                    {
                        Console.Error.WriteLine("A valid WAV file path is required.");
                        return;
                    }

                    var diarizationSettings = BuildDiarizationOptions(
                        diarize,
                        diarizationModel,
                        diarizationModelUrl,
                        diarizationConfig,
                        similarityThreshold,
                        energyThreshold,
                        minSpeechMs,
                        minSilenceMs,
                        maxSegmentSeconds,
                        windowSeconds,
                        windowHopSeconds,
                        minSpeakerWindows,
                        activeMatchMargin,
                        fallbackSimilarity,
                        minNewSpeakerWindows,
                        instantSwitchSimilarity,
                        logScores,
                        overlapMinSimilarity,
                        overlapMaxGap);
                    DiarizationResources? diarizationResources = null;
                    if (diarizationSettings.Enabled)
                    {
                        diarizationResources = await DiarizationModelLoader.EnsureResourcesAsync(diarizationSettings, Console.WriteLine);
                    }

                    var modelPath = await WhisperClient.EnsureModelDownloaded(model);
                    var transcriptPath = ResolveTranscriptPath($"{Path.GetFileNameWithoutExtension(wavPath)}.stream.txt", outputDir);

                    Console.WriteLine($"Streaming file: {wavPath}");
                    Console.WriteLine($"  Model: {model}");
                    Console.WriteLine($"  Language: {language}");
                    Console.WriteLine($"  Chunk size: {chunkSize} sec");
                    Console.WriteLine($"  Transcript: {transcriptPath}");
                    Console.WriteLine();

                    DiarizationPipeline? diarization = null;
                    using var recorder = new FileAudioRecorder(wavPath);
                    try
                    {
                        if (diarizationResources != null)
                        {
                            var modelInstance = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                            diarization = new DiarizationPipeline(modelInstance, diarizationSettings, Console.WriteLine);
                        }

                        using var processor = new WhisperStreamProcessor(
                            modelPath,
                            language,
                            recorder,
                            chunkSize,
                            sourceLabel: "SPK",
                            showLevels: false,
                            transcriptFilePath: transcriptPath,
                            diarization: diarization);

                        recorder.StartRecording();
                        processor.StartProcessing();
                        await processor.WaitForCompletionAsync();

                        recorder.StopRecording();
                    }
                    finally
                    {
                        diarization?.Dispose();
                    }

                    Console.WriteLine("Streaming complete.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
            });

            return streamCommand;
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

            var diarizationOptions = AddDiarizationOptions(micCommand);

            micCommand.SetHandler(async (InvocationContext context) =>
            {
                var model = context.ParseResult.GetValueForOption(modelOption) ?? TranscribeDefaults.DefaultModel;
                var language = context.ParseResult.GetValueForOption(languageOption) ?? TranscribeDefaults.DefaultLanguage;
                var chunkSize = context.ParseResult.GetValueForOption(chunkSizeOption);
                var device = context.ParseResult.GetValueForOption(deviceOption);
                var summarize = context.ParseResult.GetValueForOption(summarizeOption);
                var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
                var diarize = context.ParseResult.GetValueForOption(diarizationOptions.Enable);
                var diarizationModel = context.ParseResult.GetValueForOption(diarizationOptions.ModelPath);
                var diarizationModelUrl = context.ParseResult.GetValueForOption(diarizationOptions.ModelUrl);
                var diarizationConfig = context.ParseResult.GetValueForOption(diarizationOptions.ConfigPath);
                var similarityThreshold = context.ParseResult.GetValueForOption(diarizationOptions.SimilarityThreshold);
                var energyThreshold = context.ParseResult.GetValueForOption(diarizationOptions.EnergyThreshold);
                var minSpeechMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeechMs);
                var minSilenceMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSilenceMs);
                var maxSegmentSeconds = context.ParseResult.GetValueForOption(diarizationOptions.MaxSegmentSeconds);
                var windowSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowSeconds);
                var windowHopSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowHopSeconds);
                var minSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeakerWindows);
                var activeMatchMargin = context.ParseResult.GetValueForOption(diarizationOptions.ActiveMatchMargin);
                var fallbackSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.FallbackSimilarityThreshold);
                var minNewSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinNewSpeakerWindows);
                var instantSwitchSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.InstantSwitchSimilarity);
                var logScores = context.ParseResult.GetValueForOption(diarizationOptions.LogScores);
                var overlapMinSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMinSimilarity);
                var overlapMaxGap = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMaxGap);

                try
                {
                    var diarizationOptions = BuildDiarizationOptions(
                        diarize,
                        diarizationModel,
                        diarizationModelUrl,
                        diarizationConfig,
                        similarityThreshold,
                        energyThreshold,
                        minSpeechMs,
                        minSilenceMs,
                        maxSegmentSeconds,
                        windowSeconds,
                        windowHopSeconds,
                        minSpeakerWindows,
                        activeMatchMargin,
                        fallbackSimilarity,
                        minNewSpeakerWindows,
                        instantSwitchSimilarity,
                        logScores,
                        overlapMinSimilarity,
                        overlapMaxGap);
                    DiarizationResources? diarizationResources = null;
                    if (diarizationOptions.Enabled)
                    {
                        diarizationResources = await DiarizationModelLoader.EnsureResourcesAsync(diarizationOptions, Console.WriteLine);
                    }

                    var modelPath = await WhisperClient.EnsureModelDownloaded(model);
                    
                    Console.WriteLine($"Starting microphone transcription using device {device}. Press Ctrl+C to stop...");
                    Console.WriteLine();

                    var micTranscript = ResolveTranscriptPath($"{Guid.NewGuid():N}.txt", outputDir);
                    {
                        DiarizationPipeline? diarization = null;
                        using var recorder = new MicrophoneRecorder(deviceNumber: device);
                        try
                        {
                            if (diarizationResources != null)
                            {
                                var modelInstance = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                                diarization = new DiarizationPipeline(modelInstance, diarizationOptions, Console.WriteLine);
                            }

                            using var processor = new WhisperStreamProcessor(
                                modelPath,
                                language,
                                recorder,
                                chunkSize,
                                transcriptFilePath: micTranscript,
                                diarization: diarization);

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

                            diarization = null;
                        }
                        finally
                        {
                            diarization?.Dispose();
                        }
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
            });

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

            var diarizationOptions = AddDiarizationOptions(speakerCommand);

            speakerCommand.SetHandler(async (InvocationContext context) =>
            {
                var model = context.ParseResult.GetValueForOption(modelOption) ?? TranscribeDefaults.DefaultModel;
                var language = context.ParseResult.GetValueForOption(languageOption) ?? TranscribeDefaults.DefaultLanguage;
                var chunkSize = context.ParseResult.GetValueForOption(chunkSizeOption);
                var device = context.ParseResult.GetValueForOption(deviceOption);
                var micDevice = context.ParseResult.GetValueForOption(micDeviceOption);
                var summarize = context.ParseResult.GetValueForOption(summarizeOption);
                var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
                var diarize = context.ParseResult.GetValueForOption(diarizationOptions.Enable);
                var diarizationModel = context.ParseResult.GetValueForOption(diarizationOptions.ModelPath);
                var diarizationModelUrl = context.ParseResult.GetValueForOption(diarizationOptions.ModelUrl);
                var diarizationConfig = context.ParseResult.GetValueForOption(diarizationOptions.ConfigPath);
                var similarityThreshold = context.ParseResult.GetValueForOption(diarizationOptions.SimilarityThreshold);
                var energyThreshold = context.ParseResult.GetValueForOption(diarizationOptions.EnergyThreshold);
                var minSpeechMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeechMs);
                var minSilenceMs = context.ParseResult.GetValueForOption(diarizationOptions.MinSilenceMs);
                var maxSegmentSeconds = context.ParseResult.GetValueForOption(diarizationOptions.MaxSegmentSeconds);
                var windowSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowSeconds);
                var windowHopSeconds = context.ParseResult.GetValueForOption(diarizationOptions.WindowHopSeconds);
                var minSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinSpeakerWindows);
                var activeMatchMargin = context.ParseResult.GetValueForOption(diarizationOptions.ActiveMatchMargin);
                var fallbackSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.FallbackSimilarityThreshold);
                var minNewSpeakerWindows = context.ParseResult.GetValueForOption(diarizationOptions.MinNewSpeakerWindows);
                var instantSwitchSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.InstantSwitchSimilarity);
                var logScores = context.ParseResult.GetValueForOption(diarizationOptions.LogScores);
                var overlapMinSimilarity = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMinSimilarity);
                var overlapMaxGap = context.ParseResult.GetValueForOption(diarizationOptions.OverlapMaxGap);

                try
                {
                    var diarizationOptions = BuildDiarizationOptions(
                        diarize,
                        diarizationModel,
                        diarizationModelUrl,
                        diarizationConfig,
                        similarityThreshold,
                        energyThreshold,
                        minSpeechMs,
                        minSilenceMs,
                        maxSegmentSeconds,
                        windowSeconds,
                        windowHopSeconds,
                        minSpeakerWindows,
                        activeMatchMargin,
                        fallbackSimilarity,
                        minNewSpeakerWindows,
                        instantSwitchSimilarity,
                        logScores,
                        overlapMinSimilarity,
                        overlapMaxGap);
                    DiarizationResources? diarizationResources = null;
                    if (diarizationOptions.Enabled)
                    {
                        diarizationResources = await DiarizationModelLoader.EnsureResourcesAsync(diarizationOptions, Console.WriteLine);
                    }

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
                    DiarizationPipeline? speakerDiarization = null;
                    DiarizationPipeline? micDiarization = null;
                    try
                    {
                        if (diarizationResources != null)
                        {
                            var speakerModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                            speakerDiarization = new DiarizationPipeline(speakerModel, diarizationOptions, Console.WriteLine);
                        }

                        using var speakerProcessor = new WhisperStreamProcessor(
                            modelPath,
                            language,
                            speakerRecorder,
                            chunkSize,
                            sourceLabel: "SPK",
                            showLevels: false,
                            transcriptFilePath: sharedTranscript,
                            merger: merger,
                            diarization: speakerDiarization);

                        // Optional mic
                        MicrophoneRecorder? micRecorder = null;
                        WhisperStreamProcessor? micProcessor = null;
                        if (micDevice >= 0)
                        {
                            micRecorder = new MicrophoneRecorder(deviceNumber: micDevice, bufferMilliseconds: 50, useWasapiExclusive: false);
                            if (diarizationResources != null)
                            {
                                var micModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                                micDiarization = new DiarizationPipeline(micModel, diarizationOptions, Console.WriteLine);
                            }

                            micProcessor = new WhisperStreamProcessor(
                                modelPath,
                                language,
                                micRecorder,
                                chunkSize,
                                sourceLabel: "MIC",
                                showLevels: false,
                                transcriptFilePath: sharedTranscript,
                                merger: merger,
                                diarization: micDiarization);
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

                        speakerDiarization = null;
                        micDiarization = null;
                    }
                    finally
                    {
                        micDiarization?.Dispose();
                        speakerDiarization?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            });

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

        private static DiarizationCliOptions AddDiarizationOptions(Command command)
        {
            var enableOption = new Option<bool>(
                name: "--diarize",
                description: "Enable speaker diarization labels in output.",
                getDefaultValue: () => false);

            var modelPathOption = new Option<string?>(
                name: "--diarization-model",
                description: $"Path to ONNX speaker embedding model. Default: {DiarizationModelLoader.GetDefaultModelPath()}",
                getDefaultValue: () => null);

            var modelUrlOption = new Option<string?>(
                name: "--diarization-model-url",
                description: $"URL to download the diarization ONNX model if it is missing. Default: {TranscribeDefaults.DefaultDiarizationModelUrl}",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationModelUrl);

            var configPathOption = new Option<string?>(
                name: "--diarization-config",
                description: $"Path to diarization config JSON. Default: {DiarizationModelLoader.GetDefaultConfigPath()}",
                getDefaultValue: () => null);

            var similarityThresholdOption = new Option<float>(
                name: "--diarization-similarity",
                description: "Cosine similarity threshold for clustering.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationSimilarityThreshold);

            var energyThresholdOption = new Option<float>(
                name: "--diarization-energy",
                description: "Energy threshold for speech detection.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationEnergyThreshold);

            var minSpeechOption = new Option<int>(
                name: "--diarization-min-speech-ms",
                description: "Minimum speech segment duration in milliseconds.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationMinSpeechMs);

            var minSilenceOption = new Option<int>(
                name: "--diarization-min-silence-ms",
                description: "Minimum silence to close a segment in milliseconds.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationMinSilenceMs);

            var maxSegmentOption = new Option<int>(
                name: "--diarization-max-segment-seconds",
                description: "Maximum segment duration before forcing a split.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationMaxSegmentSeconds);

            var windowSecondsOption = new Option<float>(
                name: "--diarization-window-seconds",
                description: "Sliding window size (seconds) for diarization embeddings.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationWindowSeconds);

            var windowHopOption = new Option<float>(
                name: "--diarization-window-hop-seconds",
                description: "Sliding window hop (seconds) for diarization embeddings.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationWindowHopSeconds);

            var minSpeakerWindowsOption = new Option<int>(
                name: "--diarization-min-speaker-windows",
                description: "Number of consecutive windows required to switch speakers.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationMinSpeakerWindows);

            var activeMarginOption = new Option<float>(
                name: "--diarization-active-margin",
                description: "Similarity margin to keep the active speaker.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationActiveMatchMargin);

            var fallbackSimilarityOption = new Option<float>(
                name: "--diarization-fallback-similarity",
                description: "Similarity threshold to reuse an existing speaker before creating a new one.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationFallbackSimilarity);

            var minNewSpeakerWindowsOption = new Option<int>(
                name: "--diarization-min-new-speaker-windows",
                description: "Number of consecutive windows required to create a new speaker.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationMinNewSpeakerWindows);

            var instantSwitchSimilarityOption = new Option<float>(
                name: "--diarization-instant-switch-similarity",
                description: "Similarity required to switch immediately to a known speaker.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationInstantSwitchSimilarity);

            var logScoresOption = new Option<bool>(
                name: "--diarization-log-scores",
                description: "Log diarization similarity scores for debugging.",
                getDefaultValue: () => false);

            var overlapMinSimilarityOption = new Option<float>(
                name: "--diarization-overlap-min-similarity",
                description: "Minimum similarity required for a second speaker to label overlap.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationOverlapMinSimilarity);

            var overlapMaxGapOption = new Option<float>(
                name: "--diarization-overlap-max-gap",
                description: "Maximum similarity gap between the top two speakers to label overlap.",
                getDefaultValue: () => TranscribeDefaults.DefaultDiarizationOverlapMaxGap);

            command.AddOption(enableOption);
            command.AddOption(modelPathOption);
            command.AddOption(modelUrlOption);
            command.AddOption(configPathOption);
            command.AddOption(similarityThresholdOption);
            command.AddOption(energyThresholdOption);
            command.AddOption(minSpeechOption);
            command.AddOption(minSilenceOption);
            command.AddOption(maxSegmentOption);
            command.AddOption(windowSecondsOption);
            command.AddOption(windowHopOption);
            command.AddOption(minSpeakerWindowsOption);
            command.AddOption(activeMarginOption);
            command.AddOption(fallbackSimilarityOption);
            command.AddOption(minNewSpeakerWindowsOption);
            command.AddOption(instantSwitchSimilarityOption);
            command.AddOption(logScoresOption);
            command.AddOption(overlapMinSimilarityOption);
            command.AddOption(overlapMaxGapOption);

            return new DiarizationCliOptions(
                enableOption,
                modelPathOption,
                modelUrlOption,
                configPathOption,
                similarityThresholdOption,
                energyThresholdOption,
                minSpeechOption,
                minSilenceOption,
                maxSegmentOption,
                windowSecondsOption,
                windowHopOption,
                minSpeakerWindowsOption,
                activeMarginOption,
                fallbackSimilarityOption,
                minNewSpeakerWindowsOption,
                instantSwitchSimilarityOption,
                logScoresOption,
                overlapMinSimilarityOption,
                overlapMaxGapOption);
        }

        private static DiarizationOptions BuildDiarizationOptions(
            bool enabled,
            string? modelPath,
            string? modelUrl,
            string? configPath,
            float similarityThreshold,
            float energyThreshold,
            int minSpeechMs,
            int minSilenceMs,
            int maxSegmentSeconds,
            float windowSeconds,
            float windowHopSeconds,
            int minSpeakerWindows,
            float activeMatchMargin,
            float fallbackSimilarityThreshold,
            int minNewSpeakerWindows,
            float instantSwitchSimilarity,
            bool logScores,
            float overlapMinSimilarity,
            float overlapMaxGap)
        {
            return new DiarizationOptions
            {
                Enabled = enabled,
                ModelPath = modelPath,
                ModelUrl = modelUrl,
                ConfigPath = configPath,
                SimilarityThreshold = similarityThreshold,
                EnergyThreshold = energyThreshold,
                MinSpeechMs = minSpeechMs,
                MinSilenceMs = minSilenceMs,
                MaxSegmentSeconds = maxSegmentSeconds,
                WindowSeconds = windowSeconds,
                WindowHopSeconds = windowHopSeconds,
                MinSpeakerWindows = minSpeakerWindows,
                ActiveMatchMargin = activeMatchMargin,
                FallbackSimilarityThreshold = fallbackSimilarityThreshold,
                MinNewSpeakerWindows = minNewSpeakerWindows,
                InstantSwitchSimilarity = instantSwitchSimilarity,
                LogScores = logScores,
                OverlapMinSimilarity = overlapMinSimilarity,
                OverlapMaxGap = overlapMaxGap
            };
        }

        private sealed record DiarizationCliOptions(
            Option<bool> Enable,
            Option<string?> ModelPath,
            Option<string?> ModelUrl,
            Option<string?> ConfigPath,
            Option<float> SimilarityThreshold,
            Option<float> EnergyThreshold,
            Option<int> MinSpeechMs,
            Option<int> MinSilenceMs,
            Option<int> MaxSegmentSeconds,
            Option<float> WindowSeconds,
            Option<float> WindowHopSeconds,
            Option<int> MinSpeakerWindows,
            Option<float> ActiveMatchMargin,
            Option<float> FallbackSimilarityThreshold,
            Option<int> MinNewSpeakerWindows,
            Option<float> InstantSwitchSimilarity,
            Option<bool> LogScores,
            Option<float> OverlapMinSimilarity,
            Option<float> OverlapMaxGap);
    }
}
