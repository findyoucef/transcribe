using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Transcribe.Gui;

public sealed class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            InitializeTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var iconUri = new Uri("avares://transcribe.gui/Assets/icon.ico");
            using var stream = AssetLoader.Open(iconUri);
            var icon = new WindowIcon(stream);

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("Show");
            showItem.Click += Show_Click;
            menu.Add(showItem);
            
            // Separator support varies, skipping for safety or adding standard item
            // var sep = new NativeMenuItemSeparator(); 
            // menu.Add(sep);

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += Exit_Click;
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Transcribe AI",
                Menu = menu
            };

            _trayIcon.Clicked += TrayIcon_Clicked;

            var trayIcons = new TrayIcons
            {
                _trayIcon
            };

            TrayIcon.SetIcons(this, trayIcons);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize tray icon: {ex}");
        }
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ToggleWindow();
    }

    private void Show_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow != null)
            {
                desktop.MainWindow.Show();
                desktop.MainWindow.WindowState = WindowState.Normal;
                desktop.MainWindow.Activate();
            }
        }
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ToggleWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow != null)
            {
                if (desktop.MainWindow.IsVisible)
                {
                    desktop.MainWindow.Hide();
                }
                else
                {
                    desktop.MainWindow.Show();
                    desktop.MainWindow.WindowState = WindowState.Normal;
                    desktop.MainWindow.Activate();
                }
            }
        }
    }
}

