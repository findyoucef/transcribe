using System.CommandLine;
using System.CommandLine.Parsing;

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
                description: "Whisper model to use: LargeV3 (default), LargeV3Turbo, BaseEn, TinyEn",
                getDefaultValue: () => "LargeV3");

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

                    IProgress<int> progress = new Progress<int>(p =>
                    {
                        Console.Write($"\rProgress: {p}%");
                        if (p >= 100)
                        {
                            Console.WriteLine();
                        }
                    });

                    await WhisperClient.TranscribeAudioAsync(wavPath, progress, model, language);
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