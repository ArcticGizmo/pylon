using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.Sources.GitHub;

/// <summary>
/// The S1 starting hypothesis, until real measurements replace it (docs/implementation-plan.md):
/// <list type="number">
/// <item>Per repo, the 50 most recently updated PRs with <c>If-None-Match</c>. A 304 is free.</item>
/// <item>Reviews only for open PRs whose <c>updated_at</c> moved (assumes a review bumps it), and only when a
/// review event is switched on.</item>
/// <item>Checks for every open PR's head commit, with ETags, when <c>checks</c> is switched on (assumes a check
/// finishing does <em>not</em> bump <c>updated_at</c>).</item>
/// </list>
/// The first poll of a repo is a baseline: it stores what it sees and reports nothing.
/// <para>
/// With <see cref="GitHubEvents.CheckAssumptions"/> on, it also does the work the hypothesis skips (reviews for every
/// open PR, and an unconditional refetch whenever the list answers 304) and notes wherever the hypothesis would have
/// missed something. That costs requests, so it's for measuring, not for every day.
/// </para>
/// </summary>
public sealed class GitHubMonitor : IMonitorRunner
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // People read payloads in the stream widget, so "+00:00" rather than "+00:00". Never embedded in HTML.
    static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MonitorContext _context;
    readonly ConditionalHttp _http;
    readonly DiffOptions _options;
    readonly bool _checkAssumptions;
    readonly List<Observation> _observations = [];
    readonly List<ObservationNote> _notes = [];

    public GitHubMonitor(MonitorContext context)
    {
        _context = context;
        _http = new ConditionalHttp(context.Http, context.Time);
        var definition = context.Definition;
        _options = new DiffOptions(
            definition.Events.ToHashSet(StringComparer.Ordinal),
            definition.Option(GitHubEvents.IncludeDrafts, false),
            definition.Option(GitHubEvents.IgnoreBots, true));
        _checkAssumptions = definition.Option(GitHubEvents.CheckAssumptions, false);
    }

    bool WantsReviews => _options.Events.Contains(GitHubEvents.Approved) || _options.Events.Contains(GitHubEvents.Reviewed);

    bool WantsChecks => _options.Events.Contains(GitHubEvents.Checks);

    public async Task<PollResult> PollAsync(CancellationToken cancellationToken)
    {
        _http.ResetCounters();
        _observations.Clear();
        _notes.Clear();
        var events = new List<EventDraft>();
        try
        {
            foreach (var repo in _context.Definition.Targets)
                events.AddRange(await PollRepoAsync(repo, cancellationToken));
        }
        catch (GitHubThrottledException ex)
        {
            return Result(events, ex.Message, ex.RetryAfter);
        }
        catch (GitHubRequestException ex)
        {
            return Result(events, ex.Message);
        }
        return Result(events);
    }

    PollResult Result(IReadOnlyList<EventDraft> events, string? error = null, TimeSpan? retryAfter = null) =>
        new(_http.Requests, _http.NotModified, events, error, retryAfter)
        {
            Calls = [.. _http.Calls],
            Observations = [.. _observations],
            Notes = [.. _notes],
        };

    async Task<IReadOnlyList<EventDraft>> PollRepoAsync(string repo, CancellationToken cancellationToken)
    {
        var state = _context.State;
        var baselineKey = $"baseline:{repo}";
        var baselineAt = state.Get(baselineKey) is { } stamp ? DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        var baseline = baselineAt is null;

        var pulls = await ListAsync(repo, cancellationToken);
        var drafts = new List<EventDraft>();
        foreach (var listed in pulls)
        {
            var key = $"pr:{listed.Reference}";
            var previous = state.Get(key) is { } stored ? JsonSerializer.Deserialize<PullRequestSnapshot>(stored, Json) : null;
            var current = listed;
            var moved = previous?.UpdatedAt != listed.UpdatedAt;

            if (listed.State == PrState.Open)
            {
                if ((WantsReviews || _checkAssumptions) && (previous?.Reviews is null || moved || _checkAssumptions))
                    current = current with { Reviews = GitHubRest.ParseReviews(await _http.GetAsync(GitHubRest.ReviewsPath(repo, listed.Number), cancellationToken)) };
                if (WantsChecks && listed.HeadSha.Length > 0 && (_options.IncludeDrafts || !listed.Draft))
                    current = current with { Checks = await ChecksAsync(repo, listed.HeadSha, cancellationToken) };
            }

            // An old PR we've never seen (it was outside the window until someone touched it) isn't newly opened.
            var history = previous is null && baselineAt is { } since && listed.CreatedAt < since;
            var options = baseline ? DiffOptions.Baseline("the first poll of this repository is a baseline")
                : history ? DiffOptions.Baseline("first seen, but opened before this monitor's baseline")
                : _options;
            var (next, changes, quiet) = PullRequestDiff.Apply(previous, current, options);
            state.Set(key, JsonSerializer.Serialize(next, Json));

            foreach (var change in changes)
                drafts.Add(ToDraft(change, previous, next));
            if (!baseline)
                Observe(previous, next, changes, quiet);
        }

        if (baseline)
        {
            state.Set(baselineKey, _context.Time.GetUtcNow().ToString("O"));
            _notes.Add(new ObservationNote(NoteLevel.Info, $"Baseline for {repo}: {pulls.Count} pull request(s) recorded, nothing reported."));
        }
        Forget(repo, pulls);
        return drafts;
    }

    /// <summary>The pulls list. When checking assumptions, a 304 is double-checked with an unconditional fetch (A4).</summary>
    async Task<IReadOnlyList<PullRequestSnapshot>> ListAsync(string repo, CancellationToken cancellationToken)
    {
        var path = GitHubRest.PullsPath(repo);
        var body = await _http.GetAsync(path, cancellationToken);
        if (!_checkAssumptions || !_http.Calls[^1].NotModified)
            return GitHubRest.ParsePulls(repo, body);

        var fresh = await _http.GetFreshAsync(path, "A4: does a 304 ever hide a change?", cancellationToken);
        var before = GitHubRest.ParsePulls(repo, body).ToDictionary(p => p.Number);
        var after = GitHubRest.ParsePulls(repo, fresh);
        var differing = after.Where(p => !before.TryGetValue(p.Number, out var old) || Fields(old, p).Count > 0).Select(p => p.Reference).ToList();
        if (differing.Count > 0 || after.Count != before.Count)
            _notes.Add(new ObservationNote(NoteLevel.Warning,
                $"The {repo} list answered 304, but fetching it again showed changes ({(differing.Count > 0 ? string.Join(", ", differing) : "different pull requests")}). Using the fresh copy.", "A4"));
        return after;
    }

    async Task<string> ChecksAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var runs = await _http.GetAsync(GitHubRest.CheckRunsPath(repo, sha), cancellationToken);
        var status = await _http.GetAsync(GitHubRest.StatusPath(repo, sha), cancellationToken);
        return GitHubRest.CombineChecks(runs, status);
    }

    void Observe(PullRequestSnapshot? previous, PullRequestSnapshot next, IReadOnlyList<PrChange> changes, IReadOnlyList<QuietChange> quiet)
    {
        IReadOnlyList<FieldChange> fields = previous is null ? [new FieldChange("first seen", null, next.State)] : Fields(previous, next);
        IReadOnlyList<ObservationNote> notes = previous is null ? [] : AssumptionNotes(previous, next, fields);
        if (fields.Count == 0 && changes.Count == 0 && quiet.Count == 0 && notes.Count == 0)
            return;
        _observations.Add(new Observation
        {
            Resource = next.Reference,
            Title = next.Title,
            Url = next.Url.Length > 0 ? next.Url : null,
            Topic = GitHubEvents.Topic(next.Repo, next.Number),
            Changes = fields,
            Reported = [.. changes.Select(c => GitHubEvents.Type(c.Name))],
            Quiet = quiet,
            Notes = notes,
        });
    }

    /// <summary>Only meaningful with assumption checks on: that's when reviews are fetched whatever <c>updated_at</c> says.</summary>
    List<ObservationNote> AssumptionNotes(PullRequestSnapshot previous, PullRequestSnapshot next, IReadOnlyList<FieldChange> fields)
    {
        var notes = new List<ObservationNote>();
        if (!_checkAssumptions)
            return notes;
        var moved = previous.UpdatedAt != next.UpdatedAt;
        var others = fields.Where(f => f.Field is not ("updatedAt" or "checks")).ToList();

        if (previous.Reviews is not null && next.Reviews is not null && next.Reviews.Any(r => previous.Reviews.All(p => p.Id != r.Id)))
            notes.Add(moved
                ? new ObservationNote(NoteLevel.Info, "A new review came with updated_at moving, as assumed.", "A1")
                : new ObservationNote(NoteLevel.Warning, "A new review arrived but updated_at didn't move: without assumption checks it would have been missed.", "A1"));

        // On a new commit updated_at moves for the push, so only a re-run or a finish on the same commit says anything.
        if (fields.Any(f => f.Field == "checks") && previous.HeadSha == next.HeadSha)
        {
            if (!moved)
                notes.Add(new ObservationNote(NoteLevel.Info, "Checks changed without updated_at moving, as assumed.", "A2"));
            else if (others.Count == 0)
                notes.Add(new ObservationNote(NoteLevel.Info, "Only checks changed, yet updated_at moved: finishing checks may bump it (harmless; checks are polled anyway).", "A2"));
        }

        if (fields.Any(f => f.Field == "requestedReviewers"))
            notes.Add(moved
                ? new ObservationNote(NoteLevel.Info, "Requested reviewers changed with updated_at moving, as assumed.", "A3")
                : new ObservationNote(NoteLevel.Warning, "Requested reviewers changed but updated_at didn't move.", "A3"));

        return notes;
    }

    /// <summary>The fields that decide events, compared one by one and written for a person.</summary>
    static IReadOnlyList<FieldChange> Fields(PullRequestSnapshot before, PullRequestSnapshot after)
    {
        var changes = new List<FieldChange>();
        void Compare(string field, string? a, string? b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
                changes.Add(new FieldChange(field, a, b));
        }
        Compare("state", before.State, after.State);
        Compare("draft", Bool(before.Draft), Bool(after.Draft));
        Compare("title", before.Title, after.Title);
        Compare("updatedAt", Time(before.UpdatedAt), Time(after.UpdatedAt));
        Compare("headSha", Short(before.HeadSha), Short(after.HeadSha));
        Compare("requestedReviewers", string.Join(", ", before.RequestedReviewers), string.Join(", ", after.RequestedReviewers));
        if (before.Reviews is not null && after.Reviews is not null)
            Compare("reviews", Reviews(before.Reviews), Reviews(after.Reviews));
        if (after.Checks != ChecksState.None || before.Checks != ChecksState.None)
            Compare("checks", before.Checks, after.Checks);
        return changes;
    }

    internal static string Bool(bool value) => value ? "true" : "false";

    // GitHub's timestamps are whole seconds, so the fraction only shows for the fake; dropping it would hide a change.
    internal static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    internal static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    internal static string Reviews(IReadOnlyList<ReviewSnapshot> reviews) =>
        reviews.Count == 0 ? "none" : string.Join(", ", reviews.Select(r => $"{r.State.ToLowerInvariant()} by {r.Author}"));

    /// <summary>Closed PRs that have dropped out of the window can't change in a way we report any more.</summary>
    void Forget(string repo, IReadOnlyList<PullRequestSnapshot> listed)
    {
        var window = listed.Select(p => $"pr:{p.Reference}").ToHashSet(StringComparer.Ordinal);
        foreach (var key in _context.State.Keys().Where(k => k.StartsWith($"pr:{repo}#", StringComparison.Ordinal) && !window.Contains(k)))
        {
            if (_context.State.Get(key) is { } stored && JsonSerializer.Deserialize<PullRequestSnapshot>(stored, Json) is { State: not PrState.Open })
                _context.State.Remove(key);
        }
    }

    EventDraft ToDraft(PrChange change, PullRequestSnapshot? before, PullRequestSnapshot after)
    {
        var payload = new JsonObject
        {
            ["monitor"] = _context.Definition.Id,
            ["change"] = change.Name,
            ["before"] = before is null ? null : JsonSerializer.SerializeToNode(before, Json),
            ["after"] = JsonSerializer.SerializeToNode(after, Json),
            ["requests"] = new JsonArray([.. _http.Calls.Select(c => (JsonNode?)JsonValue.Create($"{c.Status} {c.Path}"))]),
        };
        return new EventDraft
        {
            Source = GitHubSource.SourceName,
            MonitorId = _context.Definition.Id,
            Topic = GitHubEvents.Topic(after.Repo, after.Number),
            Type = GitHubEvents.Type(change.Name),
            Kind = change.Kind,
            ResourceId = after.Url.Length > 0 ? after.Url : null,
            Hint = change.Hint,
            Summary = change.Summary,
            Status = change.Status,
            Via = EventVia.Poll,
            Payload = Payload.Json(payload.ToJsonString(Readable)),
        };
    }
}
