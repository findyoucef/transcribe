using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Transcribe.Gui.Services;

public sealed class WindowServices : IWindowServices
{
    private readonly Window window;
    private readonly Func<string, string?> getSelectionOrAllText;

    public WindowServices(Window window, Func<string, string?> getSelectionOrAllText)
    {
        this.window = window;
        this.getSelectionOrAllText = getSelectionOrAllText;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = title
        });

        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<string?> PickWavFileAsync(string title)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = title,
            FileTypeFilter =
            [
                new FilePickerFileType("WAV audio")
                {
                    Patterns = ["*.wav", "*.WAV"]
                }
            ]
        });

        return files.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<string?> SaveTextFileAsync(string suggestedFileName)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text file")
                {
                    Patterns = ["*.txt"]
                }
            ]
        });

        return file?.Path.LocalPath;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel?.Clipboard == null)
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(text);
    }

    public string? GetSelectionOrAllText(string context) => getSelectionOrAllText(context);

    public void OpenFolderInShell(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore.
        }
    }
}

