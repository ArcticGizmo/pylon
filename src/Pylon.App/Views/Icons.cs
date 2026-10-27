using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pylon.App.Views;

/// <summary>
/// The icon set, as path data on a 24×24 grid, drawn in code so they stay sharp at any size and render the same in
/// the headless snapshots. A monitor stores a key from here, never a file.
/// </summary>
internal static class Icons
{
    public const string PullRequest = "pull-request";
    public const string Heartbeat = "heartbeat";
    public const string Test = "test";
    public const string Pylon = "pylon";
    public const string Bell = "bell";
    public const string Alert = "alert";
    public const string Rocket = "rocket";

    // UI glyphs, not offered for monitors.
    public const string Close = "ui-close";
    public const string Expand = "ui-expand";
    public const string Collapse = "ui-collapse";
    public const string FullScreen = "ui-fullscreen";
    public const string Filter = "ui-filter";
    public const string Pause = "ui-pause";
    public const string Play = "ui-play";
    public const string Clear = "ui-clear";

    static readonly Dictionary<string, string> Paths = new()
    {
        [PullRequest] = "M6 2.5a3 3 0 1 1 0 6a3 3 0 1 1 0-6zM6 15.5a3 3 0 1 1 0 6a3 3 0 1 1 0-6zM18 15.5a3 3 0 1 1 0 6a3 3 0 1 1 0-6zM5 8h2v8H5zM17 9h2v7h-2zM12.5 4.5H16a3 3 0 0 1 3 3V9h-2V7.5a1 1 0 0 0-1-1h-3.5zM12.5 2v7l-4-3.5z",
        [Heartbeat] = "M2 11h4.4l2.1-5.2 4.1 10.6 2.9-6.7 1.6 1.3H22v2h-5.6l-1-.8-3 7-4-10.4-1.4 3.2H2z",
        [Test] = "M8.5 2h7v2h-1v4.6l5.2 9.3A2.7 2.7 0 0 1 17.3 22H6.7a2.7 2.7 0 0 1-2.4-4.1l5.2-9.3V4h-1zM11.5 4v5.1l-2.3 4.1h5.6l-2.3-4.1V4z",
        [Pylon] = "M11 2h2l6 20h-2.2l-1.2-4H8.4l-1.2 4H5zM12 6.2 10.6 11h2.8zM10 13l-1 3h6l-1-3zM4 7h16v2H4z",
        [Bell] = "M12 2a6 6 0 0 1 6 6v5l2 3v1H4v-1l2-3V8a6 6 0 0 1 6-6zM9.5 19h5a2.5 2.5 0 0 1-5 0z",
        [Alert] = "M12 2 23 21H1zM11 9v6h2V9zM11 17v2h2v-2z",
        [Rocket] = "M12 2c3.5 2 5.5 5.6 5.5 10l-2 3h-7l-2-3C6.5 7.6 8.5 4 12 2zM12 7a2 2 0 1 0 0 4a2 2 0 1 0 0-4zM8 16h8l-1.5 3h-5zM10.5 20h3L12 23z",
        [Close] = "M5.6 4.2 12 10.6l6.4-6.4 1.4 1.4-6.4 6.4 6.4 6.4-1.4 1.4-6.4-6.4-6.4 6.4-1.4-1.4 6.4-6.4-6.4-6.4z",
        [Expand] = "M4 4h7v2H6v5H4zM20 20h-7v-2h5v-5h2z",
        [Collapse] = "M11 11H4V9h5V4h2zM13 13h7v2h-5v5h-2z",
        [FullScreen] = "M3 3h18v18H3zM5 5v14h14V5z",
        [Filter] = "M3 4h18l-7 8.5V19l-4 2v-8.5z",
        [Pause] = "M6 4h4v16H6zM14 4h4v16h-4z",
        [Play] = "M7 4l13 8-13 8z",
        [Clear] = "M4 6h16v2H4zM9 3h6v2H9zM6 9h12l-1 12H7z",
    };

    /// <summary>The keys a monitor can pick.</summary>
    public static IReadOnlyList<string> MonitorIcons { get; } = [PullRequest, Heartbeat, Test, Pylon, Bell, Alert, Rocket];

    public static Geometry Geometry(string key) =>
        Avalonia.Media.Geometry.Parse(Paths.TryGetValue(key, out var data) ? data : Paths[Bell]);

    public static Control Glyph(string key, IBrush fill, double size) => new Viewbox
    {
        Width = size,
        Height = size,
        Child = new Avalonia.Controls.Shapes.Path { Data = Geometry(key), Fill = fill, Width = 24, Height = 24, Stretch = Stretch.None },
    };

    /// <summary>A monitor's icon on a tinted rounded square, for stream records and the Monitors page.</summary>
    public static Control Badge(string key, IBrush accent, double size = 30)
    {
        var tint = accent is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.18) : (IBrush)Ui.Raised;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 4),
            Background = tint,
            Child = new Viewbox
            {
                Margin = new Thickness(size * 0.2),
                Child = new Avalonia.Controls.Shapes.Path { Data = Geometry(key), Fill = accent, Width = 24, Height = 24, Stretch = Stretch.None },
            },
        };
    }
}
