using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.App.Views;

/// <summary>
/// One event in full: what subscribers got, and what Pylon observed to produce it. For a poll event the payload holds
/// the PR before and after plus the requests made, so "what changed" is worked out and listed first.
/// </summary>
internal sealed class PayloadWindow : Window
{
    // Shown to a person, never embedded in HTML, so "+00:00" rather than "+00:00".
    static readonly JsonSerializerOptions Indented = new(PylonEvent.Json)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string _payloadText;
    readonly string _eventJson;

    public PayloadWindow(EventRecord record, Payload? payload, MonitorDefinition? monitor)
    {
        Title = $"Pylon: event #{record.Sequence}";
        Icon = TrayArt.WindowIcon();
        Width = 760;
        Height = 720;
        MinWidth = 480;
        MinHeight = 360;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _eventJson = JsonSerializer.Serialize(record.ToContract(), Indented);
        _payloadText = payload is null ? "" : Pretty(payload);

        var (icon, accent) = StreamWindow.Look(record, monitor);
        var header = new DockPanel
        {
            Children =
            {
                Ui.Right(new Border
                {
                    Background = Ui.Raised,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Ellipse { Width = 9, Height = 9, Fill = Ui.StatusBrush(record.Status), VerticalAlignment = VerticalAlignment.Center },
                            Ui.Text(Ui.StatusLabel(record.Status), Ui.Foreground, 12),
                        },
                    },
                }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        Icons.Badge(icon, accent, 38),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = record.Summary, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                                Ui.Text($"{record.ObservedAtUtc.ToLocalTime():ddd d MMM HH:mm:ss} · {monitor?.Name ?? "no monitor"} · via {record.Via.ToString().ToLowerInvariant()}", Ui.Muted, 12),
                            },
                        },
                    },
                },
            },
        };

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), RowSpacing = 4 };
        var rows = new List<(string, string)>
        {
            ("Sequence", record.Sequence.ToString()),
            ("Topic", record.Topic),
            ("Type", record.Type),
            ("Kind", record.Kind.ToString().ToLowerInvariant()),
            ("Source", record.Source),
            ("Monitor", record.MonitorId ?? "—"),
            ("Resource", record.ResourceId ?? "—"),
            ("Hint", record.Hint is { Count: > 0 } hint ? string.Join("  ", hint.Select(p => $"{p.Key}={p.Value}")) : "—"),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Text(rows[i].Item1, Ui.Muted, 12);
            var value = new SelectableTextBlock { Text = rows[i].Item2, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Foreground, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            fields.Children.Add(label);
            fields.Children.Add(value);
        }

        var body = new StackPanel { Spacing = 14, Children = { header, Ui.Card(fields) } };

        if (payload is not null && Changes(payload) is { Count: > 0 } changes)
        {
            var list = new StackPanel { Spacing = 3 };
            foreach (var (field, before, after) in changes)
                list.Children.Add(new SelectableTextBlock
                {
                    Text = $"{field}: {before} → {after}",
                    FontFamily = Ui.Mono,
                    FontSize = 12.5,
                    Foreground = Ui.Foreground,
                    TextWrapping = TextWrapping.Wrap,
                });
            body.Children.Add(Ui.Field("What changed", Ui.Card(list)));
        }

        body.Children.Add(Ui.Field(
            payload is null ? "Payload" : $"Payload ({payload.ContentType}, {payload.Content.Length:N0} characters)",
            Ui.Card(payload is null
                ? Ui.Text(record.HasPayload ? "The payload has been pruned (payloads are kept for 7 days)." : "Nothing was kept for this event.", Ui.Muted)
                : new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new SelectableTextBlock { Text = _payloadText, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Foreground, TextWrapping = TextWrapping.NoWrap },
                })));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                Ui.Button("Copy event", () => Copy(_eventJson)),
                Ui.Button("Copy payload", () => Copy(_payloadText)),
                Ui.Button("Close", Close, primary: true),
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                Ui.Bottom(buttons),
                // Only the payload scrolls sideways; everything else wraps, so the header's status stays in view.
                new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
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

    async void Copy(string text)
    {
        if (Clipboard is { } clipboard && text.Length > 0)
            await clipboard.SetTextAsync(text);
    }

    static string Pretty(Payload payload)
    {
        if (!payload.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return payload.Content;
        try
        {
            return JsonNode.Parse(payload.Content)?.ToJsonString(Indented) ?? payload.Content;
        }
        catch (JsonException)
        {
            return payload.Content;
        }
    }

    /// <summary>For poll payloads (<c>before</c> and <c>after</c> objects): each top-level field whose value differs.</summary>
    internal static IReadOnlyList<(string Field, string Before, string After)> Changes(Payload payload)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload.Content) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }
        if (root?["after"] is not JsonObject after)
            return [];
        var before = root["before"] as JsonObject;
        if (before is null)
            return [("(new)", "—", "first seen")];

        var changes = new List<(string, string, string)>();
        foreach (var (name, value) in after)
        {
            var old = before[name];
            if (!JsonNode.DeepEquals(old, value))
                changes.Add((name, Short(old), Short(value)));
        }
        return changes;
    }

    static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString(Compact) ?? "null";
        return text.Length > 120 ? text[..117] + "…" : text;
    }
}
