using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pylon.App.Services;
using Pylon.Broker;
using Pylon.Core.Monitors;

namespace Pylon.App.Views;

/// <summary>Everything apart from the stream widget, in one window with a nav rail, the same shape as Postern's.</summary>
internal sealed class MainWindow : Window
{
    public const string MonitorsKey = "monitors";
    public const string GeneralKey = "general";

    const double NavWidth = 190;

    readonly MonitorsPage _monitors;
    readonly Panel _content = new();
    readonly Dictionary<string, Control> _pages = [];
    readonly List<(string Key, Button Item)> _nav = [];

    public MainWindow(PylonRuntime runtime, UiSettings settings, Action showStream, string page = MonitorsKey)
    {
        Title = $"Pylon{Profile.DisplaySuffix}";
        Width = 1100;
        Height = 720;
        MinWidth = 820;
        MinHeight = 500;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = TrayArt.WindowIcon();

        _monitors = new MonitorsPage(runtime);
        var nav = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        AddPage(nav, MonitorsKey, "Monitors", _monitors);
        AddPage(nav, GeneralKey, "General", General(runtime, settings, showStream));

        var rail = new Border
        {
            Width = NavWidth,
            Background = Ui.Sunken,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Top(Brand()),
                    new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_content, 1);
        grid.Children.Add(rail);
        grid.Children.Add(_content);
        Content = grid;

        void OnChanged() => Dispatcher.UIThread.Post(_monitors.Refresh);
        void OnPolled(PollRecord poll) => Dispatcher.UIThread.Post(() => _monitors.OnPolled(poll));
        runtime.Monitors.Changed += OnChanged;
        runtime.Monitors.Polled += OnPolled;
        Closed += (_, _) =>
        {
            runtime.Monitors.Changed -= OnChanged;
            runtime.Monitors.Polled -= OnPolled;
        };
        NavigateTo(page);
    }

    /// <summary>One monitor's detail, with a poll expanded when one is given (from an event's "Show the poll").</summary>
    public void ShowMonitor(string monitorId, long? pollId = null, MonitorDetail.Tab tab = MonitorDetail.Tab.Activity)
    {
        NavigateTo(MonitorsKey);
        _monitors.ShowDetail(monitorId, pollId, tab);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    public void NavigateTo(string key)
    {
        if (!_pages.ContainsKey(key))
            return;
        foreach (var (pageKey, page) in _pages)
            page.IsVisible = pageKey == key;
        foreach (var (itemKey, item) in _nav)
        {
            var selected = itemKey == key;
            item.Background = selected ? Ui.Panel : Ui.Sunken;
            item.Foreground = selected ? Ui.Foreground : Ui.Muted;
            item.BorderBrush = selected ? Ui.Accent : Brushes.Transparent;
        }
        if (key == MonitorsKey)
            _monitors.ShowList();
    }

    void AddPage(StackPanel nav, string key, string title, Control page)
    {
        page.IsVisible = false;
        _pages[key] = page;
        _content.Children.Add(page);
        var item = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Ui.Sunken,
            Foreground = Ui.Muted,
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(15, 0, 12, 0),
            Height = 42,
            FontSize = 14,
        };
        item.Click += (_, _) => NavigateTo(key);
        nav.Children.Add(item);
        _nav.Add((key, item));
    }

    static Control Brand() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Margin = new Thickness(16, 18, 12, 14),
        Children =
        {
            Icons.Glyph(Icons.Pylon, TrayArt.Colour, 28),
            new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Pylon", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground },
                    Ui.Text($"{PylonRuntime.Version}{Profile.DisplaySuffix}", Ui.Muted, 11),
                },
            },
        },
    };

    static Control General(PylonRuntime runtime, UiSettings settings, Action showStream)
    {
        var options = runtime.Options;
        var showOnStart = new CheckBox { Content = "Show the stream when Pylon starts", IsChecked = settings.StreamVisible };
        showOnStart.IsCheckedChanged += (_, _) =>
        {
            settings.StreamVisible = showOnStart.IsChecked == true;
            settings.Save();
        };
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    Ui.Heading("General"),
                    Ui.Card(new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            Ui.Field("Subscribers connect to", Ui.Code($@"\\.\pipe\{options.PipeName}  (GET /_pylon/events, server-sent events)")),
                            Ui.Field("Calls out through Postern on", Ui.Code($@"\\.\pipe\{options.PosternPipeName}  as app '{options.PosternAppName}'")),
                            Ui.Field("Data", Ui.Code(options.DataDirectory)),
                            Ui.Field("Dev ingest (test bench, pylon-cli emit)", Ui.Text(options.DevIngest
                                ? "On: anything running as you can add test events to the stream. Debug builds always have it on."
                                : "Off.", options.DevIngest ? Ui.Warning : Ui.Muted)),
                            Ui.Field("Keeps", Ui.Text($"Events for {options.Retention.EventAge.TotalDays:0} days (at most {options.Retention.MaxEvents:N0}); payloads for {options.Retention.PayloadAge.TotalDays:0} days (at most {options.Retention.MaxPayloadBytes / 1024 / 1024:N0} MB).", Ui.Muted)),
                        },
                    }),
                    Ui.Card(new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            showOnStart,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children =
                                {
                                    Ui.Button("Show the stream", showStream, primary: true),
                                    Ui.Button("Open data folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{options.DataDirectory}\"") { UseShellExecute = false })),
                                },
                            },
                        },
                    }),
                },
            },
        };
    }
}
