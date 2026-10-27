using Microsoft.AspNetCore.Builder;
using Pylon.Broker.Storage;
using Pylon.Core.Monitors;
using Pylon.FakeGitHub;
using Pylon.Sources.GitHub;

namespace Pylon.Tests;

/// <summary>The poll strategy end to end against the fake GitHub on a loopback port (no Postern, no real GitHub).</summary>
public class GitHubMonitorTests : IAsyncLifetime
{
    readonly TempDirectory _dir = new();
    readonly FakeGitHubState _github = new();
    readonly List<FakeRequest> _requests = [];
    WebApplication? _app;
    HttpClient? _http;
    EventStore? _store;

    public async Task InitializeAsync()
    {
        var (app, address) = await FakeGitHubApi.StartAsync(_github, log: r =>
        {
            lock (_requests)
                _requests.Add(r);
        });
        _app = app;
        _http = new HttpClient { BaseAddress = address };
        _store = new EventStore(Path.Combine(_dir.Path, "pylon.db"), TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
        _dir.Dispose();
    }

    GitHubMonitor Monitor(IReadOnlyList<string>? events = null, bool drafts = false) => new(new MonitorContext(
        new MonitorDefinition
        {
            Id = "work",
            Name = "Work",
            Source = "github",
            Connection = "github-fake",
            Targets = ["example/app"],
            Events = events ?? [.. GitHubEvents.All.Select(e => e.Name)],
            Options = new Dictionary<string, bool> { [GitHubEvents.IncludeDrafts] = drafts },
        },
        _http!, _store!.StateFor("work"), TimeProvider.System));

    [Fact]
    public async Task BaselineThenEveryEventType()
    {
        var existing = _github.Open("example/app", "Existing work", "author-a");
        var monitor = Monitor();

        var baseline = await monitor.PollAsync(CancellationToken.None);
        Assert.Null(baseline.Error);
        Assert.Empty(baseline.Events);

        Assert.Empty((await monitor.PollAsync(CancellationToken.None)).Events);

        var pr = _github.Open("example/app", "New feature", "author-b");
        Assert.Equal(["github.pr.opened"], await Types(monitor));

        _github.RequestReview(pr, "reviewer-c");
        Assert.Equal(["github.pr.review_requested"], await Types(monitor));

        _github.Review(pr, "reviewer-c", "CHANGES_REQUESTED");
        Assert.Equal(["github.pr.reviewed"], await Types(monitor));

        _github.Review(pr, "reviewer-c", "APPROVED");
        Assert.Equal(["github.pr.approved"], await Types(monitor));

        _github.Push(pr);
        Assert.Equal(["github.pr.checks"], await Types(monitor));

        _github.SetChecks(pr, "failing");
        Assert.Equal(["github.pr.checks"], await Types(monitor));

        _github.SetChecks(pr, "passing");
        Assert.Equal(["github.pr.checks"], await Types(monitor));

        _github.Merge(pr, "reviewer-c");
        Assert.Equal(["github.pr.merged"], await Types(monitor));

        _github.Close(existing, "author-a");
        Assert.Equal(["github.pr.closed"], await Types(monitor));

        Assert.Empty((await monitor.PollAsync(CancellationToken.None)).Events);
    }

    [Fact]
    public async Task QuietPollsAreAll304s()
    {
        var pr = _github.Open("example/app", "Feature", "author-a");
        _github.SetChecks(pr, "passing");
        var monitor = Monitor();
        await monitor.PollAsync(CancellationToken.None);

        var quiet = await monitor.PollAsync(CancellationToken.None);
        Assert.True(quiet.Requests > 0);
        Assert.Equal(quiet.Requests, quiet.NotModified);
    }

    [Fact]
    public async Task ReviewsAreOnlyFetchedWhenThePrChanged()
    {
        _github.Open("example/app", "Feature", "author-a");
        var monitor = Monitor(events: [GitHubEvents.Approved]);
        await monitor.PollAsync(CancellationToken.None);
        lock (_requests)
            _requests.Clear();

        await monitor.PollAsync(CancellationToken.None);
        lock (_requests)
            Assert.DoesNotContain(_requests, r => r.Path.Contains("/reviews"));
    }

    [Fact]
    public async Task PayloadShowsBeforeAndAfter()
    {
        var monitor = Monitor();
        await monitor.PollAsync(CancellationToken.None);
        _github.Open("example/app", "Feature", "author-a");
        var e = Assert.Single((await monitor.PollAsync(CancellationToken.None)).Events);
        Assert.Contains("\"after\"", e.Payload!.Content);
        Assert.Contains("\"requests\"", e.Payload.Content);
        Assert.Equal("work", e.MonitorId);
    }

    [Fact]
    public async Task StateSurvivesANewRunner()
    {
        await Monitor().PollAsync(CancellationToken.None);
        _github.Open("example/app", "Feature", "author-a");
        Assert.Single((await Monitor().PollAsync(CancellationToken.None)).Events);
        Assert.Empty((await Monitor().PollAsync(CancellationToken.None)).Events);
    }

    [Fact]
    public async Task MissingRepoIsAnErrorNotACrash()
    {
        var monitor = new GitHubMonitor(new MonitorContext(
            new MonitorDefinition { Id = "x", Name = "X", Source = "github", Connection = "c", Targets = ["example/app"], Events = ["opened"] },
            new HttpClient { BaseAddress = new Uri(_http!.BaseAddress!, "nowhere/") }, _store!.StateFor("x"), TimeProvider.System));
        var result = await monitor.PollAsync(CancellationToken.None);
        Assert.NotNull(result.Error);
    }

    async Task<string[]> Types(GitHubMonitor monitor)
    {
        var result = await monitor.PollAsync(CancellationToken.None);
        Assert.Null(result.Error);
        return [.. result.Events.Select(e => e.Type)];
    }
}
