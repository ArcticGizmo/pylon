using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pylon.Core.Events;

namespace Pylon.App.Views;

/// <summary>Palette and small control factories, the same as Postern's so the two apps look like siblings.</summary>
internal static class Ui
{
    public static readonly IBrush Sunken = Brush("#101315");
    public static readonly IBrush Background = Brush("#15181B");
    public static readonly IBrush Panel = Brush("#1E2226");
    public static readonly IBrush Raised = Brush("#252A2F");
    public static readonly IBrush Border = Brush("#2E343A");
    public static readonly IBrush Foreground = Brush("#E6E9EC");
    public static readonly IBrush Muted = Brush("#9AA4AE");
    public static readonly IBrush Accent = Brush("#4FB8A8");
    public static readonly IBrush Warning = Brush("#F5A524");
    public static readonly IBrush Danger = Brush("#EF5350");
    public static readonly IBrush Ok = Brush("#4ADE80");
    public static readonly IBrush Info = Brush("#60A5FA");
    public static readonly IBrush Merged = Brush("#A78BFA");
    public static readonly IBrush Neutral = Brush("#6B7580");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>A monitor's accent, falling back to the app accent when the setting isn't a colour.</summary>
    public static IBrush AccentFor(string? hex) => hex is not null && Color.TryParse(hex, out var colour) ? new SolidColorBrush(colour) : Accent;

    /// <summary>The status dot: one colour per meaning, used the same way in the widget, the payload view and the test bench.</summary>
    public static IBrush StatusBrush(EventStatus status) => status switch
    {
        EventStatus.Success => Ok,
        EventStatus.Failure => Danger,
        EventStatus.Attention => Warning,
        EventStatus.Pending => Warning,
        EventStatus.Merged => Merged,
        EventStatus.Info => Info,
        _ => Neutral,
    };

    public static string StatusLabel(EventStatus status) => status switch
    {
        EventStatus.Success => "Success",
        EventStatus.Failure => "Failure",
        EventStatus.Attention => "Needs attention",
        EventStatus.Pending => "Pending",
        EventStatus.Merged => "Merged",
        EventStatus.Info => "Info",
        _ => "Neutral",
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Text(string text, IBrush? colour = null, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = colour ?? Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Code(string text, IBrush? colour = null) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 12.5,
        Foreground = colour ?? Foreground,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A muted label above a value.</summary>
    public static Control Field(string label, Control value) => new StackPanel
    {
        Spacing = 2,
        Children = { Text(label.ToUpperInvariant(), Muted, 10.5), value },
    };

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 6),
        };
        if (primary)
        {
            button.Background = Accent;
            button.Foreground = Brush("#0B1F1C");
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A small square button holding an icon, for title bars.</summary>
    public static Button IconButton(string icon, string tip, Action onClick, double size = 26)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Content = Icons.Glyph(icon, Muted, size - 10),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Card(Control child) => new()
    {
        Background = Panel,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = child,
    };

    public static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    public static Control Bottom(Control control)
    {
        DockPanel.SetDock(control, Dock.Bottom);
        return control;
    }

    public static Control Right(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } when)
            return "never";
        var age = now - when;
        return age.TotalSeconds < 60 ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
            : when.ToLocalTime().ToString("d MMM HH:mm");
    }
}
