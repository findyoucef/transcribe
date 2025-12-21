namespace Transcribe.Gui.Services;

public interface IWindowServices
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickWavFileAsync(string title);
    Task<string?> SaveTextFileAsync(string suggestedFileName);
    Task CopyToClipboardAsync(string text);
    string? GetSelectionOrAllText(string context);
    void OpenFolderInShell(string path);
}

