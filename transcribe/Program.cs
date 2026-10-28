using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
            rootCommand.AddCommand(CreateRecoverCommand());

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

            // Install BlackHole command
            var installBlackHoleCommand = CreateInstallBlackHoleCommand();
            rootCommand.AddCommand(installBlackHoleCommand);

            return await rootCommand.InvokeAsync(args);
        }

        private static Command CreateRecoverCommand()
        {
            var directory = new Argument<string>("archive", "The .audio directory saved beside a meeting transcript.");
            var model = new Option<string>("--model", () => TranscribeDefaults.DefaultModel);
            var language = new Option<string>("--language", () => TranscribeDefaults.DefaultLanguage);
            var diarize = new Option<bool>("--diarize", "Rebuild speaker labels as well as text.");
            var command = new Command("recover", "Rebuild a complete transcript from saved audio without overwriting the original.")
            {
                directory, model, language, diarize
            };
            command.SetHandler(async (InvocationContext context) =>
            {
                var temporary = new List<string>();
                try
                {
                    var archive = Path.GetFullPath(context.ParseResult.GetValueForArgument(directory));
                    var output = Path.ChangeExtension(archive, "recovered.txt");
                    if (File.Exists(output)) throw new IOException($"Recovered transcript already exists: {output}");
                    var sources = Directory.GetFiles(archive, "*.wav");
                    if (sources.Length == 0) throw new IOException("No saved audio found.");
                    var modelPath = await WhisperClient.EnsureModelDownloaded(context.ParseResult.GetValueForOption(model)!);
                    var options = new DiarizationOptions { Enabled = context.ParseResult.GetValueForOption(diarize) };
                    var resources = options.Enabled ? await DiarizationModelLoader.EnsureResourcesAsync(options, Console.WriteLine) : null;
                    var lines = new List<string>();
                    foreach (var source in sources)
                    {
                        var timing = JsonSerializer.Deserialize<RecordingArchive.SourceTiming>(File.ReadAllText(Path.ChangeExtension(source, "json")))
                            ?? throw new InvalidDataException($"Missing audio timing: {source}");
                        if (timing.Source is not ("SPK" or "MIC") || !double.IsFinite(timing.OffsetSeconds) || timing.OffsetSeconds < 0)
                            throw new InvalidDataException($"Invalid audio timing: {source}");
                        var transcript = Path.GetTempFileName();
                        temporary.Add(transcript);
                        using var recorder = new FileAudioRecorder(source);
                        var pipeline = resources == null ? null : new DiarizationPipeline(new SpeakerEmbeddingModel(resources.ModelPath, resources.Config, Console.WriteLine), options, Console.WriteLine, resources.SpeechModelPath);
                        using (var processor = new WhisperStreamProcessor(modelPath, context.ParseResult.GetValueForOption(language)!, recorder,
                            sourceLabel: timing.Source, showLevels: false, transcriptFilePath: transcript, diarization: pipeline))
                        {
                            recorder.StartRecording();
                            processor.StartProcessing(TimeSpan.FromSeconds(timing.OffsetSeconds));
                            await processor.WaitForCompletionAsync();
                            recorder.StopRecording();
                        }
                        lines.AddRange(File.ReadAllLines(transcript));
                    }
                    var staging = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    temporary.Add(staging);
                    await File.WriteAllLinesAsync(staging, lines.OrderBy(line => TimeSpan.Parse(line.AsSpan(0, line.IndexOf("->", StringComparison.Ordinal)), CultureInfo.InvariantCulture)));
                    File.Move(staging, output);
                    Console.WriteLine($"Recovered transcript: {output}");
                }
                catch (Exception ex)
                {
                    context.ExitCode = 1;
                    Console.Error.WriteLine($"Recovery failed: {ex.Message}");
                }
                finally { foreach (var path in temporary) File.Delete(path); }
            });
            return command;
        }

        private static Command CreateInstallBlackHoleCommand()
        {
            var brewOption = new Option<bool>(
                name: "--brew",
                description: "Install via Homebrew instead of the bundled PKG.",
                getDefaultValue: () => false);

            var command = new Command("install-blackhole", "Install BlackHole 2ch audio driver (macOS only)")
            {
                brewOption
            };

            command.SetHandler((bool useBrew) =>
            {
                if (!OperatingSystem.IsMacOS())
                {
                    Console.WriteLine("This command is only available on macOS.");
                    return;
                }

                try
                {
                    if (useBrew)
                    {
                        Console.WriteLine("Installing BlackHole via Homebrew...");
                        var process = Process.Start("brew", "install --cask blackhole-2ch");
                        process.WaitForExit();
                    }
                    else
                    {
                        var appDir = AppContext.BaseDirectory;
                        var pkgPath = Path.Combine(appDir, "installers", "BlackHole2ch-0.6.1.pkg");

                        if (!File.Exists(pkgPath))
                        {
                            Console.WriteLine($"Bundled installer not found at: {pkgPath}");
                            Console.WriteLine("Please install manually or use --brew.");
                            return;
                        }

                        Console.WriteLine($"Opening installer: {pkgPath}");
                        Process.Start("open", pkgPath);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error during installation: {ex.Message}");
                }
            }, brewOption);

            return command;
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
                    context.ExitCode = 1;
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
                            diarization = new DiarizationPipeline(modelInstance, diarizationSettings, Console.WriteLine, diarizationResources.SpeechModelPath);
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
                    context.ExitCode = 1;
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
                    var archive = new RecordingArchive(micTranscript);
                    var audioPath = archive.CreatePath("MIC");
                    {
                        DiarizationPipeline? diarization = null;
                        using var recorder = AudioCaptureFactory.CreateMicrophoneRecorder(deviceIndex: device, recordingPath: audioPath);
                        try
                        {
                            if (diarizationResources != null)
                            {
                                var modelInstance = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                                diarization = new DiarizationPipeline(modelInstance, diarizationOptions, Console.WriteLine, diarizationResources.SpeechModelPath);
                            }

                            using var processor = new WhisperStreamProcessor(
                                modelPath,
                                language,
                                recorder,
                                chunkSize,
                                transcriptFilePath: micTranscript,
                                diarization: diarization);

                            recorder.StartRecording();
                            processor.StartProcessing(archive.Started(audioPath, "MIC"));

                            // Wait for Ctrl+C
                            var tcs = new TaskCompletionSource<bool>();
                            ConsoleCancelEventHandler cancel = (s, e) =>
                            {
                                e.Cancel = true;
                                tcs.TrySetResult(true);
                            };

                            diarization = null; // Owned by the processor.
                            Console.CancelKeyPress += cancel;
                            try { await await Task.WhenAny(tcs.Task, processor.Completion); }
                            finally
                            {
                                Console.CancelKeyPress -= cancel;
                                recorder.StopRecording();
                                await processor.StopProcessing();
                            }
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
                    context.ExitCode = 1;
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
                AudioCaptureFactory.ListMicrophones();
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

            var echoCancellationOption = new Option<bool>(
                name: "--echo-cancellation",
                description: "Reduce speaker echo in the selected microphone using Apple voice processing (macOS 14.2+, requires --mic-device). May lower playback volume.");

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
                echoCancellationOption,
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
                var echoCancellation = context.ParseResult.GetValueForOption(echoCancellationOption);
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
                    if (echoCancellation && micDevice < 0)
                        throw new ArgumentException("--echo-cancellation requires --mic-device.");
                    if (echoCancellation && !OperatingSystem.IsMacOSVersionAtLeast(14, 2))
                        throw new PlatformNotSupportedException("Echo cancellation currently requires macOS 14.2 or later.");

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

                    // Create shared transcript file path (GUID-based to match existing convention)
                    var meetingId = Guid.TryParse(Environment.GetEnvironmentVariable("TX_MEETING_ID"), out var id) ? id : Guid.NewGuid();
                    var sharedTranscript = ResolveTranscriptPath($"{meetingId:N}.txt", outputDir);
                    var archive = new RecordingArchive(sharedTranscript);
                    var speakerAudio = archive.CreatePath("SPK");
                    var micAudio = archive.CreatePath("MIC");
                    using var speakerRecorder = AudioCaptureFactory.CreateSpeakerRecorder(deviceIndex: device, recordingPath: speakerAudio);
                    using var merger = new TranscriptionMerger(sharedTranscript, TimeSpan.FromSeconds(3));

                    // Speaker processor
                    DiarizationPipeline? speakerDiarization = null;
                    DiarizationPipeline? micDiarization = null;
                    try
                    {
                        if (diarizationResources != null)
                        {
                            var speakerModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                            speakerDiarization = new DiarizationPipeline(speakerModel, diarizationOptions, Console.WriteLine, diarizationResources.SpeechModelPath);
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
                        speakerDiarization = null; // The processor now owns this pipeline.

                        // Optional mic
                        using var micRecorder = micDevice >= 0
                            ? AudioCaptureFactory.CreateMicrophoneRecorder(deviceIndex: micDevice, echoCancellation: echoCancellation, recordingPath: micAudio)
                            : null;
                        WhisperStreamProcessor? micProcessor = null;
                        if (micRecorder != null)
                        {
                            if (diarizationResources != null)
                            {
                                var micModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, Console.WriteLine);
                                micDiarization = new DiarizationPipeline(micModel, diarizationOptions, Console.WriteLine, diarizationResources.SpeechModelPath);
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
                            micDiarization = null;
                        }

                        // Start voice processing before the system tap, because it may
                        // configure the output device used as the echo reference.
                        try
                        {
                            micRecorder?.StartRecording();
                            var micOffset = micRecorder != null ? archive.Started(micAudio, "MIC") : TimeSpan.Zero;
                            speakerRecorder.StartRecording();
                            speakerProcessor.StartProcessing(archive.Started(speakerAudio, "SPK"));
                            micProcessor?.StartProcessing(micOffset);
                            if (echoCancellation)
                                Console.WriteLine("Microphone echo cancellation enabled (Apple voice processing; minimum playback ducking).");

                            var tcs = new TaskCompletionSource<bool>();
                            ConsoleCancelEventHandler cancel = (s, e) =>
                            {
                                e.Cancel = true;
                                tcs.TrySetResult(true);
                            };
                            Console.CancelKeyPress += cancel;
                            try
                            {
                                var tasks = new List<Task> { tcs.Task, speakerProcessor.Completion };
                                if (micProcessor != null) tasks.Add(micProcessor.Completion);
                                await await Task.WhenAny(tasks);
                            }
                            finally { Console.CancelKeyPress -= cancel; }
                        }
                        finally
                        {
                            Console.WriteLine("Stopping transcription...");
                            speakerRecorder.StopRecording();
                            micRecorder?.StopRecording();
                            try
                            {
                                await Task.WhenAll(speakerProcessor.StopProcessing(), micProcessor?.StopProcessing() ?? Task.CompletedTask);
                            }
                            finally { micProcessor?.Dispose(); }
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
                    finally
                    {
                        micDiarization?.Dispose();
                        speakerDiarization?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    context.ExitCode = 1;
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
                AudioCaptureFactory.ListSpeakers();
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
