using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pylon.Client;
using Pylon.Core.Events;
using Pylon.FakeGitHub;

namespace Pylon.TestBench;

/// <summary>
/// Three columns: the fake GitHub's pull requests and buttons that change them; a payload editor that sends straight
/// to Pylon; and a log of what the fake served, what was sent, and what Pylon emitted (tailed over its pipe).
/// </summary>
internal sealed class BenchWindow : Window
{
    static readonly IBrush Backdrop = Brush("#15181B");
    static readonly IBrush Panel = Brush("#1E2226");
    static readonly IBrush Edge = Brush("#2E343A");
    static readonly IBrush Ink = Brush("#E6E9EC");
    static readonly IBrush Muted = Brush("#9AA4AE");
    static readonly IBrush Accent = Brush("#4FB8A8");
    static readonly IBrush Warning = Brush("#F5A524");
    static readonly IBrush Danger = Brush("#EF5350");
    static readonly IBrush Ok = Brush("#4ADE80");
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    readonly FakeGitHubState _state;
    readonly BenchLog _log;
    readonly Uri _address;
    readonly PylonClient _pylon;
    readonly CancellationTokenSource _stop = new();

    readonly TextBox _repo = new() { Text = "example-org/app" };
    readonly TextBox _title = new() { Text = "Add a widget to the dashboard" };
    readonly TextBox _author = new() { Text = "dev-one" };
    readonly TextBox _actor = new() { Text = "dev-two" };
    readonly CheckBox _draft = new() { Content = "Draft" };
    readonly CheckBox _bot = new() { Content = "Opened by a bot" };
    readonly CheckBox _forward = new() { Content = "Also send each change to Pylon as a webhook (push path)" };
    readonly TextBox _delay = new() { Text = "3", Width = 60 };
    readonly ListBox _pulls = new() { MinHeight = 200 };
    readonly ComboBox _template = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _githubEvent = new() { PlaceholderText = "X-GitHub-Event (empty for a raw Pylon event)" };
    readonly TextBox _payload = new() { AcceptsReturn = true, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    readonly TextBlock _sendResult = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _logRows = new() { Spacing = 1 };
    readonly StatusDot _pylonDot = new();
    readonly TextBlock _pylonText = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _redraw;
    bool _logDirty;

    public BenchWindow(FakeGitHubState state, BenchLog log, Uri address, string pipe)
    {
        _state = state;
        _log = log;
        _address = address;
        _pylon = new PylonClient(new PylonClientOptions { PipeName = pipe });

        Title = $"Pylon test bench · fake GitHub on {address.Authority} · Pylon pipe {pipe}";
        Width = 1400;
        Height = 860;
        MinWidth = 1000;
        MinHeight = 600;
        Background = Backdrop;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.1*,1.2*,1*"), Margin = new Thickness(12), ColumnSpacing = 12 };
        var fake = Column("Fake GitHub", FakeColumn());
        var send = Column("Send a payload to Pylon", SendColumn());
        var logs = Column("What happened", LogColumn());
        Grid.SetColumn(send, 1);
        Grid.SetColumn(logs, 2);
        grid.Children.Add(fake);
        grid.Children.Add(send);
        grid.Children.Add(logs);

        Content = new DockPanel { Children = { Top(TopBar(pipe)), grid } };

        _state.Changed += OnFakeChanged;
        _log.Changed += () => _logDirty = true;
        _redraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _redraw.Tick += (_, _) => Redraw();
        _redraw.Start();
        Closed += (_, _) =>
        {
            _stop.Cancel();
            _redraw.Stop();
            _pylon.Dispose();
        };

        _template.ItemsSource = Templates.Names;
        _template.SelectionChanged += (_, _) => LoadTemplate();
        _template.SelectedIndex = 0;
        RefreshPulls();
        _logDirty = true;
        Redraw();
        _ = Task.Run(() => TailPylon(_stop.Token));
    }

    Control TopBar(string pipe)
    {
        var setup = Button("Postern setup for polling…", ShowSetup);
        return new Border
        {
            Background = Brush("#101315"),
            BorderBrush = Edge,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8),
            Child = new DockPanel
            {
                Children =
                {
                    Right(setup),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 18,
                        Children =
                        {
                            new TextBlock { Text = "Pylon test bench", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center },
                            Text($"Fake GitHub: {_address}", Muted),
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _pylonDot, _pylonText } },
                        },
                    },
                },
            },
        };
    }

    Control FakeColumn()
    {
        var open = Button("Open PR", () => _state.Open(Repo, _title.Text ?? "Untitled", _author.Text ?? "dev-one", _draft.IsChecked == true, _bot.IsChecked == true), primary: true);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, action) in new (string, Action<FakePullRequest>)[]
        {
            ("Ready", pr => _state.MarkReady(pr)),
            ("Request review", pr => _state.RequestReview(pr, Actor)),
            ("Approve", pr => _state.Review(pr, Actor, "APPROVED")),
            ("Request changes", pr => _state.Review(pr, Actor, "CHANGES_REQUESTED")),
            ("Comment", pr => _state.Review(pr, Actor, "COMMENTED")),
            ("Push", pr => _state.Push(pr)),
            ("Checks pending", pr => _state.SetChecks(pr, "pending")),
            ("Checks pass", pr => _state.SetChecks(pr, "passing")),
            ("Checks fail", pr => _state.SetChecks(pr, "failing")),
            ("Merge", pr => _state.Merge(pr, Actor)),
            ("Close", pr => _state.Close(pr, Actor)),
        })
        {
            var button = Button(label, () => OnSelected(action));
            button.Margin = new Thickness(0, 0, 6, 6);
            button.MinWidth = 0;
            actions.Children.Add(button);
        }

        var play = Button("Play a PR's whole life", () => _ = PlayLife());
        var quirks = new StackPanel { Spacing = 2 };
        foreach (var (label, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("Reviews don't move updated_at (breaks A1)", () => !_state.ReviewsBumpUpdatedAt, v => _state.ReviewsBumpUpdatedAt = !v),
            ("Checks move updated_at (breaks A2)", () => _state.ChecksBumpUpdatedAt, v => _state.ChecksBumpUpdatedAt = v),
            ("The pulls list keeps a stale ETag (breaks A4)", () => _state.StaleListETags, v => _state.StaleListETags = v),
        })
        {
            var box = new CheckBox { Content = label, IsChecked = get() };
            box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
            quirks.Children.Add(box);
        }

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("Pylon polls this through Postern (see Postern setup), or gets each change pushed when the box below is ticked.", Muted, 12),
                Field("Repository", _repo),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("2*,*"),
                    ColumnSpacing = 8,
                    Children = { Field("Title", _title), At(Field("Author", _author), 1) },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { open, _draft, _bot } },
                Field("Pull requests (newest change first)", _pulls),
                Field("Reviewer / actor for the buttons below", _actor),
                actions,
                _forward,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { play, Text("seconds between steps", Muted, 12), _delay } },
                Field("Quirks: break the poll strategy's assumptions (a monitor with 'check assumptions' on flags each one)", quirks),
            },
        };
    }

    Control SendColumn()
    {
        var send = Button("Send to Pylon", () => _ = SendPayload(), primary: true);
        var reload = Button("Reload template", LoadTemplate);
        return new DockPanel
        {
            Children =
            {
                Top(new StackPanel
                {
                    Spacing = 8,
                    Margin = new Thickness(0, 0, 0, 8),
                    Children =
                    {
                        Text("A GitHub webhook body goes through the same normaliser push mode will use. A raw Pylon event goes in as it is. Both arrive with via=test.", Muted, 12),
                        Field("Template (uses the repository, author and reviewer on the left)", _template),
                        _githubEvent,
                    },
                }),
                Bottom(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { send, reload } }, _sendResult },
                }),
                _payload,
            },
        };
    }

    Control LogColumn()
    {
        var clear = Button("Clear view", () => _logRows.Children.Clear());
        return new DockPanel
        {
            Children =
            {
                Top(new DockPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Children =
                    {
                        Right(clear),
                        Text("PYLON = emitted by Pylon (live from its pipe) · SENT = this bench → Pylon · FAKE = a request Pylon made to the fake", Muted, 11.5),
                    },
                }),
                new ScrollViewer { Content = _logRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            },
        };
    }

    string Repo => (_repo.Text ?? "").Trim() is { Length: > 0 } repo && repo.Contains('/') ? repo : "example-org/app";

    string Actor => (_actor.Text ?? "").Trim() is { Length: > 0 } actor ? actor : "dev-two";

    void OnSelected(Action<FakePullRequest> action)
    {
        if (_pulls.SelectedItem is not PullItem item || _state.Find(item.Repo, item.Number) is not { } pr)
        {
            _log.Add(BenchLog.Kind.Error, "Pick a pull request first.");
            return;
        }
        action(pr);
    }

    void OnFakeChanged(FakeWebhook hook)
    {
        Dispatcher.UIThread.Post(RefreshPulls);
        if (Dispatcher.UIThread.CheckAccess() ? _forward.IsChecked == true : Dispatcher.UIThread.Invoke(() => _forward.IsChecked == true))
            _ = Send(hook.Event, hook.Body, hook.Description);
    }

    void RefreshPulls()
    {
        var selected = (_pulls.SelectedItem as PullItem)?.Key;
        var items = _state.Pulls().OrderByDescending(p => p.UpdatedAt).Select(p => new PullItem(p)).ToList();
        _pulls.ItemsSource = items;
        _pulls.SelectedItem = items.FirstOrDefault(i => i.Key == selected) ?? items.FirstOrDefault();
    }

    async Task PlayLife()
    {
        var delay = TimeSpan.FromSeconds(double.TryParse(_delay.Text, out var seconds) ? Math.Clamp(seconds, 0, 120) : 3);
        var actor = Actor;
        var pr = _state.Open(Repo, "Walkthrough: " + (_title.Text ?? "a change"), _author.Text ?? "dev-one");
        var steps = new (string, Action)[]
        {
            ("request review", () => _state.RequestReview(pr, actor)),
            ("push", () => _state.Push(pr)),
            ("checks fail", () => _state.SetChecks(pr, "failing")),
            ("changes requested", () => _state.Review(pr, actor, "CHANGES_REQUESTED")),
            ("push a fix", () => _state.Push(pr)),
            ("checks pass", () => _state.SetChecks(pr, "passing")),
            ("approve", () => _state.Review(pr, actor, "APPROVED")),
            ("merge", () => _state.Merge(pr, actor)),
        };
        foreach (var (name, step) in steps)
        {
            await Task.Delay(delay);
            step();
        }
    }

    void LoadTemplate()
    {
        if (_template.SelectedItem is not string name)
            return;
        var (githubEvent, body) = Templates.Build(name, Repo, _author.Text ?? "dev-one", Actor);
        _githubEvent.Text = githubEvent ?? "";
        _payload.Text = body;
    }

    async Task SendPayload()
    {
        var body = _payload.Text ?? "";
        try
        {
            JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            _sendResult.Text = $"That isn't JSON: {ex.Message}";
            _sendResult.Foreground = Danger;
            return;
        }
        var githubEvent = (_githubEvent.Text ?? "").Trim();
        await Send(githubEvent.Length > 0 ? githubEvent : null, body, githubEvent.Length > 0 ? $"{githubEvent} from the editor" : "raw event from the editor");
    }

    async Task Send(string? githubEvent, string body, string description)
    {
        try
        {
            var result = githubEvent is null
                ? await _pylon.EmitAsync(body, _stop.Token)
                : await _pylon.IngestGitHubAsync(githubEvent, body, cancellationToken: _stop.Token);
            var sequences = result["sequences"]?.AsArray().Select(n => n?.ToString()).ToList() ?? [];
            var text = sequences.Count == 0 ? $"{description}: Pylon accepted it but it produced no event (an action Pylon doesn't report)." : $"{description} → event #{string.Join(", #", sequences)}";
            _log.Add(BenchLog.Kind.Sent, text);
            Dispatcher.UIThread.Post(() =>
            {
                _sendResult.Text = text;
                _sendResult.Foreground = sequences.Count == 0 ? Warning : Ok;
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or PylonUnavailableException)
        {
            _log.Add(BenchLog.Kind.Error, $"{description}: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                _sendResult.Text = ex.Message;
                _sendResult.Foreground = Danger;
            });
        }
    }

    async Task TailPylon(CancellationToken cancellationToken)
    {
        long? since = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var available = _pylon.IsAvailable;
            Dispatcher.UIThread.Post(() => SetPylonState(available));
            if (available)
            {
                try
                {
                    await foreach (var e in _pylon.SubscribeAsync(new(["**"]), since, cancellationToken))
                    {
                        since = e.Sequence;
                        var hint = e.Hint is { Count: > 0 } h ? " " + string.Join(' ', h.Select(p => $"{p.Key}={p.Value}")) : "";
                        _log.Add(BenchLog.Kind.Pylon, $"#{e.Sequence} {e.Kind.ToString().ToLowerInvariant()} {e.Type} {e.Topic}{hint}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or PylonUnavailableException or InvalidOperationException)
                {
                    Dispatcher.UIThread.Post(() => _pylonText.Text = $"Pylon: {ex.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    void SetPylonState(bool available)
    {
        _pylonDot.Fill = available ? Ok : Danger;
        _pylonText.Text = available ? $"Pylon is listening on {_pylon.Options.PipeName}" : $"Pylon isn't running on {_pylon.Options.PipeName}";
    }

    void Redraw()
    {
        if (!_logDirty)
            return;
        _logDirty = false;
        _logRows.Children.Clear();
        foreach (var entry in _log.Snapshot().Take(200))
        {
            var (tag, colour) = entry.Kind switch
            {
                BenchLog.Kind.Pylon => ("PYLON", Accent),
                BenchLog.Kind.Sent => ("SENT ", Ok),
                BenchLog.Kind.Fake => ("FAKE ", Muted),
                _ => ("ERROR", Danger),
            };
            _logRows.Children.Add(new SelectableTextBlock
            {
                Text = $"{entry.At:HH:mm:ss} {tag} {entry.Text}",
                FontFamily = Mono,
                FontSize = 11.5,
                Foreground = entry.Kind == BenchLog.Kind.Fake && entry.Text.StartsWith("304", StringComparison.Ordinal) ? Brush("#6B7580") : colour,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    void ShowSetup()
    {
        var text = $$"""
            Pylon (Dev) polls through Postern (Dev). Point a Postern connection at this fake, give it any token, and
            make a monitor that uses it. Nothing below touches real GitHub.

            1. Postern (Dev) → Connections → Edit as JSON, and add:
                 { "name": "github-fake", "baseUrl": "{{_address.GetLeftPart(UriPartial.Authority)}}" }
               (plain http is allowed for loopback addresses)

            2. Store a throwaway token for it (Debug postern-cli talks to Postern (Dev)):
                 'not-a-real-token' | postern-cli credential set github-fake

            3. Pylon (Dev) → Monitors → Add GitHub pull requests:
                 connection: github-fake      repositories: {{Repo}}
               and approve Pylon's scopes when Postern asks
               (GET github-fake/repos/{{Repo.Split('/')[0]}}/*/pulls/** and the checks scopes).

            4. Change a pull request on the left and watch the FAKE lines (Pylon polling, 304s in grey)
               and the PYLON lines (what it reported). "Poll now" on the Monitors page skips the wait.
            """;
        var window = new Window
        {
            Title = "Postern setup for polling the fake",
            Width = 760,
            SizeToContent = SizeToContent.Height,
            Background = Backdrop,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(18),
                Child = new SelectableTextBlock { Text = text, FontFamily = Mono, FontSize = 12.5, Foreground = Ink, TextWrapping = TextWrapping.Wrap },
            },
        };
        window.ShowDialog(this);
    }

    sealed record PullItem(string Repo, int Number, string Label)
    {
        public PullItem(FakePullRequest pr) : this(pr.Repo, pr.Number,
            $"{pr.Repo}#{pr.Number} · {pr.State}{(pr.Draft ? " (draft)" : "")} · checks {pr.Checks} · {pr.Reviews.Count} review(s){(pr.RequestedReviewers.Count > 0 ? " · waiting on " + string.Join(", ", pr.RequestedReviewers) : "")}\n{pr.Title}")
        {
        }

        public string Key => $"{Repo}#{Number}";

        public override string ToString() => Label;
    }

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    static TextBlock Text(string text, IBrush colour, double size = 13) => new() { Text = text, Foreground = colour, FontSize = size, TextWrapping = TextWrapping.Wrap };

    static Control Field(string label, Control value) => new StackPanel { Spacing = 3, Children = { Text(label.ToUpperInvariant(), Muted, 10.5), value } };

    static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 6) };
        if (primary)
        {
            button.Background = Accent;
            button.Foreground = Brush("#0B1F1C");
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    static Control Column(string title, Control body) => new Border
    {
        Background = Panel,
        BorderBrush = Edge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = new DockPanel
        {
            Children =
            {
                Top(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 8) }),
                body is StackPanel ? new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : body,
            },
        },
    };

    static Control At(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    static Control Bottom(Control control)
    {
        DockPanel.SetDock(control, Dock.Bottom);
        return control;
    }

    static Control Right(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    /// <summary>A small status dot.</summary>
    sealed class StatusDot : Avalonia.Controls.Shapes.Ellipse
    {
        public StatusDot()
        {
            Width = 10;
            Height = 10;
            Fill = Brush("#6B7580");
            VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
