using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

namespace Transcribe
{

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var wavArgument = new Argument<string>(
                name: "wav",
                description: "Path to the WAV file to transcribe")
            {
                Arity = ArgumentArity.ExactlyOne
            };

            var modelOption = new Option<string>(
                name: "--model",
                description: "Whisper model to use: LargeV3Turbo (default), LargeV3, BaseEn, TinyEn",
                getDefaultValue: () => "LargeV3Turbo");

            var languageOption = new Option<string>(
                name: "--language",
                description: "Language code for transcription (e.g., en, fr, es). Default: en",
                getDefaultValue: () => "en");

            var rootCommand = new RootCommand("Transcribe audio WAV files using Whisper")
            {
                wavArgument,
                modelOption,
                languageOption
            };

            rootCommand.SetHandler(async (string wavPath, string model, string language) =>
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

                    await WhisperClient.TranscribeAudioAsync(wavPath, progress, model, language);

                    stopwatch.Stop();
                    Console.WriteLine($"Transcription completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
            }, wavArgument, modelOption, languageOption);

            return await rootCommand.InvokeAsync(args);
        }
    }
}