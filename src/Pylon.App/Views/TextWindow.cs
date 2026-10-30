using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Pylon.App.Views;

/// <summary>A read-only view of one block of text (a response body), pretty-printed when it's JSON, with Copy.</summary>
internal sealed class TextWindow : Window
{
    // Shown to a person, never embedded in HTML.
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _text;

    public TextWindow(string title, string subtitle, string text)
    {
        Title = $"Pylon: {title}";
        Icon = TrayArt.WindowIcon();
        Width = 820;
        Height = 760;
        MinWidth = 420;
        MinHeight = 300;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _text = Pretty(text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                Ui.Button("Copy", Copy),
                Ui.Button("Close", Close, primary: true),
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                Ui.Top(new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(0, 0, 0, 12),
                    Children =
                    {
                        new SelectableTextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground, TextWrapping = TextWrapping.Wrap },
                        Ui.Text(subtitle, Ui.Muted, 12),
                    },
                }),
                Ui.Bottom(buttons),
                Ui.Card(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock { Text = _text, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Foreground, TextWrapping = TextWrapping.NoWrap },
                }),
            },
        };
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

    async void Copy()
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_text);
    }

    static string Pretty(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return text;
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(Indented) ?? text;
        }
        catch (JsonException)
        {
            return text; // a body cut short by the size limit isn't valid JSON any more
        }
    }
}
