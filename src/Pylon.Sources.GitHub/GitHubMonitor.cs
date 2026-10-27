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
/// </summary>
public sealed class GitHubMonitor : IMonitorRunner
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // People read payloads in the stream widget, so "+00:00" rather than "+00:00". Never embedded in HTML.
    static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MonitorContext _context;
    readonly ConditionalHttp _http;
    readonly DiffOptions _options;

    public GitHubMonitor(MonitorContext context)
    {
        _context = context;
        _http = new ConditionalHttp(context.Http, context.Time);
        var definition = context.Definition;
        _options = new DiffOptions(
            definition.Events.ToHashSet(StringComparer.Ordinal),
            definition.Option(GitHubEvents.IncludeDrafts, false),
            definition.Option(GitHubEvents.IgnoreBots, true));
    }

    bool WantsReviews => _options.Events.Contains(GitHubEvents.Approved) || _options.Events.Contains(GitHubEvents.Reviewed);

    bool WantsChecks => _options.Events.Contains(GitHubEvents.Checks);

    public async Task<PollResult> PollAsync(CancellationToken cancellationToken)
    {
        _http.ResetCounters();
        var events = new List<EventDraft>();
        try
        {
            foreach (var repo in _context.Definition.Targets)
                events.AddRange(await PollRepoAsync(repo, cancellationToken));
        }
        catch (GitHubThrottledException ex)
        {
            return new PollResult(_http.Requests, _http.NotModified, events, ex.Message, ex.RetryAfter);
        }
        catch (GitHubRequestException ex)
        {
            return new PollResult(_http.Requests, _http.NotModified, events, ex.Message);
        }
        return new PollResult(_http.Requests, _http.NotModified, events);
    }

    async Task<IReadOnlyList<EventDraft>> PollRepoAsync(string repo, CancellationToken cancellationToken)
    {
        var state = _context.State;
        var baselineKey = $"baseline:{repo}";
        var baselineAt = state.Get(baselineKey) is { } stamp ? DateTimeOffset.Parse(stamp, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        var baseline = baselineAt is null;

        var pulls = GitHubRest.ParsePulls(repo, await _http.GetAsync(GitHubRest.PullsPath(repo), cancellationToken));
        var drafts = new List<EventDraft>();
        foreach (var listed in pulls)
        {
            var key = $"pr:{listed.Reference}";
            var previous = state.Get(key) is { } stored ? JsonSerializer.Deserialize<PullRequestSnapshot>(stored, Json) : null;
            var current = listed;

            if (listed.State == PrState.Open)
            {
                if (WantsReviews && (previous?.Reviews is null || previous.UpdatedAt != listed.UpdatedAt))
                    current = current with { Reviews = GitHubRest.ParseReviews(await _http.GetAsync(GitHubRest.ReviewsPath(repo, listed.Number), cancellationToken)) };
                if (WantsChecks && listed.HeadSha.Length > 0 && (_options.IncludeDrafts || !listed.Draft))
                    current = current with { Checks = await ChecksAsync(repo, listed.HeadSha, cancellationToken) };
            }

            // An old PR we've never seen (it was outside the window until someone touched it) isn't newly opened.
            var history = previous is null && baselineAt is { } since && listed.CreatedAt < since;
            var (next, changes) = PullRequestDiff.Apply(previous, current, baseline || history ? DiffOptions.Nothing : _options);
            state.Set(key, JsonSerializer.Serialize(next, Json));

            foreach (var change in changes)
                drafts.Add(ToDraft(change, previous, next));
        }

        if (baseline)
            state.Set(baselineKey, _context.Time.GetUtcNow().ToString("O"));
        Forget(repo, pulls);
        return drafts;
    }

    async Task<string> ChecksAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var runs = await _http.GetAsync(GitHubRest.CheckRunsPath(repo, sha), cancellationToken);
        var status = await _http.GetAsync(GitHubRest.StatusPath(repo, sha), cancellationToken);
        return GitHubRest.CombineChecks(runs, status);
    }

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
            ["requests"] = new JsonArray([.. _http.Log.Select(l => (JsonNode?)JsonValue.Create(l))]),
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
