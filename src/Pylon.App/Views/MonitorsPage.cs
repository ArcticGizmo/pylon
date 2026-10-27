using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pylon.Broker;
using Pylon.Broker.Monitors;
using Pylon.Core.Monitors;

namespace Pylon.App.Views;

/// <summary>
/// Every monitor with what it's doing right now, and Pylon's standing with Postern (which every GitHub monitor depends
/// on). Edits save monitors.json and restart the loops; fingerprints survive unless "Start over" is used.
/// </summary>
internal sealed class MonitorsPage : UserControl
{
    readonly PylonRuntime _runtime;
    readonly StackPanel _postern = new() { Spacing = 6 };
    readonly StackPanel _list = new() { Spacing = 10 };
    readonly TextBlock _error = Ui.Text("", Ui.Danger, 12);
    readonly DispatcherTimer _tick;

    public MonitorsPage(PylonRuntime runtime)
    {
        _runtime = runtime;
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var source in runtime.Monitors.Sources)
            add.Children.Add(Ui.Button($"Add {source.DisplayName}", () => Edit(source, null), primary: source.UsesPostern));

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    Ui.Heading("Monitors"),
                    Ui.Text("Each monitor polls one source and turns what changed into events. GitHub monitors call out through Postern, so the first one (and each new owner) needs your approval in Postern.", Ui.Muted),
                    Ui.Card(_postern),
                    add,
                    _error,
                    _list,
                },
            },
        };

        // Relative times ("12s ago") and next-poll countdowns need redrawing even when nothing else changes.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => _tick.Start();
        DetachedFromVisualTree += (_, _) => _tick.Stop();
        Refresh();
    }

    public void Refresh()
    {
        RefreshPostern();
        _error.Text = _runtime.Monitors.LoadError is { } loadError ? $"monitors.json couldn't be read, so nothing is running: {loadError}" : "";
        _error.IsVisible = _error.Text.Length > 0;

        _list.Children.Clear();
        var statuses = _runtime.Monitors.Status().ToDictionary(s => s.Id);
        var definitions = _runtime.Monitors.Definitions;
        if (definitions.Count == 0)
            _list.Children.Add(Ui.Text("No monitors yet. Add a GitHub monitor to watch pull requests, or a heartbeat to see the stream working.", Ui.Muted));
        foreach (var definition in definitions)
            _list.Children.Add(Card(definition, statuses.GetValueOrDefault(definition.Id)));
    }

    void RefreshPostern()
    {
        _postern.Children.Clear();
        var wanted = _runtime.Monitors.WantedScopes();
        if (_runtime.Postern is not { } postern)
        {
            _postern.Children.Add(Ui.Text("Postern is bypassed in this run (monitors call a test server directly).", Ui.Muted));
            return;
        }
        var (colour, label) = postern.State switch
        {
            PosternState.Approved => (Ui.Ok, "Approved"),
            PosternState.WaitingForApproval => (Ui.Warning, "Waiting for your approval in Postern"),
            PosternState.Denied => (Ui.Danger, "Refused"),
            PosternState.NotRunning => (Ui.Warning, "Postern isn't running"),
            PosternState.Error => (Ui.Danger, "Problem"),
            _ => (Ui.Muted, "Checking…"),
        };
        var again = Ui.Button("Ask Postern again", _runtime.Monitors.AskPosternAgain);
        again.IsVisible = postern.State is PosternState.Denied or PosternState.NotRunning or PosternState.Error;
        _postern.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Right(again),
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = $"Postern ({postern.PipeName}): {label}", Foreground = colour, FontWeight = FontWeight.SemiBold },
                        Ui.Text(postern.Detail ?? "", Ui.Muted, 12),
                    },
                },
            },
        });
        if (wanted.Count > 0)
        {
            var scopes = new StackPanel { Spacing = 1 };
            foreach (var scope in wanted)
                scopes.Children.Add(Ui.Code((postern.Approved.Contains(scope, StringComparer.OrdinalIgnoreCase) ? "✓ " : "· ") + scope,
                    postern.Approved.Contains(scope, StringComparer.OrdinalIgnoreCase) ? Ui.Foreground : Ui.Warning));
            _postern.Children.Add(Ui.Field("Scopes the monitors need", scopes));
        }
    }

    Control Card(MonitorDefinition definition, MonitorStatus? status)
    {
        var source = _runtime.Monitors.Source(definition.Source);
        var now = DateTimeOffset.UtcNow;
        var state = status?.State ?? MonitorState.Starting;
        var stateColour = state switch
        {
            MonitorState.Idle or MonitorState.Polling => Ui.Ok,
            MonitorState.Error or MonitorState.Invalid => Ui.Danger,
            MonitorState.WaitingForPostern => Ui.Warning,
            _ => Ui.Muted,
        };

        var what = definition.Source == "github"
            ? $"{string.Join(", ", definition.Targets)} via {definition.Connection} · {string.Join(", ", definition.Events)}"
            : source?.DisplayName ?? definition.Source;
        var numbers = status is null ? "" :
            $"Last poll {Ui.Ago(status.LastPollAt, now)} · last event {Ui.Ago(status.LastEventAt, now)} · {status.Requests} request(s), {status.NotModified} unchanged (304) · {status.Events} event(s)";
        var next = status?.NextPollAt is { } at && at > now ? $"next poll in {(int)(at - now).TotalSeconds}s" : "";

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                Ui.Button("Edit", () => { if (source is not null) Edit(source, definition); }),
                Ui.Button("Poll now", () => _runtime.Monitors.PollNow(definition.Id)),
                Ui.Button(definition.Enabled ? "Turn off" : "Turn on", () => Replace(definition, definition with { Enabled = !definition.Enabled })),
                Ui.Button("Start over", () => StartOver(definition)),
                Ui.Button("Remove", () => Remove(definition)),
            },
        };
        ToolTip.SetTip(buttons.Children[3], "Forget what this monitor has seen: its next poll is a fresh baseline and reports nothing.");

        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        Ui.Right(new TextBlock { Text = state.ToString(), Foreground = stateColour, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children =
                            {
                                Icons.Badge(definition.Icon.Length > 0 ? definition.Icon : StreamWindow.DefaultIcon(definition.Source), Ui.AccentFor(definition.Accent), 30),
                                new StackPanel
                                {
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Children =
                                    {
                                        new TextBlock { Text = definition.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground },
                                        Ui.Text($"{definition.Id} · every {definition.PollSeconds}s", Ui.Muted, 11.5),
                                    },
                                },
                            },
                        },
                    },
                },
                Ui.Text(what, Ui.Foreground, 12.5),
            },
        };
        // Idle's detail is the request count, which the numbers line already shows.
        if (status?.Detail is { Length: > 0 } detail && state is not (MonitorState.Idle or MonitorState.Polling))
            body.Children.Add(Ui.Text(detail, state is MonitorState.Error or MonitorState.Invalid or MonitorState.WaitingForPostern ? Ui.Warning : Ui.Muted, 12));
        if (numbers.Length > 0)
            body.Children.Add(Ui.Text($"{numbers}{(next.Length > 0 ? " · " + next : "")}", Ui.Muted, 11.5));
        body.Children.Add(buttons);
        return Ui.Card(body);
    }

    async void Edit(ISource source, MonitorDefinition? original)
    {
        var editor = new MonitorEditor(source, original, _runtime.Monitors.Definitions);
        if (TopLevel.GetTopLevel(this) is Window owner)
            await editor.ShowDialog(owner);
        else
            editor.Show();
        if (editor.Result is not { } result)
            return;
        var list = _runtime.Monitors.Definitions.ToList();
        var index = original is null ? -1 : list.FindIndex(d => d.Id == original.Id);
        if (index >= 0)
            list[index] = result;
        else
            list.Add(result);
        Save(list);
    }

    void Replace(MonitorDefinition original, MonitorDefinition updated) =>
        Save([.. _runtime.Monitors.Definitions.Select(d => d.Id == original.Id ? updated : d)]);

    void Remove(MonitorDefinition definition)
    {
        Save([.. _runtime.Monitors.Definitions.Where(d => d.Id != definition.Id)]);
        _runtime.Monitors.ResetState(definition.Id);
    }

    void StartOver(MonitorDefinition definition)
    {
        _runtime.Monitors.ResetState(definition.Id);
        Save([.. _runtime.Monitors.Definitions]);
    }

    void Save(IReadOnlyList<MonitorDefinition> definitions)
    {
        try
        {
            _runtime.Monitors.Save(definitions);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _error.Text = ex.Message;
            _error.IsVisible = true;
            return;
        }
        Refresh();
    }
}
