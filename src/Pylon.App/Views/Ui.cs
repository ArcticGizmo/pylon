using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pylon.Core.Events;

namespace Pylon.App.Views;

/// <summary>
/// Palette and small control factories. The palette is Emuwren's (shared with Sprig and Perch): a blue-tinted dark with
/// brighter titles, so Pylon sits with those apps rather than with Postern's darker teal.
/// </summary>
internal static class Ui
{
    public static readonly IBrush Background = Brush("#181820");
    public static readonly IBrush Panel = Brush("#1F1F2A");

    /// <summary>Chrome that frames content (the nav rail, the stream's title bar): the panel colour, as Emuwren's nav.</summary>
    public static readonly IBrush Sunken = Panel;

    public static readonly IBrush Raised = Brush("#2D2D3C");
    public static readonly IBrush Border = Brush("#2D2D3C");
    public static readonly IBrush Foreground = Brush("#E1E1EB");
    public static readonly IBrush Title = Brush("#F5F5FA");
    public static readonly IBrush Muted = Brush("#8C8CA0");

    /// <summary>A step below muted, for a secondary line that mustn't compete with the one above it.</summary>
    public static readonly IBrush Faint = Brush("#65657A");

    public static readonly IBrush Accent = Brush("#60A5FA");

    /// <summary>The accent at low opacity: the selected nav item and tab.</summary>
    public static readonly IBrush AccentTint = Brush("#2660A5FA");

    public static readonly IBrush Warning = Brush("#FBBF24");
    public static readonly IBrush Danger = Brush("#F87171");
    public static readonly IBrush Ok = Brush("#4ADE80");
    public static readonly IBrush Info = Brush("#60A5FA");
    public static readonly IBrush Merged = Brush("#A78BFA");
    public static readonly IBrush Neutral = Brush("#75758A");
    public static readonly IBrush Dev = Brush("#FF5FB0");
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

    /// <summary>Where a page's content starts, as Emuwren's pages.</summary>
    public static readonly Thickness PageMargin = new(32, 28, 32, 24);

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeight.SemiBold,
        Foreground = Title,
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

    /// <summary>A small-caps section label above a value.</summary>
    public static Control Field(string label, Control value) => new StackPanel
    {
        Spacing = 6,
        Children =
        {
            new TextBlock
            {
                Text = label.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
            },
            value,
        },
    };

    /// <summary>Primary is the accented call to action; everything else is the neutral button.</summary>
    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 8),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Background = primary ? Accent : Raised,
            Foreground = primary ? Brush("#0B1220") : Foreground,
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
        };
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

    /// <summary>
    /// A chromeless clickable item (a nav entry, a tab) that carries its own hover and selection, as Emuwren's nav. Not
    /// a Button: Fluent repaints a button's template on hover, which fights the tinted selected state. Selected is
    /// <see cref="AccentTint"/> as the background.
    /// </summary>
    public static Border Selectable(Control content, Action onClick)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = content,
        };
        item.Tapped += (_, _) => onClick();
        item.PointerEntered += (_, _) =>
        {
            if (item.Background != AccentTint)
                item.Background = Raised;
        };
        item.PointerExited += (_, _) =>
        {
            if (item.Background != AccentTint)
                item.Background = Brushes.Transparent;
        };
        return item;
    }

    public static Border Card(Control child) => new()
    {
        Background = Panel,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(20, 18),
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
