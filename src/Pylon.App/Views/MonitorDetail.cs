using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pylon.Broker;
using Pylon.Broker.Monitors;
using Pylon.Core.Monitors;
using Pylon.Sources.GitHub;

namespace Pylon.App.Views;

/// <summary>
/// One monitor, opened up: every poll it made and what came of it (Activity), what it remembers about each thing it
/// watches (Watching), and what its polling costs (Budget). This is where "why didn't I hear about that?" gets answered,
/// and where the S1 assumptions are measured.
/// </summary>
internal sealed class MonitorDetail : UserControl
{
    public enum Tab { Activity, Watching, Budget }

    const int PageSize = 100;
    const int MaxLoaded = 1000;

    readonly PylonRuntime _runtime;
    readonly StackPanel _header = new() { Spacing = 4 };
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    readonly Dictionary<Tab, (Border Item, TextBlock Label)> _tabButtons = [];
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _activity = new() { Spacing = 10 };
    readonly StackPanel _polls = new() { Spacing = 4 };
    readonly StackPanel _watching = new() { Spacing = 10 };
    readonly StackPanel _budget = new() { Spacing = 14 };
    readonly CheckBox _hideQuiet = new() { Content = "Hide polls where nothing changed", Margin = new Thickness(12, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly Button _older;
    readonly List<PollRecord> _loaded = [];
    readonly HashSet<long> _expanded = [];
    readonly Dictionary<long, Control> _rows = [];
    Tab _tab = Tab.Activity;
    bool _exhausted;

    public MonitorDetail(PylonRuntime runtime, Action back)
    {
        _runtime = runtime;
        _older = Ui.Button("Load older polls", LoadOlder);

        foreach (var tab in Enum.GetValues<Tab>())
        {
            var label = new TextBlock { Text = tab.ToString(), FontSize = 14 };
            var item = Ui.Selectable(label, () => Select(tab));
            item.Padding = new Thickness(14, 8);
            _tabButtons[tab] = (item, label);
            _tabs.Children.Add(item);
        }

        _hideQuiet.IsCheckedChanged += (_, _) => RenderPolls();
        _activity.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Right(_hideQuiet),
                Ui.Text("Newest first. Click a poll to see each request and what it noticed about each pull request, including changes kept quiet and why.", Ui.Muted, 12),
            },
        });
        _activity.Children.Add(_polls);
        _activity.Children.Add(_older);

        var backButton = Ui.Button("← Monitors", back);
        backButton.MinWidth = 0;
        Content = new DockPanel
        {
            Margin = new Thickness(Ui.PageMargin.Left, Ui.PageMargin.Top, Ui.PageMargin.Right, 0),
            Children =
            {
                Ui.Top(new StackPanel { Spacing = 10, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { backButton } }, _header } }),
                Ui.Top(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8), Margin = new Thickness(0, 14, 0, 14), Child = _tabs }),
                _scroll,
            },
        };
    }

    public string? MonitorId { get; private set; }

    public Tab Current => _tab;

    /// <summary>Opens a monitor, optionally with one poll expanded and scrolled to.</summary>
    public void Show(string monitorId, long? pollId = null, Tab tab = Tab.Activity)
    {
        MonitorId = monitorId;
        _loaded.Clear();
        _expanded.Clear();
        _exhausted = false;
        LoadPage();
        if (pollId is { } id)
        {
            while (!_exhausted && _loaded.All(p => p.Id != id))
                LoadPage();
            if (_loaded.Any(p => p.Id == id))
                _expanded.Add(id);
            tab = Tab.Activity;
            _hideQuiet.IsChecked = false;
        }
        RefreshHeader();
        Select(tab);
        if (pollId is { } target)
            Dispatcher.UIThread.Post(() => _rows.GetValueOrDefault(target)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    public void Select(Tab tab)
    {
        _tab = tab;
        foreach (var (key, (item, label)) in _tabButtons)
        {
            var selected = key == tab;
            item.Background = selected ? Ui.AccentTint : Brushes.Transparent;
            label.Foreground = selected ? Ui.Title : Ui.Muted;
        }
        switch (tab)
        {
            case Tab.Activity:
                RenderPolls();
                _scroll.Content = _activity;
                break;
            case Tab.Watching:
                RenderWatching();
                _scroll.Content = _watching;
                break;
            case Tab.Budget:
                RenderBudget();
                _scroll.Content = _budget;
                break;
        }
        _scroll.Offset = default;
    }

    /// <summary>A new poll from any monitor; only this monitor's matter.</summary>
    public void OnPolled(PollRecord poll)
    {
        if (poll.MonitorId != MonitorId)
            return;
        _loaded.Insert(0, poll);
        if (_loaded.Count > MaxLoaded)
            _loaded.RemoveRange(MaxLoaded, _loaded.Count - MaxLoaded);
        RefreshHeader();
        if (!IsVisible)
            return;
        switch (_tab)
        {
            case Tab.Activity:
                RenderPolls();
                break;
            case Tab.Watching:
                RenderWatching();
                break;
            case Tab.Budget:
                RenderBudget();
                break;
        }
    }

    public void RefreshHeader()
    {
        _header.Children.Clear();
        if (MonitorId is null || _runtime.Monitors.Definitions.FirstOrDefault(d => d.Id == MonitorId) is not { } definition)
        {
            _header.Children.Add(Ui.Heading("This monitor has been removed."));
            return;
        }
        var status = _runtime.Monitors.Status().FirstOrDefault(s => s.Id == MonitorId);
        var now = DateTimeOffset.UtcNow;
        var state = status?.State ?? MonitorState.Starting;
        var next = status?.NextPollAt is { } at && at > now ? $" · next poll in {(int)(at - now).TotalSeconds}s" : "";
        var checking = definition.Option(GitHubEvents.CheckAssumptions, false);

        var poll = Ui.Button("Poll now", () => _runtime.Monitors.PollNow(definition.Id), primary: true);
        _header.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Right(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = state.ToString(), Foreground = StateColour(state), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        poll,
                    },
                }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        Icons.Badge(definition.Icon.Length > 0 ? definition.Icon : StreamWindow.DefaultIcon(definition.Source), Ui.AccentFor(definition.Accent), 38),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = definition.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Ui.Title },
                                Ui.Text($"{definition.Id} · every {definition.PollSeconds}s · last poll {Ui.Ago(status?.LastPollAt, now)}{next}", Ui.Muted, 12),
                            },
                        },
                    },
                },
            },
        });
        if (definition.Source == GitHubSource.SourceName)
            _header.Children.Add(Ui.Text($"{string.Join(", ", definition.Targets)} via {definition.Connection}", Ui.Foreground, 12.5));
        if (checking)
            _header.Children.Add(Ui.Text("Checking assumptions: reviews are fetched for every open PR and a 304 on the pulls list is fetched again, so this monitor makes more requests than usual.", Ui.Warning, 12));
        if (status?.Detail is { Length: > 0 } detail && state is MonitorState.Error or MonitorState.Invalid or MonitorState.WaitingForPostern)
            _header.Children.Add(Ui.Text(detail, Ui.Warning, 12));
    }

    static IBrush StateColour(MonitorState state) => state switch
    {
        MonitorState.Idle or MonitorState.Polling => Ui.Ok,
        MonitorState.Error or MonitorState.Invalid => Ui.Danger,
        MonitorState.WaitingForPostern => Ui.Warning,
        _ => Ui.Muted,
    };

    void LoadPage()
    {
        if (MonitorId is null || _exhausted)
            return;
        var page = _runtime.Journal.Recent(MonitorId, _loaded.Count > 0 ? _loaded[^1].Id : null, PageSize);
        _loaded.AddRange(page);
        _exhausted = page.Count < PageSize;
    }

    void LoadOlder()
    {
        LoadPage();
        RenderPolls();
    }

    // ── Activity ────────────────────────────────────────────────────────────────────────────────

    static bool IsQuiet(PollRecord poll) =>
        poll.Outcome == PollOutcome.Ok && poll.Observations.Count == 0 && poll.Notes.Count == 0 && poll.Sequences.Count == 0;

    void RenderPolls()
    {
        _polls.Children.Clear();
        _rows.Clear();
        var shown = _hideQuiet.IsChecked == true ? _loaded.Where(p => !IsQuiet(p)).ToList() : _loaded;
        if (_loaded.Count == 0)
            _polls.Children.Add(Ui.Text("No polls recorded yet. Polls are kept for 3 days; response bodies for a day (at most 100 MB).", Ui.Muted));
        else if (shown.Count == 0)
            _polls.Children.Add(Ui.Text("Nothing changed in any of the loaded polls.", Ui.Muted));
        foreach (var poll in shown)
        {
            var row = PollRow(poll);
            _rows[poll.Id] = row;
            _polls.Children.Add(row);
        }
        _older.IsVisible = !_exhausted;
    }

    Control PollRow(PollRecord poll)
    {
        var expanded = _expanded.Contains(poll.Id);
        var line = new DockPanel();
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (poll.Sequences.Count > 0)
            chips.Children.Add(Chip($"{poll.Sequences.Count} event(s)", Ui.Accent));
        if (poll.HasWarnings)
            chips.Children.Add(Chip("assumption broken", Ui.Warning));
        if (poll.Outcome != PollOutcome.Ok)
            chips.Children.Add(Chip(poll.Outcome == PollOutcome.Throttled ? "throttled" : "error", poll.Outcome == PollOutcome.Throttled ? Ui.Warning : Ui.Danger));
        chips.Children.Add(Ui.Text($"{poll.Milliseconds:N0} ms", Ui.Muted, 11.5));
        if (poll.RateLimit?.Remaining is { } left)
            chips.Children.Add(Ui.Text($"{left:N0} left", Ui.Muted, 11.5));
        line.Children.Add(Ui.Right(chips));

        var time = poll.StartedAt.ToLocalTime();
        var stamp = time.Date == DateTime.Today ? time.ToString("HH:mm:ss", CultureInfo.CurrentCulture) : time.ToString("d MMM HH:mm:ss", CultureInfo.CurrentCulture);
        line.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = (expanded ? "▾ " : "▸ ") + stamp, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Foreground, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Headline(poll), FontSize = 12.5, Foreground = IsQuiet(poll) ? Ui.Muted : Ui.Foreground, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });

        var head = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = line };
        head.Tapped += (_, _) =>
        {
            if (!_expanded.Remove(poll.Id))
                _expanded.Add(poll.Id);
            var index = _polls.Children.IndexOf(_rows[poll.Id]);
            var replacement = PollRow(poll);
            _rows[poll.Id] = replacement;
            if (index >= 0)
                _polls.Children[index] = replacement;
        };

        var body = new StackPanel { Spacing = 8, Children = { head } };
        if (expanded)
            body.Children.Add(PollDetail(poll));
        return new Border
        {
            Background = expanded ? Ui.Panel : Ui.Background,
            BorderBrush = poll.HasWarnings ? Ui.Warning : expanded ? Ui.Border : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Child = body,
        };
    }

    static string Headline(PollRecord poll)
    {
        if (poll.Outcome != PollOutcome.Ok && poll.Requests == 0)
            return poll.Error ?? "Failed.";
        var parts = new List<string> { $"{poll.Requests} request(s)" };
        if (poll.NotModified > 0)
            parts.Add($"{poll.NotModified}×304");
        var full = poll.Calls.Count(c => c.Status is >= 200 and < 300);
        if (full > 0)
            parts.Add($"{full}×200");
        if (poll.Observations.Count > 0)
            parts.Add($"{poll.Observations.Count} PR(s) changed");
        else if (poll.Requests > 0 && poll.Outcome == PollOutcome.Ok && poll.Notes.Count == 0)
            parts.Add("nothing changed");
        if (poll.Notes.FirstOrDefault() is { } note)
            parts.Add(note.Text);
        if (poll.Outcome != PollOutcome.Ok && poll.Error is { } error)
            parts.Add(error);
        return string.Join(" · ", parts);
    }

    Control PollDetail(PollRecord poll)
    {
        var detail = new StackPanel { Spacing = 10, Margin = new Thickness(18, 2, 0, 4) };
        if (poll.Error is { } error)
            detail.Children.Add(Ui.Text(error, poll.Outcome == PollOutcome.Throttled ? Ui.Warning : Ui.Danger, 12.5));
        foreach (var note in poll.Notes)
            detail.Children.Add(Note(note));

        if (poll.Observations.Count > 0)
        {
            var observations = new StackPanel { Spacing = 10 };
            foreach (var observation in poll.Observations)
                observations.Children.Add(ObservationView(observation));
            detail.Children.Add(Ui.Field("What it noticed", observations));
        }

        if (poll.Sequences.Count > 0)
        {
            var events = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var sequence in poll.Sequences)
            {
                var record = _runtime.Store.ReadAfter(sequence - 1, 1).FirstOrDefault(r => r.Sequence == sequence);
                var button = Ui.Button(record is null ? $"#{sequence} (pruned)" : $"#{sequence} {record.Type}", () =>
                {
                    if (record is null)
                        return;
                    var monitor = _runtime.Monitors.Definitions.FirstOrDefault(d => d.Id == record.MonitorId);
                    var window = new PayloadWindow(record, record.HasPayload ? _runtime.Store.GetPayload(record.Sequence) : null, monitor);
                    window.Show();
                });
                button.Margin = new Thickness(0, 0, 6, 6);
                button.IsEnabled = record is not null;
                events.Children.Add(button);
            }
            detail.Children.Add(Ui.Field("Events reported", events));
        }

        if (poll.Calls.Count > 0)
        {
            var bodies = _runtime.Journal.BodiesFor(poll.Id);
            var calls = new StackPanel { Spacing = 6 };
            for (var i = 0; i < poll.Calls.Count; i++)
                calls.Children.Add(CallView(poll, i, bodies.Contains(i)));
            detail.Children.Add(Ui.Field("Requests", calls));
        }
        return detail;
    }

    static Control ObservationView(Observation observation)
    {
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new SelectableTextBlock
        {
            Text = observation.Title is { Length: > 0 } title ? $"{observation.Resource}  {title}" : observation.Resource,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.Foreground,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var change in observation.Changes)
            lines.Children.Add(Mono(change.Before is null ? $"  {change.Field}: {change.After}" : $"  {change.Field}: {Blank(change.Before)} → {Blank(change.After)}", Ui.Foreground));
        foreach (var type in observation.Reported)
            lines.Children.Add(Mono($"  → reported {type}", Ui.Ok));
        foreach (var quiet in observation.Quiet)
            lines.Children.Add(Mono($"  · kept quiet: {quiet.Event} ({quiet.Reason})", Ui.Muted));
        foreach (var note in observation.Notes)
            lines.Children.Add(Note(note));
        if (observation.Changes.Count > 0 && observation.Reported.Count == 0 && observation.Quiet.Count == 0 && observation.Notes.Count == 0)
            lines.Children.Add(Mono("  · no rule cares about these fields", Ui.Muted));
        return lines;
    }

    static string Blank(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;

    Control CallView(PollRecord poll, int index, bool hasBody)
    {
        var call = poll.Calls[index];
        var colour = call.Status switch
        {
            304 => Ui.Muted,
            >= 200 and < 300 => Ui.Foreground,
            429 => Ui.Warning,
            _ => Ui.Danger,
        };
        var meta = new List<string>();
        if (call.IfNoneMatch is not null || call.ETag is not null)
            meta.Add(call.NotModified ? $"ETag {call.IfNoneMatch} matched" : call.IfNoneMatch is null ? $"unconditional, got ETag {call.ETag}" : $"ETag {call.IfNoneMatch} → {call.ETag}");
        if (call.Bytes > 0)
            meta.Add(call.Bytes >= 1024 ? $"{call.Bytes / 1024.0:N1} KB" : $"{call.Bytes} B");
        meta.Add($"{call.Milliseconds:N0} ms");
        if (call.RateLimit?.Remaining is { } left)
            meta.Add($"{left:N0} left");
        if (call.RateCost is { } cost)
            meta.Add($"cost {cost}");
        if (call.PosternError is { } posternError)
            meta.Add($"Postern: {posternError}");

        var text = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new SelectableTextBlock
                {
                    Inlines = new Avalonia.Controls.Documents.InlineCollection
                    {
                        new Avalonia.Controls.Documents.Run($"{call.Status} ") { Foreground = colour, FontWeight = FontWeight.SemiBold },
                        new Avalonia.Controls.Documents.Run($"{call.Method} {call.Path}") { Foreground = Ui.Foreground },
                    },
                    FontFamily = Ui.Mono,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                Ui.Text(string.Join(" · ", meta), Ui.Muted, 11.5),
            },
        };
        if (call.Purpose is { } purpose)
            text.Children.Add(Ui.Text(purpose, Ui.Info, 11.5));

        var row = new DockPanel();
        if (hasBody)
        {
            var open = Ui.Button("Body", () =>
            {
                if (_runtime.Journal.Body(poll.Id, index) is { } body)
                    new TextWindow($"{call.Status} {call.Method} {call.Path}", $"{poll.StartedAt.ToLocalTime():ddd d MMM HH:mm:ss} · {call.Bytes:N0} characters", body).Show();
            });
            open.MinWidth = 0;
            open.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(Ui.Right(open));
        }
        row.Children.Add(text);
        return row;
    }

    static Control Note(ObservationNote note)
    {
        var colour = note.Level == NoteLevel.Warning ? Ui.Warning : Ui.Info;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 1, 0, 1) };
        if (note.Assumption is { } assumption)
            panel.Children.Add(Chip(assumption, colour));
        panel.Children.Add(new TextBlock { Text = note.Text, Foreground = colour, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    static Control Chip(string text, IBrush colour) => new Border
    {
        BorderBrush = colour,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = colour, FontSize = 11 },
    };

    static SelectableTextBlock Mono(string text, IBrush colour) => new()
    {
        Text = text,
        FontFamily = Ui.Mono,
        FontSize = 12,
        Foreground = colour,
        TextWrapping = TextWrapping.Wrap,
    };

    // ── Watching ────────────────────────────────────────────────────────────────────────────────

    void RenderWatching()
    {
        _watching.Children.Clear();
        if (MonitorId is null)
            return;
        _watching.Children.Add(Ui.Text("What this monitor remembers about each thing it watches: the fingerprint every poll is compared with. Closed pull requests are forgotten once they drop out of the 50 most recently updated.", Ui.Muted, 12));
        var items = _runtime.Monitors.Watched(MonitorId);
        if (items.Count == 0)
            _watching.Children.Add(Ui.Text("Nothing yet. The first poll of each repository records a baseline.", Ui.Muted));
        foreach (var item in items)
        {
            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowSpacing = 2 };
            for (var i = 0; i < item.Fields.Count; i++)
            {
                fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = Ui.Text(item.Fields[i].Field, Ui.Muted, 12);
                var value = Mono(item.Fields[i].Value, Ui.Foreground);
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                fields.Children.Add(label);
                fields.Children.Add(value);
            }
            var title = new DockPanel();
            if (item.Url is { } url)
            {
                var open = Ui.Button("Open on GitHub", () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
                open.MinWidth = 0;
                title.Children.Add(Ui.Right(open));
            }
            title.Children.Add(new SelectableTextBlock
            {
                Text = item.Title is { Length: > 0 } t ? $"{item.Resource}  {t}" : item.Resource,
                FontWeight = FontWeight.SemiBold,
                Foreground = Ui.Foreground,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _watching.Children.Add(Ui.Card(new StackPanel { Spacing = 8, Children = { title, fields } }));
        }
    }

    // ── Budget ──────────────────────────────────────────────────────────────────────────────────

    void RenderBudget()
    {
        _budget.Children.Clear();
        if (MonitorId is null)
            return;
        var hours = _runtime.Journal.Hours(MonitorId, DateTimeOffset.UtcNow.AddHours(-24));
        if (hours.Count == 0)
        {
            _budget.Children.Add(Ui.Text("No polls in the last 24 hours.", Ui.Muted));
            return;
        }

        var requests = hours.Sum(h => h.Requests);
        var notModified = hours.Sum(h => h.NotModified);
        var billable = requests - notModified;
        // Projected from how long polling has actually been going, so a few minutes of history isn't read as an hour.
        var span = DateTimeOffset.UtcNow - hours.Min(h => h.FirstPoll);
        var perHour = billable / Math.Max(span.TotalHours, 1.0 / 60);
        var latest = _loaded.FirstOrDefault(p => p.RateLimit is not null)?.RateLimit;

        var summary = new StackPanel { Spacing = 6 };
        summary.Children.Add(Ui.Text(
            $"Last 24 hours: {hours.Sum(h => h.Polls):N0} polls, {requests:N0} requests, {notModified:N0} unchanged (304, {(requests == 0 ? 0 : 100.0 * notModified / requests):N0}%), {hours.Sum(h => h.Events):N0} events, {hours.Sum(h => h.Errors):N0} failed polls.",
            Ui.Foreground));
        var limitText = latest?.Limit is { } limit && limit > 0 ? $", {100 * perHour / limit:N1}% of a {limit:N0}/hour limit" : "";
        summary.Children.Add(span < TimeSpan.FromMinutes(10)
            ? Ui.Text($"Requests that count against the rate limit (everything but 304s): {billable:N0} in the {Math.Max(1, (int)span.TotalMinutes)} minute(s) of history so far; give it ten minutes for an hourly rate.", Ui.Foreground)
            : Ui.Text($"Requests that count against the rate limit (everything but 304s): about {perHour:N0} an hour{limitText}.", Ui.Foreground));
        if (latest?.Remaining is { } remaining)
            summary.Children.Add(Ui.Text(
                $"Rate limit now: {remaining:N0}{(latest.Limit is { } l ? $" of {l:N0}" : "")} left{(latest.Reset is { } reset ? $", resets at {reset.ToLocalTime():HH:mm}" : "")}{(latest.Resource is { } r ? $" ({r})" : "")}. That's shared with everything else using the same token.",
                Ui.Muted, 12.5));
        _budget.Children.Add(Ui.Card(summary));

        var measured304 = hours.Sum(h => h.Measured304);
        var cost304 = hours.Sum(h => h.Cost304);
        var measured200 = hours.Sum(h => h.Measured200);
        var cost200 = hours.Sum(h => h.Cost200);
        var evidence = new StackPanel { Spacing = 6 };
        evidence.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Chip("A5", measured304 == 0 ? Ui.Muted : cost304 == 0 ? Ui.Info : Ui.Warning), Ui.Text("Are 304s free?", Ui.Foreground) },
        });
        evidence.Children.Add(Ui.Text(
            measured304 == 0 ? "Not measured yet. A call's cost is the drop in the remaining limit since the call before it in the same poll, so it needs a poll with more than one request."
            : cost304 == 0 ? $"So far, yes: {measured304:N0} 304s measured, none of them used any rate limit."
            : $"Apparently not: {measured304:N0} 304s measured used {cost304:N0} of the rate limit. (Another app using the same token at that moment can add noise.)",
            measured304 > 0 && cost304 > 0 ? Ui.Warning : Ui.Muted, 12.5));
        if (measured200 > 0)
            evidence.Children.Add(Ui.Text($"For comparison, {measured200:N0} full responses measured used {cost200:N0}.", Ui.Muted, 12.5));
        _budget.Children.Add(Ui.Card(evidence));

        var columns = new[] { "Hour", "Polls", "Requests", "304s", "Counted", "Events", "Failed", "Flags", "Lowest left" };
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("90,70,80,70,80,70,70,70,*"), RowSpacing = 3 };
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < columns.Length; c++)
            Cell(table, 0, c, Ui.Text(columns[c].ToUpperInvariant(), Ui.Muted, 10.5));
        for (var i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] values =
            [
                h.Hour.ToLocalTime().ToString("HH:00", CultureInfo.CurrentCulture),
                h.Polls.ToString("N0", CultureInfo.CurrentCulture),
                h.Requests.ToString("N0", CultureInfo.CurrentCulture),
                h.NotModified.ToString("N0", CultureInfo.CurrentCulture),
                (h.Requests - h.NotModified).ToString("N0", CultureInfo.CurrentCulture),
                h.Events.ToString("N0", CultureInfo.CurrentCulture),
                h.Errors.ToString("N0", CultureInfo.CurrentCulture),
                h.Warnings.ToString("N0", CultureInfo.CurrentCulture),
                h.MinRemaining?.ToString("N0", CultureInfo.CurrentCulture) ?? "—",
            ];
            for (var c = 0; c < values.Length; c++)
                Cell(table, i + 1, c, Mono(values[c], c == 6 && h.Errors > 0 ? Ui.Danger : c == 7 && h.Warnings > 0 ? Ui.Warning : Ui.Foreground));
        }
        _budget.Children.Add(Ui.Field("By hour (\"counted\" is what uses the rate limit; \"flags\" are polls where an assumption broke)", Ui.Card(table)));
    }

    static void Cell(Grid grid, int row, int column, Control control)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
