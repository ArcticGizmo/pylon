using Microsoft.AspNetCore.Builder;
using Pylon.Broker;
using Pylon.Broker.Events;
using Pylon.Broker.Monitors;
using Pylon.Broker.Storage;
using Pylon.Client;
using Pylon.Core.Events;
using Pylon.Core.Monitors;
using Pylon.Core.Topics;
using Pylon.FakeGitHub;
using Pylon.Sources.GitHub;

namespace Pylon.Tests;

/// <summary>The broker on a throwaway pipe, through the real client. Monitors call the fake GitHub directly (no Postern).</summary>
public class BrokerTests : IAsyncLifetime
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    readonly TempDirectory _dir = new();
    readonly string _pipe = "pylon-test-" + Guid.NewGuid().ToString("N")[..8];
    readonly FakeGitHubState _github = new();
    WebApplication? _fake;
    Uri? _fakeAddress;
    PylonRuntime? _runtime;
    PylonClient? _client;

    public async Task InitializeAsync()
    {
        (_fake, _fakeAddress) = await FakeGitHubApi.StartAsync(_github);
        _client = new PylonClient(new PylonClientOptions { PipeName = _pipe, SkipServerVerification = true });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_runtime is not null)
            await _runtime.DisposeAsync();
        if (_fake is not null)
            await _fake.DisposeAsync();
        _dir.Dispose();
    }

    /// <param name="testBench">Use the real connection logic with the fake as the test bench, instead of bypassing it.
    /// Postern then points at a pipe nobody listens on, so no real Postern is ever asked anything.</param>
    async Task<PylonRuntime> Start(bool devIngest = true, Retention? retention = null, bool testBench = false)
    {
        _runtime = PylonRuntime.Create(
            new PylonOptions
            {
                PipeName = _pipe,
                DataDirectory = _dir.Path,
                DevIngest = devIngest,
                PublishDiscovery = false,
                Retention = retention ?? new Retention(),
                TestBench = testBench ? _fakeAddress : null,
                PosternPipeName = "postern-test-" + Guid.NewGuid().ToString("N")[..8],
            },
            new PylonOverrides { MonitorHttp = testBench ? null : _ => new HttpClient { BaseAddress = _fakeAddress } });
        await _runtime.StartAsync();
        return _runtime;
    }

    static MonitorDefinition Watching(string id, string connection = "github-fake", IReadOnlyList<string>? events = null) => new()
    {
        Id = id, Name = id, Source = GitHubSource.SourceName, Connection = connection,
        Targets = ["example/app"], Events = events ?? [.. GitHubEvents.All.Select(e => e.Name)], PollSeconds = 600,
    };

    /// <summary>Reads events in the background; returns once Pylon has registered the subscription.</summary>
    async Task<Reader> Subscribe(SubscriptionRequest request, long? since = null)
    {
        var before = _runtime!.Hub.SubscriberCount;
        var reader = new Reader(_client!, request, since);
        var deadline = DateTime.UtcNow + Wait;
        while (_runtime.Hub.SubscriberCount == before)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The subscription never arrived.");
            await Task.Delay(20);
        }
        return reader;
    }

    [Fact]
    public async Task LiveEventsArriveInOrder()
    {
        var runtime = await Start();
        using var reader = await Subscribe(new(["test/**"]));
        runtime.Hub.Publish([Drafts.Make("test/1"), Drafts.Make("test/2"), Drafts.Make("elsewhere/3"), Drafts.Make("test/4")]);
        var events = await reader.Take(3);
        Assert.Equal(["test/1", "test/2", "test/4"], events.Select(e => e.Topic));
        Assert.Equal([1L, 2L, 4L], events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task ReplaysFromSinceThenGoesLive()
    {
        var runtime = await Start();
        runtime.Hub.Publish([Drafts.Make("test/1"), Drafts.Make("test/2"), Drafts.Make("test/3")]);
        using var reader = await Subscribe(new(["**"]), since: 1);
        runtime.Hub.Publish([Drafts.Make("test/4")]);
        Assert.Equal([2L, 3L, 4L], (await reader.Take(3)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task PrunedGapGetsAResync()
    {
        var runtime = await Start(retention: new Retention { MaxEvents = 2 });
        runtime.Hub.Publish([.. Enumerable.Range(1, 5).Select(i => Drafts.Make($"test/{i}"))]);
        runtime.Store.Prune();
        using var reader = await Subscribe(new(["test/**"]), since: 1);
        var resync = Assert.Single(await reader.Take(1));
        Assert.Equal(EventKind.Resync, resync.Kind);
        Assert.Equal("replay-gap", resync.Hint!["reason"]);
        Assert.Equal(5, resync.Sequence);
    }

    [Fact]
    public async Task SequenceFromAnotherLogGetsAResync()
    {
        await Start();
        using var reader = await Subscribe(new(["**"]), since: 999);
        Assert.Equal("unknown-sequence", Assert.Single(await reader.Take(1)).Hint!["reason"]);
    }

    [Fact]
    public async Task TypeFilterAndWebhookIngest()
    {
        var runtime = await Start();
        runtime.Monitors.Save([Watching("work")]);
        using var reader = await Subscribe(new(["github/pr/example/*/*"], ["github.pr.approved"]));
        _github.Changed += w => _client!.IngestGitHubAsync(w.Event, w.Body).GetAwaiter().GetResult();
        var pr = _github.Open("example/app", "Feature", "author-a");
        _github.Review(pr, "reviewer-b", "APPROVED");

        var e = Assert.Single(await reader.Take(1));
        Assert.Equal("github.pr.approved", e.Type);
        Assert.Equal("work", e.MonitorId);
        Assert.Equal(EventVia.Test, _runtime!.Store.ReadAfter(e.Sequence - 1, 1)[0].Via);
        Assert.True(_runtime.Store.ReadAfter(e.Sequence - 1, 1)[0].HasPayload);
    }

    [Fact]
    public async Task AWebhookGoesToTheMonitorsWatchingItsRepoWithTheirSwitches()
    {
        var runtime = await Start();
        runtime.Monitors.Save([Watching("all"), Watching("merges", events: [GitHubEvents.Merged])]);
        var pr = _github.Open("example/app", "Feature", "author-a");
        FakeWebhook? approval = null;
        _github.Changed += w => approval = w;
        _github.Review(pr, "reviewer-b", "APPROVED");

        var result = await _client!.IngestGitHubAsync(approval!.Event, approval.Body);
        Assert.Equal(["all", "merges"], result["monitors"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.Single(result["sequences"]!.AsArray());

        var quiet = runtime.Journal.Recent("merges", null, 10).Single(p => p.Kind == PollKind.Received);
        Assert.Contains("pull_request_review (submitted)", quiet.Received);
        Assert.Contains("switched off", Assert.Single(Assert.Single(quiet.Observations).Quiet).Reason);
        Assert.NotNull(runtime.Journal.Body(quiet.Id, 0));
        Assert.Single(runtime.Journal.Recent("all", null, 10), p => p.Kind == PollKind.Received && p.Sequences.Count == 1);
    }

    [Fact]
    public async Task EveryDeliveryIsJournalled()
    {
        var runtime = await Start();
        runtime.Monitors.Save([Watching("work")]);
        var pr = _github.Open("example/app", "Feature", "author-a");
        _github.Changed += w => runtime.Monitors.Receive(GitHubSource.SourceName, w.Event, w.Body, EventVia.Test);
        _github.Review(pr, "reviewer-b", "COMMENTED");
        _github.SetChecks(pr, "pending");

        var received = runtime.Journal.Recent("work", null, 10).Where(p => p.Kind == PollKind.Received).ToList();
        Assert.Equal(["check_run (created) for example/app", "pull_request_review (submitted) for example/app"], received.Select(p => p.Received));
        Assert.All(received, p => Assert.Single(p.Sequences));
    }

    [Fact]
    public async Task AWebhookNoMonitorWatchesIsJournalledAsUnmatched()
    {
        var runtime = await Start();
        FakeWebhook? opened = null;
        _github.Changed += w => opened = w;
        _github.Open("example/elsewhere", "Feature", "author-a");

        var result = await _client!.IngestGitHubAsync(opened!.Event, opened.Body);
        Assert.Empty(result["sequences"]!.AsArray());
        Assert.Empty(result["monitors"]!.AsArray());
        Assert.Contains("example/elsewhere", Assert.Single(runtime.Journal.Recent(PollRecord.Unmatched, null, 10)).Received);
    }

    [Fact]
    public async Task APollDoesNotReportWhatAWebhookAlreadyDid()
    {
        var runtime = await Start();
        runtime.Monitors.Save([Watching("work")]);
        await Until(() => runtime.Journal.Recent("work", null, 10).Count == 1);
        FakeWebhook? opened = null;
        _github.Changed += w => opened = w;
        _github.Open("example/app", "Feature", "author-a");
        var pushed = (await _client!.IngestGitHubAsync(opened!.Event, opened.Body))["sequences"]![0]!.GetValue<long>();

        runtime.Monitors.PollNow("work");
        await Until(() => runtime.Journal.Recent("work", null, 10).Count(p => p.Kind == PollKind.Poll) == 2);
        var poll = runtime.Journal.Recent("work", null, 10).First(p => p.Kind == PollKind.Poll);
        Assert.Empty(poll.Sequences);
        var quiet = Assert.Single(Assert.Single(poll.Observations).Quiet);
        Assert.Equal($"already received from a webhook as #{pushed}", quiet.Reason);
    }

    [Fact]
    public async Task TheTestBenchConnectionPollsWithoutPostern()
    {
        var runtime = await Start(testBench: true);
        using var reader = await Subscribe(new(["github/**"]));
        runtime.Monitors.Save([Watching("bench", MonitorHost.TestBenchConnection)]);
        await Until(() => runtime.Monitors.Status().Single().State == MonitorState.Idle);
        Assert.Empty(runtime.Monitors.WantedScopes());

        _github.Open("example/app", "Feature", "author-a");
        runtime.Monitors.PollNow("bench");
        Assert.Equal("github.pr.opened", Assert.Single(await reader.Take(1)).Type);
    }

    [Fact]
    public async Task TheTestBenchConnectionIsInvalidWithoutATestBench()
    {
        // Not Start(): bypassing Postern for tests also bypasses the connection logic under test.
        _runtime = PylonRuntime.Create(
            new PylonOptions { PipeName = _pipe, DataDirectory = _dir.Path, PublishDiscovery = false, PosternPipeName = "postern-test-" + Guid.NewGuid().ToString("N")[..8] });
        await _runtime.StartAsync();
        _runtime.Monitors.Save([Watching("bench", MonitorHost.TestBenchConnection)]);
        await Until(() => _runtime.Monitors.Status().Single().State == MonitorState.Invalid);
        Assert.Contains("dev builds", _runtime.Monitors.Status().Single().Detail);
    }

    [Fact]
    public async Task DevIngestIsOffUnlessAskedFor()
    {
        await Start(devIngest: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _client!.EmitAsync("""{"source":"x","topic":"x/y","type":"x.y"}"""));
        Assert.Contains("dev ingest is off", ex.Message);
    }

    [Fact]
    public async Task RawEmitWritesToTheStream()
    {
        var runtime = await Start();
        var result = await _client!.EmitAsync("""{"source":"test","topic":"test/raw","type":"test.raw","status":"failure","summary":"Raw one","payload":{"a":1}}""");
        Assert.Equal(1, result["sequences"]![0]!.GetValue<long>());
        var record = runtime.Store.ReadAfter(0, 1)[0];
        Assert.Equal(EventStatus.Failure, record.Status);
        Assert.Contains("\"a\": 1", runtime.Store.GetPayload(1)!.Content);
    }

    [Fact]
    public async Task StatusDescribesMonitors()
    {
        var runtime = await Start();
        runtime.Monitors.Save([new MonitorDefinition { Id = "beat", Name = "Beat", Source = HeartbeatSource.SourceName, PollSeconds = 60 }]);
        var status = await _client!.GetStatusAsync();
        Assert.Equal(_pipe, status["pipe"]!.GetValue<string>());
        Assert.Equal("beat", status["monitors"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task HeartbeatMonitorPublishesThroughTheHost()
    {
        var runtime = await Start();
        using var reader = await Subscribe(new(["heartbeat/**"]));
        runtime.Monitors.Save([new MonitorDefinition { Id = "beat", Name = "Beat", Source = HeartbeatSource.SourceName, PollSeconds = 60 }]);
        var e = Assert.Single(await reader.Take(1));
        Assert.Equal("heartbeat.tick", e.Type);
        Assert.Equal("beat", e.MonitorId);
    }

    [Fact]
    public async Task GitHubMonitorFindsChangesOnTheFake()
    {
        var runtime = await Start();
        using var reader = await Subscribe(new(["github/**"]));
        runtime.Monitors.Save([new MonitorDefinition
        {
            Id = "work", Name = "Work", Source = GitHubSource.SourceName, Connection = "github-fake",
            Targets = ["example/app"], Events = [GitHubEvents.Opened, GitHubEvents.Approved], PollSeconds = 300,
        }]);
        await Until(() => runtime.Monitors.Status().Single().State == MonitorState.Idle);

        var pr = _github.Open("example/app", "Feature", "author-a");
        runtime.Monitors.PollNow("work");
        Assert.Equal("github.pr.opened", Assert.Single(await reader.Take(1)).Type);

        _github.Review(pr, "reviewer-b", "APPROVED");
        runtime.Monitors.PollNow("work");
        Assert.Equal("github.pr.approved", Assert.Single(await reader.Take(1)).Type);
    }

    [Fact]
    public async Task EveryPollIsJournalledWithItsEvents()
    {
        var runtime = await Start();
        var polled = new List<PollRecord>();
        runtime.Monitors.Polled += p =>
        {
            lock (polled)
                polled.Add(p);
        };
        using var reader = await Subscribe(new(["github/**"]));
        runtime.Monitors.Save([new MonitorDefinition
        {
            Id = "work", Name = "Work", Source = GitHubSource.SourceName, Connection = "github-fake",
            Targets = ["example/app"], Events = [GitHubEvents.Opened], PollSeconds = 300,
        }]);
        await Until(() => runtime.Journal.Recent("work", null, 10).Count == 1);

        _github.Open("example/app", "Feature", "author-a");
        runtime.Monitors.PollNow("work");
        var e = Assert.Single(await reader.Take(1));
        await Until(() => runtime.Journal.Recent("work", null, 10).Count == 2);

        var polls = runtime.Journal.Recent("work", null, 10);
        Assert.Contains("Baseline", Assert.Single(polls[1].Notes).Text);
        Assert.Equal([e.Sequence], polls[0].Sequences);
        Assert.Equal(polls[0].Id, runtime.Journal.PollFor(e.Sequence));
        Assert.Equal("example/app#101", Assert.Single(polls[0].Observations).Resource);
        await Until(() =>
        {
            lock (polled)
                return polled.Select(p => p.Id).Order().SequenceEqual(polls.Select(p => p.Id).Order());
        });
        Assert.Single(runtime.Monitors.Watched("work"));
    }

    [Fact]
    public async Task InvalidMonitorsSayWhy()
    {
        var runtime = await Start();
        runtime.Monitors.Save([new MonitorDefinition { Id = "bad", Name = "Bad", Source = GitHubSource.SourceName, Connection = "github" }]);
        await Until(() => runtime.Monitors.Status().Single().State == MonitorState.Invalid);
        Assert.Contains("repository", runtime.Monitors.Status().Single().Detail);
        Assert.Throws<ArgumentException>(() => runtime.Monitors.Save([new MonitorDefinition { Id = "Bad Id", Name = "x", Source = "heartbeat" }]));
    }

    [Fact]
    public async Task RunAsyncRefreshesOnConnectThenDeliversEvents()
    {
        var runtime = await Start();
        var resyncs = new List<string>();
        var received = new TaskCompletionSource<PylonEvent>();
        using var stop = new CancellationTokenSource(Wait);
        var run = _client!.RunAsync(new(["test/**"]),
            (e, _) => { received.TrySetResult(e); return Task.CompletedTask; },
            (reason, _) => { resyncs.Add(reason); return Task.CompletedTask; },
            stop.Token);
        await Until(() => runtime.Hub.SubscriberCount == 1);
        runtime.Hub.Publish([Drafts.Make("test/x")]);
        Assert.Equal("test/x", (await received.Task.WaitAsync(Wait)).Topic);
        Assert.Equal(["connect"], resyncs);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task StopEndsOpenStreams()
    {
        var runtime = await Start();
        using var reader = await Subscribe(new(["**"]));
        await runtime.StopAsync().WaitAsync(Wait);
        await reader.Completion.WaitAsync(Wait);
    }

    [Fact]
    public void SlowSubscriberOverflowsAlone()
    {
        using var dir = new TempDirectory();
        var hub = new EventHub(new EventStore(Path.Combine(dir.Path, "pylon.db"), TimeProvider.System));
        var (slow, _) = hub.Subscribe(EventFilter.All, capacity: 2);
        var (fast, _) = hub.Subscribe(EventFilter.All, capacity: 100);
        hub.Publish([Drafts.Make(), Drafts.Make(), Drafts.Make()]);
        Assert.True(slow.Overflowed);
        Assert.False(fast.Overflowed);
        Assert.Equal(3, fast.Reader.Count);
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition never became true.");
            await Task.Delay(20);
        }
    }

    sealed class Reader : IDisposable
    {
        readonly CancellationTokenSource _stop = new();
        readonly System.Threading.Channels.Channel<PylonEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<PylonEvent>();

        public Reader(PylonClient client, SubscriptionRequest request, long? since)
        {
            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in client.SubscribeAsync(request, since, _stop.Token))
                        await _events.Writer.WriteAsync(e);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    _events.Writer.TryComplete();
                }
            });
        }

        public Task Completion { get; }

        public async Task<List<PylonEvent>> Take(int count)
        {
            using var timeout = new CancellationTokenSource(Wait);
            var list = new List<PylonEvent>();
            while (list.Count < count)
                list.Add(await _events.Reader.ReadAsync(timeout.Token));
            return list;
        }

        public void Dispose() => _stop.Cancel();
    }
}
