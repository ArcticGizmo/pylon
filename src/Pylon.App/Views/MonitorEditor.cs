using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Pylon.Broker.Monitors;
using Pylon.Core.Monitors;
using Pylon.Sources.GitHub;

namespace Pylon.App.Views;

/// <summary>
/// Add or edit one monitor. The fields follow the source: GitHub gets a connection, repositories, the changes to report
/// and its two switches; the heartbeat only needs a name and an interval. Problems are shown, never thrown at the user.
/// </summary>
internal sealed class MonitorEditor : Window
{
    static readonly string[] Swatches = ["#4FB8A8", "#60A5FA", "#A78BFA", "#F472B6", "#F5A524", "#4ADE80", "#EF5350", "#9AA4AE"];

    readonly ISource _source;
    readonly MonitorDefinition? _original;
    readonly IReadOnlyList<MonitorDefinition> _others;
    readonly TextBox _name = new();
    readonly TextBox _connection = new() { PlaceholderText = "github" };
    readonly TextBox _targets = new() { AcceptsReturn = true, Height = 96, PlaceholderText = "owner/repo, one per line", FontFamily = Ui.Mono };
    readonly TextBox _poll = new() { Width = 90 };
    readonly CheckBox _enabled = new() { Content = "Enabled" };
    readonly Dictionary<string, CheckBox> _events = [];
    readonly Dictionary<string, CheckBox> _options = [];
    readonly ComboBox _icon = new() { MinWidth = 160 };
    readonly TextBox _accent = new() { Width = 100, FontFamily = Ui.Mono };
    readonly Border _accentPreview = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(4) };
    readonly StackPanel _problems = new() { Spacing = 2 };

    /// <param name="testBench">Whether this run has the <c>testbench</c> connection (dev builds), which gets a shortcut.</param>
    public MonitorEditor(ISource source, MonitorDefinition? original, IReadOnlyList<MonitorDefinition> others, bool testBench = false)
    {
        _source = source;
        _original = original;
        _others = others;
        Title = original is null ? $"Pylon: new {source.DisplayName} monitor" : $"Pylon: edit {original.Name}";
        Icon = TrayArt.WindowIcon();
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        CanResize = false;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var current = original ?? Defaults(source);
        _name.Text = current.Name;
        _connection.Text = current.Connection;
        _targets.Text = string.Join(Environment.NewLine, current.Targets);
        _poll.Text = current.PollSeconds.ToString(CultureInfo.InvariantCulture);
        _enabled.IsChecked = current.Enabled;
        _icon.ItemsSource = Icons.MonitorIcons;
        _icon.SelectedItem = Icons.MonitorIcons.Contains(current.Icon) ? current.Icon : source.Icon;
        _accent.Text = current.Accent;
        _accent.TextChanged += (_, _) => _accentPreview.Background = Ui.AccentFor(_accent.Text);
        _accentPreview.Background = Ui.AccentFor(current.Accent);

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(Ui.Field("Name", _name));

        if (source.UsesPostern)
        {
            var connection = new StackPanel
            {
                Spacing = 4,
                Children = { _connection, Ui.Text("The connection name in Postern (github, github-work, …). Pylon asks Postern for read access to each owner's pull requests.", Ui.Muted, 11.5) },
            };
            if (testBench)
            {
                var useBench = Ui.Button("Use the test bench", () =>
                {
                    _connection.Text = MonitorHost.TestBenchConnection;
                    if (string.IsNullOrWhiteSpace(_targets.Text))
                        _targets.Text = "example-org/app";
                });
                useBench.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(useBench, Dock.Left);
                connection.Children.Add(new DockPanel
                {
                    Children =
                    {
                        useBench,
                        Ui.Text($"Dev builds: '{MonitorHost.TestBenchConnection}' polls the test bench's fake GitHub directly, with no Postern setup.", Ui.Muted, 11.5),
                    },
                });
            }
            form.Children.Add(Ui.Field("Connection", connection));
            form.Children.Add(Ui.Field("Repositories", _targets));
        }

        if (source.Events.Count > 1)
        {
            var events = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var e in source.Events)
            {
                var box = new CheckBox { Content = e.Label, IsChecked = current.Events.Contains(e.Name), Margin = new Thickness(0, 0, 14, 0), MinWidth = 150 };
                ToolTip.SetTip(box, e.Description);
                _events[e.Name] = box;
                events.Children.Add(box);
            }
            form.Children.Add(Ui.Field("Report", events));
        }

        if (source.Name == GitHubSource.SourceName)
        {
            var options = new StackPanel { Spacing = 2 };
            foreach (var (name, label, fallback) in new[]
            {
                (GitHubEvents.IncludeDrafts, "Include drafts (otherwise drafts are quiet until marked ready)", false),
                (GitHubEvents.IgnoreBots, "Ignore bots (PRs opened by bots, and reviews by bots)", true),
                (GitHubEvents.CheckAssumptions, "Check assumptions (extra requests; flags anything the poll strategy would miss)", false),
            })
            {
                var box = new CheckBox { Content = label, IsChecked = current.Option(name, fallback) };
                if (name == GitHubEvents.CheckAssumptions)
                    ToolTip.SetTip(box, "Fetches reviews for every open PR on every poll, and fetches the pulls list again whenever it answers 304, then notes in the monitor's Activity wherever the normal strategy would have missed a change. For measuring (S1), not for every day: the refetch costs one request per repository per poll.");
                _options[name] = box;
                options.Children.Add(box);
            }
            form.Children.Add(Ui.Field("Options", options));
        }

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var swatch in Swatches)
        {
            var button = new Button { Width = 20, Height = 20, Padding = new Thickness(0), Background = Ui.Brush(swatch), CornerRadius = new CornerRadius(4) };
            button.Click += (_, _) => _accent.Text = swatch;
            swatches.Children.Add(button);
        }
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Children =
            {
                Ui.Field("Poll every (seconds)", _poll),
                Ui.Field("Icon", _icon),
                Ui.Field("Colour", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _accentPreview, _accent, swatches } }),
            },
        });
        form.Children.Add(_enabled);
        form.Children.Add(_problems);

        var save = Ui.Button("Save", Save, primary: true);
        save.IsDefault = true;
        var cancel = Ui.Button("Cancel", Close);
        cancel.IsCancel = true;
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save },
        });

        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(20), Child = form } };
    }

    /// <summary>Set when Save succeeds; the page saves the whole list.</summary>
    public MonitorDefinition? Result { get; private set; }

    static MonitorDefinition Defaults(ISource source) => new()
    {
        Id = "",
        Name = source.Name == GitHubSource.SourceName ? "GitHub pull requests" : source.DisplayName,
        Source = source.Name,
        Connection = source.UsesPostern ? "github" : "",
        Icon = source.Icon,
        Events = [.. source.Events.Select(e => e.Name)],
        PollSeconds = source.Name == GitHubSource.SourceName ? 60 : 30,
    };

    void Save()
    {
        _problems.Children.Clear();
        var problems = new List<string>();
        if (!int.TryParse(_poll.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 10)
            problems.Add("Poll at least every 10 seconds.");
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0)
            problems.Add("Give it a name.");

        var definition = new MonitorDefinition
        {
            Id = _original?.Id ?? UniqueId(name),
            Name = name,
            Source = _source.Name,
            Connection = (_connection.Text ?? "").Trim(),
            Icon = _icon.SelectedItem as string ?? _source.Icon,
            Accent = (_accent.Text ?? "").Trim(),
            Targets = [.. (_targets.Text ?? "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)],
            Events = _events.Count == 0 ? [.. _source.Events.Select(e => e.Name)] : [.. _events.Where(e => e.Value.IsChecked == true).Select(e => e.Key)],
            Options = _options.ToDictionary(o => o.Key, o => o.Value.IsChecked == true),
            PollSeconds = Math.Max(seconds, 10),
            Enabled = _enabled.IsChecked == true,
        };
        if (!Color.TryParse(definition.Accent, out _))
            problems.Add($"'{definition.Accent}' isn't a colour; use #rrggbb.");
        problems.AddRange(_source.Validate(definition));

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                _problems.Children.Add(Ui.Text("• " + problem, Ui.Warning, 12));
            return;
        }
        Result = definition;
        Close();
    }

    string UniqueId(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Length == 0 ? _source.Name : slug[..Math.Min(slug.Length, 40)];
        var id = slug;
        for (var n = 2; _others.Any(o => o.Id == id); n++)
            id = $"{slug}-{n}";
        return id;
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
}
