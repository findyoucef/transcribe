using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Transcribe.Gui.Services;
using Transcribe.Gui.ViewModels;

namespace Transcribe.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var services = new WindowServices(this, GetSelectionOrAllText);
        DataContext = new MainViewModel(services);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Auto-scroll transcript
        TranscriptViewer.TextChanged += (s, e) =>
        {
            if (TranscriptViewer.Text is { Length: > 0 } text)
            {
                TranscriptViewer.CaretIndex = text.Length;
            }
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // If a TextBox has a selection, let the default copy behavior happen.
            var topLevel = TopLevel.GetTopLevel(this);
            var focused = topLevel?.FocusManager?.GetFocusedElement();
            if (focused is TextBox tb && !string.IsNullOrEmpty(tb.SelectedText))
            {
                return;
            }

            if (vm.CopyOutputCommand.CanExecute(null))
            {
                vm.CopyOutputCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private string? GetSelectionOrAllText(string context)
    {
        return context switch
        {
            "Transcript" => GetTextBoxSelectionOrAll(TranscriptViewer),
            "Summary" => GetTextBoxSelectionOrAll(SummaryViewer),
            _ => GetLogText(),
        };
    }

    private static string? GetTextBoxSelectionOrAll(TextBox? textBox)
    {
        if (textBox == null)
        {
            return null;
        }

        var selection = textBox.SelectedText;
        if (!string.IsNullOrEmpty(selection))
        {
            return selection;
        }

        return textBox.Text;
    }

    private string GetLogText()
    {
        if (DataContext is not MainViewModel vm)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, vm.LogEntries.Select(e => $"[{e.Timestamp:HH:mm:ss}] {e.Level}: {e.Message}"));
    }
}
