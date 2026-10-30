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

    const double NavWidth = 200;

    readonly MonitorsPage _monitors;
    readonly Panel _content = new();
    readonly Dictionary<string, Control> _pages = [];
    readonly List<(string Key, Border Item, TextBlock Label)> _nav = [];

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
        var nav = new StackPanel { Margin = new Thickness(8, 4), Spacing = 2 };
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
        foreach (var (itemKey, item, label) in _nav)
        {
            var selected = itemKey == key;
            item.Background = selected ? Ui.AccentTint : Brushes.Transparent;
            label.Foreground = selected ? Ui.Title : Ui.Foreground;
        }
        if (key == MonitorsKey)
            _monitors.ShowList();
    }

    void AddPage(StackPanel nav, string key, string title, Control page)
    {
        page.IsVisible = false;
        _pages[key] = page;
        _content.Children.Add(page);
        var label = new TextBlock { Text = title, FontSize = 14, Foreground = Ui.Foreground, VerticalAlignment = VerticalAlignment.Center };
        var item = Ui.Selectable(label, () => NavigateTo(key));
        nav.Children.Add(item);
        _nav.Add((key, item, label));
    }

    static Control Brand()
    {
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Icons.Glyph(Icons.Pylon, TrayArt.Colour, 24),
                new TextBlock { Text = "Pylon", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Ui.Title, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        // Dev instances poll through Postern (Dev) and keep their own data; flag it so it's obvious at a glance.
        if (Profile.IsDev)
            name.Children.Add(new TextBlock { Text = "- DEV", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Ui.Dev, VerticalAlignment = VerticalAlignment.Center });
        var tagline = Ui.Text($"changes, not polling · {PylonRuntime.Version}", Ui.Muted, 11);
        tagline.Margin = new Thickness(0, 6, 0, 0);
        return new StackPanel { Margin = new Thickness(18, 20, 18, 18), Children = { name, tagline } };
    }

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
                Margin = Ui.PageMargin,
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
