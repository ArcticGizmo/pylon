using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pylon.App.Services;
using Pylon.Broker;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.App.Views;

/// <summary>
/// The floating stream widget: a small topmost window that shows events as they arrive. It never takes focus when it
/// appears (so a burst of events can't steal the keyboard from what you're typing in), stays out of the taskbar and
/// Alt+Tab, and can be dragged anywhere, resized from any edge, switched between compact and expanded, or made full
/// screen. Clicking a record opens what Pylon observed for it.
/// <para>
/// It reads the hub in-process rather than subscribing over the pipe: it's part of Pylon, and it needs the
/// presentation fields (summary, status, payload) that subscribers never get.
/// </para>
/// </summary>
internal sealed class StreamWindow : Window
{
    public const int MaxItems = 2000;
    const int HistoryPage = 300;
    const double SnapDistance = 24;
    const double Grip = 6;

    readonly PylonRuntime _runtime;
    readonly UiSettings _settings;
    readonly ObservableCollection<EventRecord> _visible = [];
    readonly List<EventRecord> _all = [];
    readonly List<EventRecord> _held = [];
    readonly ListBox _list;
    readonly TextBox _search;
    readonly ComboBox _monitorPicker;
    readonly ComboBox _statusPicker;
    readonly Control _filterBar;
    readonly TextBlock _title;
    readonly TextBlock _footer;
    readonly Button _pause;
    readonly Button _mode;
    readonly Grid _grips;
    readonly DispatcherTimer _settle;
    Dictionary<string, MonitorDefinition> _monitors = [];
    StreamFilter _filter;
    bool _paused;
    bool _filterOpen;
    long? _oldestLoaded;
    bool _historyExhausted;
    bool _moving;

    public StreamWindow(PylonRuntime runtime, UiSettings settings)
    {
        _runtime = runtime;
        _settings = settings;
        _filter = settings.Filter;

        Title = "Pylon stream";
        Icon = TrayArt.WindowIcon();
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = true;
        Background = Brushes.Transparent;
        MinWidth = 300;
        MinHeight = 140;
        ApplySize(settings.StreamMode);

        _title = new TextBlock { Text = "Pylon", FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground, VerticalAlignment = VerticalAlignment.Center };
        _pause = Ui.IconButton(Icons.Pause, "Pause the stream", TogglePause);
        _mode = Ui.IconButton(settings.StreamMode == StreamMode.Compact ? Icons.Expand : Icons.Collapse, "Expand", ToggleMode);
        var header = new Border
        {
            Background = Ui.Sunken,
            Padding = new Thickness(10, 4, 4, 4),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Right(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        Children =
                        {
                            Ui.IconButton(Icons.Filter, "Filter", ToggleFilter),
                            _pause,
                            Ui.IconButton(Icons.Clear, "Clear the list (the log keeps everything)", ClearView),
                            _mode,
                            Ui.IconButton(Icons.FullScreen, "Full screen", ToggleFullScreen),
                            Ui.IconButton(Icons.Close, "Hide (the tray menu brings it back)", Close),
                        },
                    }),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { Icons.Glyph(Icons.Pylon, TrayArt.Colour, 16), _title },
                    },
                },
            },
        };
        header.PointerPressed += (_, e) =>
        {
            if (WindowState != WindowState.FullScreen && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        header.DoubleTapped += (_, _) => ToggleMode();

        _search = new TextBox { PlaceholderText = "Filter: words in the summary, repo or type", Text = _filter.Text, MinWidth = 120 };
        _search.TextChanged += (_, _) => SetFilter(_filter with { Text = _search.Text ?? "" });
        _monitorPicker = new ComboBox { MinWidth = 120 };
        _monitorPicker.SelectionChanged += (_, _) => OnMonitorPicked();
        _statusPicker = new ComboBox { MinWidth = 110 };
        _statusPicker.ItemsSource = new[] { "Any status" }.Concat(Enum.GetValues<EventStatus>().Select(Ui.StatusLabel)).ToList();
        _statusPicker.SelectedIndex = _filter.Statuses.Count == 1 ? 1 + (int)_filter.Statuses.First() : 0;
        _statusPicker.SelectionChanged += (_, _) => SetFilter(_filter with
        {
            Statuses = _statusPicker.SelectedIndex <= 0 ? new HashSet<EventStatus>() : new HashSet<EventStatus> { (EventStatus)(_statusPicker.SelectedIndex - 1) },
        });
        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _monitorPicker, _statusPicker } };
        _filterBar = new Border
        {
            Background = Ui.Panel,
            Padding = new Thickness(8, 6),
            Child = new StackPanel { Spacing = 6, Children = { _search, pickers } },
        };

        _list = new ListBox
        {
            ItemsSource = _visible,
            Background = Brushes.Transparent,
            // Avalonia builds the template with null content while it recycles a row container.
            ItemTemplate = new FuncDataTemplate<EventRecord>((record, _) => record is null ? new Panel() : Row(record), supportsRecycling: false),
        };
        // Rows trim to the widget's width rather than scrolling sideways.
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.Tapped += (_, e) =>
        {
            if (e.Source is Control control && control.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is EventRecord record)
                OpenPayload(record);
        };

        _footer = new TextBlock { FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        var older = new Button { Content = "Load older", FontSize = 11, Padding = new Thickness(8, 2), Background = Brushes.Transparent };
        older.Click += (_, _) => LoadOlder();
        var footer = new Border
        {
            Background = Ui.Sunken,
            Padding = new Thickness(10, 3, 4, 3),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Child = new DockPanel { Children = { Ui.Right(older), _footer } },
        };

        var frame = new Border
        {
            Background = Ui.Background,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new DockPanel { Children = { Ui.Top(header), Ui.Top(_filterBar), Ui.Bottom(footer), _list } },
        };

        _grips = ResizeGrips();
        Content = new Grid { Children = { frame, _grips } };

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Settle();
        };
        PositionChanged += (_, _) =>
        {
            // Only the user's moves: reacting to our own Place/Settle would snap forever.
            if (_moving)
                return;
            _settle.Stop();
            _settle.Start();
        };
        Opened += (_, _) =>
        {
            Place();
            HideFromAltTab();
        };

        RefreshMonitors();
        _runtime.Hub.Published += OnPublished;
        _runtime.Monitors.Changed += OnMonitorsChanged;
        Closed += (_, _) =>
        {
            _runtime.Hub.Published -= OnPublished;
            _runtime.Monitors.Changed -= OnMonitorsChanged;
            SaveBounds();
        };

        UpdateFilterBar();
        LoadOlder();
    }

    /// <summary>For snapshots and tests: adds records as if they had just been published.</summary>
    internal void Add(IReadOnlyList<EventRecord> records)
    {
        if (_paused)
        {
            _held.AddRange(records);
            UpdateTitle();
            return;
        }
        foreach (var record in records)
        {
            _all.Insert(0, record);
            if (_filter.Matches(record))
                _visible.Insert(0, record);
        }
        Trim();
        UpdateTitle();
    }

    internal int VisibleCount => _visible.Count;

    internal void ShowFilter(bool open)
    {
        _filterOpen = open;
        UpdateFilterBar();
    }

    void OnPublished(IReadOnlyList<EventRecord> records) => Dispatcher.UIThread.Post(() => Add(records));

    void OnMonitorsChanged() => Dispatcher.UIThread.Post(RefreshMonitors);

    void RefreshMonitors()
    {
        var definitions = _runtime.Monitors.Definitions;
        if (definitions.Count == _monitors.Count && definitions.All(d => _monitors.TryGetValue(d.Id, out var known) && known == d))
            return;
        _monitors = definitions.ToDictionary(d => d.Id);
        var names = new List<string> { "All monitors" };
        names.AddRange(definitions.Select(d => d.Name));
        names.Add("No monitor (test events)");
        _monitorPicker.ItemsSource = names;
        var selected = _filter.Monitors.Count == 1 ? _filter.Monitors.First() : null;
        _monitorPicker.SelectedIndex = selected is null ? 0
            : selected == StreamFilter.NoMonitor ? names.Count - 1
            : Math.Max(0, definitions.ToList().FindIndex(d => d.Id == selected) + 1);
    }

    void OnMonitorPicked()
    {
        var index = _monitorPicker.SelectedIndex;
        var definitions = _runtime.Monitors.Definitions;
        HashSet<string> monitors = index <= 0 ? []
            : index > definitions.Count ? [StreamFilter.NoMonitor]
            : [definitions[index - 1].Id];
        if (!monitors.SetEquals(_filter.Monitors))
            SetFilter(_filter with { Monitors = monitors });
    }

    void SetFilter(StreamFilter filter)
    {
        _filter = filter;
        _settings.Filter = filter;
        _settings.Save();
        _visible.Clear();
        foreach (var record in _all.Where(filter.Matches))
            _visible.Add(record);
        UpdateTitle();
    }

    void LoadOlder()
    {
        if (_historyExhausted)
            return;
        var page = _runtime.Store.ReadBefore(_oldestLoaded, HistoryPage);
        if (page.Count < HistoryPage)
            _historyExhausted = true;
        foreach (var record in page)
        {
            _all.Add(record);
            if (_filter.Matches(record))
                _visible.Add(record);
        }
        if (page.Count > 0)
            _oldestLoaded = page[^1].Sequence;
        UpdateTitle();
    }

    void Trim()
    {
        while (_all.Count > MaxItems)
            _all.RemoveAt(_all.Count - 1);
        while (_visible.Count > MaxItems)
            _visible.RemoveAt(_visible.Count - 1);
        _oldestLoaded = _all.Count > 0 ? _all[^1].Sequence : null;
        _historyExhausted = false;
    }

    void TogglePause()
    {
        _paused = !_paused;
        _pause.Content = Icons.Glyph(_paused ? Icons.Play : Icons.Pause, _paused ? Ui.Warning : Ui.Muted, 16);
        ToolTip.SetTip(_pause, _paused ? "Resume" : "Pause the stream");
        if (!_paused && _held.Count > 0)
        {
            var held = _held.ToList();
            _held.Clear();
            Add(held);
        }
        UpdateTitle();
    }

    void ClearView()
    {
        _visible.Clear();
        _all.Clear();
        _held.Clear();
        _historyExhausted = true;
        UpdateTitle();
    }

    void ToggleFilter()
    {
        _filterOpen = !_filterOpen;
        UpdateFilterBar();
    }

    void UpdateFilterBar() =>
        _filterBar.IsVisible = _filterOpen || _settings.StreamMode == StreamMode.Expanded || WindowState == WindowState.FullScreen;

    void ToggleMode()
    {
        if (WindowState == WindowState.FullScreen)
            return;
        SaveBounds();
        _settings.StreamMode = _settings.StreamMode == StreamMode.Compact ? StreamMode.Expanded : StreamMode.Compact;
        _settings.Save();
        ApplySize(_settings.StreamMode);
        _mode.Content = Icons.Glyph(_settings.StreamMode == StreamMode.Compact ? Icons.Expand : Icons.Collapse, Ui.Muted, 16);
        ToolTip.SetTip(_mode, _settings.StreamMode == StreamMode.Compact ? "Expand" : "Compact");
        UpdateFilterBar();
        Dispatcher.UIThread.Post(Settle, DispatcherPriority.Background);
    }

    void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            Topmost = true;
        }
        else
        {
            SaveBounds();
            WindowState = WindowState.FullScreen;
        }
        _grips.IsVisible = WindowState != WindowState.FullScreen;
        UpdateFilterBar();
    }

    void ApplySize(StreamMode mode)
    {
        Width = mode == StreamMode.Compact ? _settings.CompactWidth : _settings.ExpandedWidth;
        Height = mode == StreamMode.Compact ? _settings.CompactHeight : _settings.ExpandedHeight;
    }

    void SaveBounds()
    {
        if (WindowState != WindowState.Normal)
            return;
        if (_settings.StreamMode == StreamMode.Compact)
        {
            _settings.CompactWidth = Width;
            _settings.CompactHeight = Height;
        }
        else
        {
            _settings.ExpandedWidth = Width;
            _settings.ExpandedHeight = Height;
        }
        var scale = DesktopScaling;
        _settings.StreamX = Position.X / scale;
        _settings.StreamY = Position.Y / scale;
        _settings.Save();
    }

    /// <summary>First show: where it was last time if that's still on a screen, otherwise the bottom-right corner.</summary>
    void Place()
    {
        var scale = DesktopScaling;
        if (_settings.StreamX is { } x && _settings.StreamY is { } y)
        {
            var saved = new PixelPoint((int)(x * scale), (int)(y * scale));
            if (Screens.ScreenFromPoint(saved) is not null)
            {
                MoveTo(saved);
                return;
            }
        }
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            MoveTo(new PixelPoint(
                area.Right - (int)(Width * screen.Scaling) - (int)(16 * screen.Scaling),
                area.Bottom - (int)(Height * screen.Scaling) - (int)(16 * screen.Scaling)));
        }
    }

    /// <summary>After a move or resize stops: snap to a working-area edge when close to one, and remember where it is.</summary>
    void Settle()
    {
        if (WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen)
            return;
        var area = screen.WorkingArea;
        var snap = (int)(SnapDistance * screen.Scaling);
        var width = (int)(Bounds.Width * screen.Scaling);
        var height = (int)(Bounds.Height * screen.Scaling);
        var x = Position.X;
        var y = Position.Y;
        if (Math.Abs(x - area.X) < snap) x = area.X;
        if (Math.Abs(area.Right - (x + width)) < snap) x = area.Right - width;
        if (Math.Abs(y - area.Y) < snap) y = area.Y;
        if (Math.Abs(area.Bottom - (y + height)) < snap) y = area.Bottom - height;
        if (x != Position.X || y != Position.Y)
            MoveTo(new PixelPoint(x, y));
        SaveBounds();
    }

    void MoveTo(PixelPoint position)
    {
        _moving = true;
        try
        {
            Position = position;
        }
        finally
        {
            _moving = false;
        }
    }

    void UpdateTitle()
    {
        var latest = _all.Count > 0 ? $"latest #{_all[0].Sequence}" : "nothing yet";
        _title.Text = _paused ? $"Pylon · paused · {_held.Count} waiting" : "Pylon";
        _footer.Text = _filter.IsEmpty
            ? $"{_visible.Count} event(s) · {latest}"
            : $"{_visible.Count} of {_all.Count} match the filter · {latest}";
    }

    Control Row(EventRecord record) =>
        Row(record, record.MonitorId is { } id ? _monitors.GetValueOrDefault(id) : null, _settings.StreamMode == StreamMode.Compact);

    /// <summary>One record: monitor icon, summary, a muted line of when/who/what, and the status dot.</summary>
    internal static Control Row(EventRecord record, MonitorDefinition? monitor, bool compact)
    {
        var (icon, accent) = Look(record, monitor);
        var who = monitor?.Name ?? (record.Via == EventVia.Test ? "test" : record.Source);
        var meta = $"{record.ObservedAtUtc.ToLocalTime():HH:mm:ss} · {who} · {record.Type}";

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Ui.StatusBrush(record.Status), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(dot, Ui.StatusLabel(record.Status));
        var badge = Icons.Badge(icon, accent, 30);
        badge.VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = record.Summary,
                    Foreground = Ui.Foreground,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = compact ? 1 : 2,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = meta, Foreground = Ui.Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(dot, 2);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        grid.Children.Add(dot);
        ToolTip.SetTip(grid, $"#{record.Sequence} {record.Topic}{(record.HasPayload ? "" : " (no payload kept)")}");
        return grid;
    }

    /// <summary>The icon and colour for a record: its monitor's, else one that says where it came from.</summary>
    internal static (string Icon, IBrush Accent) Look(EventRecord record, MonitorDefinition? monitor)
    {
        if (record.Kind == EventKind.Resync)
            return (Icons.Pylon, Ui.Neutral);
        if (monitor is not null)
            return (monitor.Icon.Length > 0 ? monitor.Icon : DefaultIcon(monitor.Source), Ui.AccentFor(monitor.Accent));
        if (record.Via == EventVia.Test)
            return (Icons.Test, Ui.Warning);
        return (DefaultIcon(record.Source), Ui.Accent);
    }

    internal static string DefaultIcon(string source) => source switch
    {
        "github" => Icons.PullRequest,
        "heartbeat" => Icons.Heartbeat,
        "pylon" => Icons.Pylon,
        _ => Icons.Bell,
    };

    void OpenPayload(EventRecord record)
    {
        var monitor = record.MonitorId is { } id ? _monitors.GetValueOrDefault(id) : null;
        var window = new PayloadWindow(record, record.HasPayload ? _runtime.Store.GetPayload(record.Sequence) : null, monitor);
        window.Show();
        window.Activate();
    }

    Grid ResizeGrips()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{Grip},*,{Grip}"),
            RowDefinitions = new RowDefinitions($"{Grip},*,{Grip}"),
        };
        void Add(int row, int column, WindowEdge edge, StandardCursorType cursor)
        {
            var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(cursor) };
            Grid.SetRow(grip, row);
            Grid.SetColumn(grip, column);
            grip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginResizeDrag(edge, e);
            };
            grid.Children.Add(grip);
        }
        Add(0, 0, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner);
        Add(0, 1, WindowEdge.North, StandardCursorType.TopSide);
        Add(0, 2, WindowEdge.NorthEast, StandardCursorType.TopRightCorner);
        Add(1, 0, WindowEdge.West, StandardCursorType.LeftSide);
        Add(1, 2, WindowEdge.East, StandardCursorType.RightSide);
        Add(2, 0, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        Add(2, 1, WindowEdge.South, StandardCursorType.BottomSide);
        Add(2, 2, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);
        return grid;
    }

    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080;
    const long WsExAppWindow = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>A tool window doesn't show in Alt+Tab. Unlike WS_EX_NOACTIVATE it can still take the keyboard, so the filter box works.</summary>
    void HideFromAltTab()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
            return;
        var style = (long)GetWindowLongPtr(handle.Handle, GwlExStyle);
        SetWindowLongPtr(handle.Handle, GwlExStyle, (IntPtr)((style | WsExToolWindow) & ~WsExAppWindow));
    }
}
