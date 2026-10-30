using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Pylon.App;
using Pylon.App.Services;
using Pylon.App.Views;
using Pylon.Broker;
using Pylon.Broker.Monitors;
using Pylon.Core.Events;
using Pylon.Core.Monitors;
using Pylon.FakeGitHub;
using Pylon.Sources.GitHub;

// pylon-snapshots [outputDir]   (default ./captures)
// Every name, repo and number below is made up. The events come from the real GitHub monitor polling the fake GitHub,
// so summaries and payloads are what the app would really show.

var output = Path.GetFullPath(args.FirstOrDefault() ?? "captures");
Directory.CreateDirectory(output);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var dataDir = Path.Combine(Path.GetTempPath(), "pylon-snapshots-" + Guid.NewGuid().ToString("N")[..8]);
Pylon.App.Program.Args = AppArgs.Parse(["--data-dir", dataDir]);

var github = new FakeGitHubState();
var (fake, fakeAddress) = await FakeGitHubApi.StartAsync(github);
var runtime = PylonRuntime.Create(
    new PylonOptions { PipeName = "pylon-snapshots", DataDirectory = dataDir, PublishDiscovery = false, DevIngest = true },
    new PylonOverrides { MonitorHttp = _ => new HttpClient { BaseAddress = fakeAddress } });

MonitorDefinition work = new()
{
    Id = "work-prs", Name = "Work PRs", Source = "github", Connection = "github-work", Icon = Icons.PullRequest, Accent = "#60A5FA",
    Targets = ["example-org/api", "example-org/web"], Events = [.. GitHubEvents.All.Select(e => e.Name)], PollSeconds = 60,
    Options = new Dictionary<string, bool> { [GitHubEvents.CheckAssumptions] = true },
};
MonitorDefinition personal = new()
{
    Id = "side-projects", Name = "Side projects", Source = "github", Connection = "github", Icon = Icons.Rocket, Accent = "#A78BFA",
    Targets = ["example-user/tool"], Events = [GitHubEvents.Opened, GitHubEvents.Approved, GitHubEvents.Merged], PollSeconds = 120,
};
MonitorDefinition beat = new() { Id = "heartbeat", Name = "Heartbeat", Source = "heartbeat", Icon = Icons.Heartbeat, Accent = "#9AA4AE", PollSeconds = 300, Enabled = false };

await Generate();
runtime.Monitors.Save([work, personal, beat]);
await Task.Delay(1500); // let each monitor's first poll land so the Monitors page shows real states
Push();

using var session = HeadlessUnitTestSession.StartNew(typeof(SnapshotApp));
await session.Dispatch(() =>
{
    var records = runtime.Store.ReadBefore(null, 200);
    var approved = records.First(r => r.Type == "github.pr.approved" && r.Via == EventVia.Poll && r.MonitorId == work.Id);
    Save(new PayloadWindow(approved, runtime.Store.GetPayload(approved.Sequence), work), "payload-poll", height: 900);
    var webhook = records.First(r => r.Via == EventVia.Test);
    Save(new PayloadWindow(webhook, runtime.Store.GetPayload(webhook.Sequence), null), "payload-webhook", height: 900);

    Save(new MainWindow(runtime, new UiSettings(), () => { }), "monitors", height: 980);
    Save(new MainWindow(runtime, new UiSettings(), () => { }, MainWindow.GeneralKey), "general");

    // The poll that found the approval, expanded: it also carries the A1 warning from the quirk switched on above.
    var approvalPoll = runtime.Journal.PollFor(approved.Sequence);
    Detail(window => window.ShowMonitor(work.Id, approvalPoll), "monitor-activity");
    Detail(window => window.ShowMonitor(work.Id), "monitor-activity-top");
    Detail(window => window.ShowMonitor(work.Id, tab: MonitorDetail.Tab.Watching), "monitor-watching");
    Detail(window => window.ShowMonitor(work.Id, tab: MonitorDetail.Tab.Budget), "monitor-budget");
    Save(new MonitorEditor(new GitHubSource(), work, [work, personal, beat], testBench: true), "monitor-editor");

    var log = new Pylon.TestBench.BenchLog();
    log.Add(Pylon.TestBench.BenchLog.Kind.Fake, "200 GET /repos/example-org/app/pulls?state=all&sort=updated&direction=desc&per_page=50");
    log.Add(Pylon.TestBench.BenchLog.Kind.Fake, "304 GET /repos/example-org/app/commits/3f2a/check-runs?per_page=100");
    log.Add(Pylon.TestBench.BenchLog.Kind.Sent, "pull_request_review from the editor → event #42");
    log.Add(Pylon.TestBench.BenchLog.Kind.Pylon, "#42 changed github.pr.approved github/pr/example-org/app/101 state=approved");
    Save(new Pylon.TestBench.BenchWindow(github, log, fakeAddress, "pylon-snapshots"), "testbench");

    var compact = new UiSettings { StreamMode = StreamMode.Compact, CompactWidth = 420, CompactHeight = 300 };
    Save(new StreamWindow(runtime, compact), "stream-compact");

    var expanded = new UiSettings { StreamMode = StreamMode.Expanded, ExpandedWidth = 480, ExpandedHeight = 680 };
    Save(new StreamWindow(runtime, expanded), "stream-expanded");

    var filtered = new UiSettings { StreamMode = StreamMode.Expanded, ExpandedWidth = 480, ExpandedHeight = 420, FilterText = "web", FilterStatuses = [EventStatus.Failure] };
    Save(new StreamWindow(runtime, filtered), "stream-filtered");
}, CancellationToken.None);

Console.WriteLine($"Wrote snapshots to {output}");
Environment.Exit(0); // the headless session's dispatcher thread doesn't wind down on its own
return;

async Task Generate()
{
    var api = github.Open("example-org/api", "Add rate-limit headers to the export endpoint", "dev-one");
    var web = github.Open("example-org/web", "Dark mode for the settings page", "dev-two");
    var tool = github.Open("example-user/tool", "Speed up config loading", "example-user");
    var workMonitor = new GitHubMonitor(new MonitorContext(work, new HttpClient { BaseAddress = fakeAddress }, runtime.Store.StateFor(work.Id), TimeProvider.System));
    var toolMonitor = new GitHubMonitor(new MonitorContext(personal, new HttpClient { BaseAddress = fakeAddress }, runtime.Store.StateFor(personal.Id), TimeProvider.System));
    var monitors = new[] { (work, workMonitor), (personal, toolMonitor) };

    // What MonitorHost does after each poll, so the detail page has a history to show.
    async Task Poll()
    {
        foreach (var (definition, monitor) in monitors)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await monitor.PollAsync(CancellationToken.None);
            var published = runtime.Hub.Publish(result.Events);
            runtime.Journal.Add(new PollRecord
            {
                MonitorId = definition.Id,
                StartedAt = started,
                Milliseconds = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                Requests = result.Requests,
                NotModified = result.NotModified,
                Calls = result.Calls,
                Observations = result.Observations,
                Notes = result.Notes,
                Sequences = [.. published.Select(r => r.Sequence)],
            });
        }
    }

    await Poll();

    var fresh = github.Open("example-org/web", "Fix flaky login test", "dev-three");
    await Poll();
    github.RequestReview(api, "dev-two");
    github.RequestReview(fresh, "team:frontend");
    await Poll();
    github.Push(web);
    await Poll();
    github.SetChecks(web, "failing");
    github.Review(api, "dev-two", "CHANGES_REQUESTED");
    await Poll();
    github.Push(api);
    github.SetChecks(api, "passing");
    await Poll();
    github.ReviewsBumpUpdatedAt = false;
    github.Review(api, "dev-two", "APPROVED");
    github.Review(tool, "example-friend", "APPROVED");
    github.ReviewsBumpUpdatedAt = true;
    await Poll();
    await Poll();
    github.Merge(api, "dev-one");
    github.Merge(tool, "example-user");
    await Poll();

    runtime.Hub.Publish([EventDraft.Resync("github", "recovered", work.Id)]);
}

// After the monitors are saved, so webhooks have monitors to go to (one of them to a repository none watches).
void Push()
{
    var fresh = github.Pulls("example-org/web").First(p => p.Title == "Fix flaky login test");
    github.Changed += hook => runtime.Monitors.Receive(GitHubSource.SourceName, hook.Event, hook.Body, EventVia.Test);
    github.Review(fresh, "dev-one", "COMMENTED");
    github.SetChecks(fresh, "pending");
    github.Open("example-org/docs", "Fix a typo in the README", "dev-four");
}

void Detail(Action<MainWindow> open, string name)
{
    var window = new MainWindow(runtime, new UiSettings(), () => { });
    open(window);
    Save(window, name, height: 1100);
}

void Save(Window window, string name, double? height = null)
{
    if (height is { } h)
        window.Height = h;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"No frame for {name}");
    var path = Path.Combine(output, $"{stamp}-ui-{name}.png");
    frame.Save(path);
    Console.WriteLine(path);
    window.Close();
}

sealed class SnapshotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SnapshotApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
