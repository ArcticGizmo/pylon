using Microsoft.Extensions.Logging;
using Pylon.Broker.Events;
using Pylon.Broker.Storage;
using Pylon.Core.Events;
using Pylon.Core.Monitors;
using Postern.Client;

namespace Pylon.Broker.Monitors;

public enum MonitorState { Starting, Disabled, Invalid, WaitingForPostern, Polling, Idle, Error }

public sealed record MonitorStatus(
    string Id,
    string Name,
    string Source,
    MonitorState State,
    string? Detail,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? NextPollAt,
    DateTimeOffset? LastEventAt,
    long Requests,
    long NotModified,
    long Events);

/// <summary>
/// Runs every monitor in <c>monitors.json</c> on its own loop: poll, publish what changed, wait (with jitter, or
/// longer after errors and throttling). Saving new definitions restarts the loops; fingerprint state survives, so a
/// restart is never a reason to report old changes again.
/// </summary>
public sealed class MonitorHost : IAsyncDisposable
{
    /// <summary>Errors back off by doubling up to this.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    /// <summary>The connection name that polls the test bench directly instead of going through Postern (dev only).</summary>
    public const string TestBenchConnection = "testbench";

    /// <summary>
    /// How long an event from one path waits to be paired with the same change arriving the other way (a webhook,
    /// then the poll that finds it, or the reverse), so it's reported once.
    /// </summary>
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(10);

    readonly EventHub _hub;
    readonly PollJournal _journal;
    readonly PosternGateway? _postern;
    readonly Func<string, HttpClient>? _httpOverride;
    readonly Uri? _testBench;
    readonly Lock _pairing = new();
    readonly Dictionary<string, Dictionary<string, Reported>> _reported = new(StringComparer.Ordinal);
    readonly string _dataDirectory;
    readonly TimeProvider _time;
    readonly ILogger _log;
    readonly Dictionary<string, ISource> _sources;
    readonly Lock _gate = new();
    readonly SemaphoreSlim _posternWake = new(0);
    List<Worker> _workers = [];
    CancellationTokenSource? _posternLoop;
    Task? _posternTask;

    public MonitorHost(IEnumerable<ISource> sources, EventHub hub, PollJournal journal, PosternGateway? postern, Func<string, HttpClient>? httpOverride,
        Uri? testBench, string dataDirectory, TimeProvider time, ILogger log)
    {
        _sources = sources.ToDictionary(s => s.Name, StringComparer.Ordinal);
        _hub = hub;
        _journal = journal;
        _postern = postern;
        _httpOverride = httpOverride;
        _testBench = testBench;
        _dataDirectory = dataDirectory;
        _time = time;
        _log = log;
        if (_postern is not null)
            _postern.Changed += () =>
            {
                WakeAll();
                Changed?.Invoke();
            };
    }

    public IReadOnlyList<ISource> Sources => [.. _sources.Values];

    public ISource? Source(string name) => _sources.GetValueOrDefault(name);

    public IReadOnlyList<MonitorDefinition> Definitions { get; private set; } = [];

    /// <summary>Why monitors.json couldn't be read, if it couldn't. Nothing runs until it's fixed or saved over.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Raised from worker threads whenever any monitor's status changes.</summary>
    public event Action? Changed;

    /// <summary>Raised from worker threads after each poll is in the journal.</summary>
    public event Action<PollRecord>? Polled;

    public PollJournal Journal => _journal;

    /// <summary>Whether monitors can use the <see cref="TestBenchConnection"/> in this run.</summary>
    public bool TestBenchAvailable => _testBench is not null;

    /// <summary>What a monitor is keeping track of, from its stored fingerprints.</summary>
    public IReadOnlyList<WatchedItem> Watched(string id) =>
        Definitions.FirstOrDefault(d => d.Id == id) is { } definition && Source(definition.Source) is { } source
            ? source.Watched(_hub.Store.StateFor(id))
            : [];

    public void Start()
    {
        try
        {
            Definitions = MonitorsFile.Load(_dataDirectory);
            LoadError = null;
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Definitions = [];
            LoadError = ex.Message;
            _log.LogError("{File} wasn't used: {Error}", MonitorsFile.FileName, ex.Message);
        }
        Restart();
        if (_postern is not null && _httpOverride is null)
        {
            _posternLoop = new CancellationTokenSource();
            _posternTask = Task.Run(() => PosternLoop(_posternLoop.Token));
        }
    }

    /// <summary>Validates, writes monitors.json and restarts the loops. Throws <see cref="ArgumentException"/> with every problem.</summary>
    public void Save(IReadOnlyList<MonitorDefinition> definitions)
    {
        var problems = new List<string>();
        foreach (var group in definitions.GroupBy(d => d.Id).Where(g => g.Count() > 1))
            problems.Add($"The id '{group.Key}' is used more than once.");
        foreach (var definition in definitions)
        {
            if (!MonitorDefinition.IsValidId(definition.Id))
                problems.Add($"'{definition.Id}' isn't a monitor id (lowercase letters, digits, '-').");
            if (definition.PollSeconds < 10)
                problems.Add($"{definition.Name}: poll at least every 10 seconds.");
        }
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, problems));

        MonitorsFile.Save(_dataDirectory, definitions);
        Definitions = definitions;
        LoadError = null;
        Restart();
        _posternWake.Release();
    }

    /// <summary>Forgets a monitor's fingerprints: its next poll is a fresh baseline.</summary>
    public void ResetState(string id) => _hub.Store.ClearState(id);

    public void PollNow(string id)
    {
        lock (_gate)
            _workers.FirstOrDefault(w => w.Definition.Id == id)?.Wake();
    }

    public void AskPosternAgain()
    {
        _postern?.AskAgain();
        _posternWake.Release();
    }

    /// <summary>The scopes the enabled, valid monitors need, in a stable order so an unchanged set never re-prompts.</summary>
    public IReadOnlyList<string> WantedScopes() =>
    [
        .. Definitions
            .Where(d => d.Enabled && d.Connection != TestBenchConnection && _sources.TryGetValue(d.Source, out var s) && s.UsesPostern && s.Validate(d).Count == 0)
            .SelectMany(d => _sources[d.Source].ScopesFor(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];

    public IReadOnlyList<MonitorStatus> Status()
    {
        lock (_gate)
            return [.. _workers.Select(w => w.Status)];
    }

    /// <summary>What happened to one pushed delivery: the events published, and the monitors that took it.</summary>
    public sealed record Delivery(IReadOnlyList<EventRecord> Records, IReadOnlyList<string> Monitors, string Description);

    /// <summary>
    /// A webhook (now from dev ingest, later from push mode) goes to every enabled monitor that watches what it's about,
    /// with that monitor's switches applied, and into that monitor's journal as a received delivery. One that no monitor
    /// watches is journalled as unmatched. Throws <see cref="ArgumentException"/> for an unknown source, and lets the
    /// source's parse errors through.
    /// </summary>
    public Delivery Receive(string sourceName, string kind, string body, EventVia via, string? onlyMonitor = null)
    {
        if (!_sources.TryGetValue(sourceName, out var source))
            throw new ArgumentException($"Pylon doesn't know the source '{sourceName}'.", nameof(sourceName));
        var started = _time.GetUtcNow();
        var description = source.DescribeDelivery(kind, body) ?? kind;
        var trace = new RequestTrace { Method = "POST", Path = $"{sourceName} webhook: {kind}", Status = 200, Bytes = body.Length, Body = body };

        var records = new List<EventRecord>();
        var monitors = new List<string>();
        foreach (var definition in Definitions.Where(d => d.Enabled && d.Source == sourceName && (onlyMonitor is null || d.Id == onlyMonitor)))
        {
            if (source.Receive(definition, kind, body, via) is not { } result)
                continue;
            monitors.Add(definition.Id);
            var (published, observations) = PublishPaired(definition.Id, result.Events, result.Observations, pushed: true);
            records.AddRange(published);
            Record(new PollRecord
            {
                MonitorId = definition.Id,
                Kind = PollKind.Received,
                Received = description,
                StartedAt = started,
                Calls = [trace],
                Observations = observations,
                Notes = result.Notes,
                Sequences = [.. published.Select(r => r.Sequence)],
            });
            lock (_gate)
                _workers.FirstOrDefault(w => w.Definition.Id == definition.Id)?.Received(published.Count);
        }

        if (monitors.Count == 0)
            Record(new PollRecord
            {
                MonitorId = PollRecord.Unmatched,
                Kind = PollKind.Received,
                Received = description,
                StartedAt = started,
                Calls = [trace],
                Notes = [new ObservationNote(NoteLevel.Info, onlyMonitor is null
                    ? "No enabled monitor watches what this is about, so nothing was reported."
                    : $"The monitor '{onlyMonitor}' isn't enabled or doesn't watch what this is about.")],
            });
        return new Delivery(records, monitors, description);
    }

    sealed record Reported(long Sequence, DateTimeOffset At, bool Pushed);

    static string PairingKey(EventDraft draft) =>
        $"{draft.Topic}|{draft.Type}|{(draft.Hint is null ? "" : string.Join(',', draft.Hint.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")))}";

    /// <summary>
    /// Publishes a monitor's events, minus any the other path (poll or push) already reported within the pairing
    /// window. Those are moved to the observation's quiet list, so the Activity says why they didn't come through. A
    /// pairing is used once, so a later change that looks the same (checks failing, then passing again) still reports.
    /// </summary>
    (IReadOnlyList<EventRecord> Published, IReadOnlyList<Observation> Observations) PublishPaired(
        string monitorId, IReadOnlyList<EventDraft> drafts, IReadOnlyList<Observation> observations, bool pushed)
    {
        lock (_pairing)
        {
            var now = _time.GetUtcNow();
            if (!_reported.TryGetValue(monitorId, out var recent))
                _reported[monitorId] = recent = new Dictionary<string, Reported>(StringComparer.Ordinal);
            foreach (var expired in recent.Where(p => now - p.Value.At > PairingWindow).Select(p => p.Key).ToList())
                recent.Remove(expired);

            var keep = new List<EventDraft>();
            var paired = new List<(EventDraft Draft, Reported Match)>();
            foreach (var draft in drafts)
            {
                var key = PairingKey(draft);
                if (recent.TryGetValue(key, out var match) && match.Pushed != pushed)
                {
                    recent.Remove(key);
                    paired.Add((draft, match));
                }
                else
                {
                    keep.Add(draft);
                }
            }

            var published = keep.Count > 0
                ? _hub.Publish([.. keep.Select(e => e.MonitorId is null ? e with { MonitorId = monitorId } : e)])
                : [];
            for (var i = 0; i < published.Count; i++)
                recent[PairingKey(keep[i])] = new Reported(published[i].Sequence, now, pushed);

            if (paired.Count == 0)
                return (published, observations);
            var updated = observations.ToList();
            foreach (var (draft, match) in paired)
            {
                var reason = match.Pushed ? $"already received from a webhook as #{match.Sequence}" : $"already found by polling as #{match.Sequence}";
                var index = updated.FindIndex(o => o.Topic == draft.Topic);
                var quiet = new QuietChange(draft.Type, draft.Summary, reason);
                if (index >= 0)
                    updated[index] = updated[index] with { Reported = [.. updated[index].Reported.Where(t => t != draft.Type)], Quiet = [.. updated[index].Quiet, quiet] };
                else
                    updated.Add(new Observation { Resource = draft.Topic, Topic = draft.Topic, Quiet = [quiet] });
            }
            return (published, updated);
        }
    }

    /// <summary>The journal is for looking at; a failure to write it never stops a monitor or a delivery.</summary>
    void Record(PollRecord poll)
    {
        PollRecord stored;
        try
        {
            // Not Polled?.Invoke(_journal.Add(poll)): with no listener, that skips the write as well as the call.
            stored = _journal.Add(poll);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't record a poll of {Id}", poll.MonitorId);
            return;
        }
        Polled?.Invoke(stored);
    }

    void Restart()
    {
        List<Worker> old;
        lock (_gate)
        {
            old = _workers;
            _workers = [.. Definitions.Select(d => new Worker(this, d))];
            foreach (var worker in _workers)
                worker.Start();
        }
        foreach (var worker in old)
            _ = worker.StopAsync();
        Changed?.Invoke();
    }

    void WakeAll()
    {
        lock (_gate)
            foreach (var worker in _workers)
                worker.Wake();
    }

    async Task PosternLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _postern!.EnsureAsync(WantedScopes(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Checking Postern failed");
            }
            // Approved or refused: wait for monitors to change. Otherwise (not running) look again shortly.
            var interval = _postern!.State is PosternState.Approved or PosternState.Denied ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
            try
            {
                await _posternWake.WaitAsync(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _posternLoop?.Cancel();
        if (_posternTask is not null)
            await _posternTask.ContinueWith(_ => { }, TaskScheduler.Default);
        List<Worker> workers;
        lock (_gate)
        {
            workers = _workers;
            _workers = [];
        }
        await Task.WhenAll(workers.Select(w => w.StopAsync()));
    }

    sealed class Worker(MonitorHost host, MonitorDefinition definition)
    {
        readonly CancellationTokenSource _stop = new();
        readonly SemaphoreSlim _wake = new(0);
        Task? _loop;
        IMonitorRunner? _runner;
        int _failures;
        bool _outage;

        public MonitorDefinition Definition => definition;

        public MonitorStatus Status { get; private set; } =
            new(definition.Id, definition.Name, definition.Source, MonitorState.Starting, null, null, null, null, 0, 0, 0);

        public void Start() => _loop = Task.Run(() => Run(_stop.Token));

        public void Wake() => _wake.Release();

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (_loop is not null)
                await _loop.ContinueWith(_ => { }, TaskScheduler.Default);
            _stop.Dispose();
        }

        async Task Run(CancellationToken cancellationToken)
        {
            if (!definition.Enabled)
            {
                Update(MonitorState.Disabled, "Turned off.");
                return;
            }
            if (host.Source(definition.Source) is not { } source)
            {
                Update(MonitorState.Invalid, $"Pylon doesn't know the source '{definition.Source}'.");
                return;
            }
            if (source.Validate(definition) is { Count: > 0 } problems)
            {
                Update(MonitorState.Invalid, string.Join(" ", problems));
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    delay = await PollOnce(source, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    host._log.LogWarning(ex, "Monitor {Id} poll failed", definition.Id);
                    var error = Describe(ex);
                    if (_pollStarted is { } started)
                        Record(new PollRecord { MonitorId = definition.Id, StartedAt = started, Milliseconds = Elapsed(), Outcome = PollOutcome.Error, Error = error });
                    delay = Failed(error, null);
                }
                _pollStarted = null;

                Status = Status with { NextPollAt = host._time.GetUtcNow() + delay };
                host.Changed?.Invoke();
                try
                {
                    await _wake.WaitAsync(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        async Task<TimeSpan> PollOnce(ISource source, CancellationToken cancellationToken)
        {
            if (_runner is null)
            {
                HttpClient http;
                if (!source.UsesPostern)
                    http = Unused;
                else if (host._httpOverride is { } factory)
                    http = factory(definition.Connection);
                else if (definition.Connection == TestBenchConnection)
                {
                    if (host._testBench is not { } bench)
                    {
                        Update(MonitorState.Invalid, "The 'testbench' connection only exists in dev builds (or with --dev-ingest). Use a Postern connection.");
                        return MaxBackoff;
                    }
                    http = new HttpClient { BaseAddress = bench };
                }
                else if (host._postern is { State: PosternState.Approved } postern && ScopesApproved(source, postern))
                    http = postern.HttpFor(definition.Connection);
                else
                {
                    Update(MonitorState.WaitingForPostern, host._postern?.Detail ?? "Waiting for Postern.");
                    return TimeSpan.FromSeconds(30);
                }
                _runner = source.Create(new MonitorContext(definition, http, host._hub.Store.StateFor(definition.Id), host._time));
            }

            Update(MonitorState.Polling, null);
            _pollStarted = host._time.GetUtcNow();
            _pollTimestamp = host._time.GetTimestamp();
            var result = await _runner.PollAsync(cancellationToken);
            var now = host._time.GetUtcNow();
            var (published, observations) = host.PublishPaired(definition.Id, result.Events, result.Observations, pushed: false);
            Record(new PollRecord
            {
                MonitorId = definition.Id,
                StartedAt = _pollStarted.Value,
                Milliseconds = Elapsed(),
                Outcome = result.Error is null ? PollOutcome.Ok : result.RetryAfter is null ? PollOutcome.Error : PollOutcome.Throttled,
                Error = result.Error,
                Requests = result.Requests,
                NotModified = result.NotModified,
                Calls = result.Calls,
                Observations = observations,
                Notes = result.Notes,
                Sequences = [.. published.Select(r => r.Sequence)],
            });
            _pollStarted = null;
            Status = Status with
            {
                LastPollAt = now,
                LastEventAt = published.Count > 0 ? now : Status.LastEventAt,
                Requests = Status.Requests + result.Requests,
                NotModified = Status.NotModified + result.NotModified,
                Events = Status.Events + published.Count,
            };

            if (result.Error is { } error)
                return Failed(error, result.RetryAfter);

            if (_outage)
                host._hub.Publish([EventDraft.Resync(definition.Source, "recovered", definition.Id)]);
            _outage = false;
            _failures = 0;
            Update(MonitorState.Idle, result.NotModified > 0 ? $"{result.Requests} request(s), {result.NotModified} unchanged (304)." : $"{result.Requests} request(s).");
            return Jitter(TimeSpan.FromSeconds(definition.PollSeconds));
        }

        DateTimeOffset? _pollStarted;
        long _pollTimestamp;

        long Elapsed() => (long)host._time.GetElapsedTime(_pollTimestamp).TotalMilliseconds;

        void Record(PollRecord poll) => host.Record(poll);

        /// <summary>A webhook gave this monitor events: they count on its card like polled ones.</summary>
        public void Received(int events)
        {
            if (events == 0)
                return;
            Status = Status with { LastEventAt = host._time.GetUtcNow(), Events = Status.Events + events };
            host.Changed?.Invoke();
        }

        bool ScopesApproved(ISource source, PosternGateway postern) =>
            source.ScopesFor(definition).All(s => postern.Approved.Contains(s, StringComparer.OrdinalIgnoreCase));

        TimeSpan Failed(string error, TimeSpan? retryAfter)
        {
            _failures++;
            _outage = true;
            // A rebuilt runner starts from a fresh HttpClient, which matters after Postern restarted.
            _runner = null;
            var backoff = TimeSpan.FromSeconds(definition.PollSeconds * Math.Pow(2, Math.Min(_failures, 6)));
            var delay = retryAfter ?? (backoff > MaxBackoff ? MaxBackoff : backoff);
            Update(MonitorState.Error, error);
            return delay;
        }

        static string Describe(Exception ex) => ex switch
        {
            PosternVerificationException => ex.Message,
            PosternUnavailableException or HttpRequestException or IOException => $"Couldn't reach Postern: {ex.Message}",
            _ => ex.Message,
        };

        static TimeSpan Jitter(TimeSpan interval) =>
            interval * (0.9 + Random.Shared.NextDouble() * 0.2);

        void Update(MonitorState state, string? detail)
        {
            if (Status.State == state && Status.Detail == detail)
                return;
            Status = Status with { State = state, Detail = detail };
            host.Changed?.Invoke();
        }

        static readonly HttpClient Unused = new();
    }
}
