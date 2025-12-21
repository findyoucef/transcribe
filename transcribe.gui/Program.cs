using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Transcribe.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine("Unhandled exception:");
                Console.Error.WriteLine(e.ExceptionObject);
            }
            catch
            {
                // Ignore.
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine("Unobserved task exception:");
                Console.Error.WriteLine(e.Exception);
            }
            catch
            {
                // Ignore.
            }
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
