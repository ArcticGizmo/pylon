using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Pylon.App.Services;
using Pylon.App.Views;
using Pylon.Broker;
using Pylon.Broker.Pipe;
using Pylon.Core.Events;

namespace Pylon.App;

public partial class App : Application
{
    ILogger _log = null!;
    PylonRuntime? _runtime;
    UiSettings _settings = null!;
    IClassicDesktopStyleApplicationLifetime? _desktop;
    TrayIcon? _tray;
    NativeMenuItem? _streamItem;
    MainWindow? _main;
    StreamWindow? _stream;
    int _unseen;
    bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: no main window, so don't exit when the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;
            _log = Program.Logs.CreateLogger("Pylon.App");
            _settings = UiSettings.Load(Program.Args.DataDirectory);
            _runtime = Program.CreateRuntime();
            _runtime.Hub.Published += OnPublished;

            SetupTray(desktop);
            desktop.ShutdownRequested += (_, _) =>
            {
                _shuttingDown = true;
                StopRuntime();
            };
            _ = StartRuntime(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    async Task StartRuntime(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await _runtime!.StartAsync();
            UpdateTray();
            if (_settings.StreamVisible)
                ShowStream();
            if (_runtime.Monitors.Definitions.Count == 0 && _runtime.Monitors.LoadError is null)
                Open(MainWindow.MonitorsKey); // first run: nothing happens until there's a monitor
        }
        catch (PipeInUseException ex)
        {
            _log.LogError("{Message}", ex.Message);
            await ShowFatal("Pylon is already running", ex.Message);
            desktop.Shutdown(Program.ExitPipeInUse);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Pylon failed to start");
            await ShowFatal("Pylon couldn't start", ex.Message);
            desktop.Shutdown(Program.ExitBrokerFailed);
        }
    }

    void StopRuntime()
    {
        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pylon didn't stop cleanly");
        }
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var header = new NativeMenuItem($"Pylon{Profile.DisplaySuffix} {PylonRuntime.Version}") { IsEnabled = false };

        _streamItem = new NativeMenuItem("Show stream");
        _streamItem.Click += (_, _) => ToggleStream();

        var open = new NativeMenuItem("Monitors…");
        open.Click += (_, _) => Open(MainWindow.MonitorsKey);

        var quit = new NativeMenuItem("Quit Pylon");
        quit.Click += (_, _) => desktop.Shutdown(0);

        var menu = new NativeMenu
        {
            header,
            new NativeMenuItemSeparator(),
            _streamItem,
            open,
            new NativeMenuItemSeparator(),
            quit,
        };

        _tray = new TrayIcon { Icon = TrayArt.WindowIcon(), Menu = menu, IsVisible = true, ToolTipText = "Pylon: starting" };
        _tray.Clicked += (_, _) => ToggleStream();
        TrayIcon.SetIcons(this, [_tray]);
    }

    void OnPublished(IReadOnlyList<EventRecord> records)
    {
        if (records.All(r => r.Kind == EventKind.Resync))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_stream is not null)
                return;
            _unseen += records.Count;
            UpdateTray();
        });
    }

    void UpdateTray()
    {
        if (_tray is null || _runtime is null)
            return;
        var monitors = _runtime.Monitors.Definitions.Count(d => d.Enabled);
        _tray.Icon = TrayArt.WindowIcon(unread: _unseen > 0);
        _tray.ToolTipText = $"Pylon{Profile.DisplaySuffix}: {monitors} monitor(s)" + (_unseen > 0 ? $"\n{_unseen} new event(s): click to see them" : "");
        if (_streamItem is not null)
            _streamItem.Header = _stream is null ? "Show stream" : "Hide stream";
    }

    void ToggleStream()
    {
        if (_stream is not null)
            _stream.Close();
        else
            ShowStream();
    }

    void ShowStream()
    {
        if (_runtime is null)
            return;
        if (_stream is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new StreamWindow(_runtime, _settings, ShowPoll);
        _stream = window;
        _settings.StreamVisible = true;
        _settings.Save();
        window.Closed += (_, _) =>
        {
            _stream = null;
            // Hiding the widget by hand means "keep it hidden next time"; quitting Pylon with it open doesn't.
            if (!_shuttingDown)
            {
                _settings.StreamVisible = false;
                _settings.Save();
            }
            UpdateTray();
        };
        window.Show();
        _unseen = 0;
        UpdateTray();
    }

    void ShowPoll(string monitorId, long pollId)
    {
        Open(MainWindow.MonitorsKey);
        _main?.ShowMonitor(monitorId, pollId);
    }

    void Open(string page)
    {
        if (_main is { } existing)
        {
            existing.NavigateTo(page);
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new MainWindow(_runtime!, _settings, ShowStream, page);
        _main = window;
        window.Closed += (_, _) => _main = null;
        window.Show();
        window.Activate();
    }

    static Task ShowFatal(string title, string message)
    {
        var closed = new TaskCompletionSource();
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            Background = Ui.Background,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children = { Ui.Heading(title), Ui.Text(message, Ui.Muted) },
            },
        };
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }
}
