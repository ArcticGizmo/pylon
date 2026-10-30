using System.Text.Json;
using System.Text.RegularExpressions;
using Pylon.Core.Monitors;

namespace Pylon.Sources.GitHub;

public sealed partial class GitHubSource : ISource
{
    public const string SourceName = "github";

    public string Name => SourceName;

    public string DisplayName => "GitHub pull requests";

    public string Icon => "pull-request";

    public bool UsesPostern => true;

    public IReadOnlyList<SourceEvent> Events => GitHubEvents.All;

    public IReadOnlyList<string> Validate(MonitorDefinition monitor)
    {
        var problems = new List<string>();
        if (!ConnectionPattern().IsMatch(monitor.Connection))
            problems.Add($"'{monitor.Connection}' isn't a Postern connection name (lowercase letters, digits, '-').");
        if (monitor.Targets.Count == 0)
            problems.Add("Add at least one repository (owner/repo).");
        foreach (var target in monitor.Targets.Where(t => !RepoPattern().IsMatch(t)))
            problems.Add($"'{target}' isn't owner/repo. Wildcards aren't supported yet.");
        if (monitor.Events.Count == 0)
            problems.Add("Pick at least one change to report.");
        foreach (var name in monitor.Events.Where(e => GitHubEvents.All.All(known => known.Name != e)))
            problems.Add($"'{name}' isn't a GitHub change Pylon knows.");
        return problems;
    }

    /// <summary>
    /// Per owner (D5), so adding another repo from an owner already approved doesn't need a new Postern prompt.
    /// Postern's <c>**</c> matches zero or more segments, so <c>pulls/**</c> covers the list and each PR's reviews.
    /// </summary>
    public IReadOnlyList<string> ScopesFor(MonitorDefinition monitor)
    {
        var scopes = new List<string>();
        foreach (var owner in monitor.Targets.Where(t => RepoPattern().IsMatch(t)).Select(t => t.Split('/')[0].ToLowerInvariant()).Distinct())
        {
            scopes.Add($"GET {monitor.Connection}/repos/{owner}/*/pulls/**");
            if (monitor.Events.Contains(GitHubEvents.Checks))
            {
                scopes.Add($"GET {monitor.Connection}/repos/{owner}/*/commits/*/check-runs");
                scopes.Add($"GET {monitor.Connection}/repos/{owner}/*/commits/*/status");
            }
        }
        return scopes;
    }

    public IMonitorRunner Create(MonitorContext context) => new GitHubMonitor(context);

    /// <summary>Every pull request a monitor has a fingerprint for, open ones first, most recently updated first.</summary>
    public IReadOnlyList<WatchedItem> Watched(IMonitorState state)
    {
        var pulls = new List<PullRequestSnapshot>();
        foreach (var key in state.Keys().Where(k => k.StartsWith("pr:", StringComparison.Ordinal)))
        {
            if (state.Get(key) is not { } stored)
                continue;
            try
            {
                if (JsonSerializer.Deserialize<PullRequestSnapshot>(stored, Json) is { } pr)
                    pulls.Add(pr);
            }
            catch (JsonException)
            {
                // A fingerprint written by an older build: the next poll replaces it.
            }
        }
        return
        [
            .. pulls
                .OrderBy(p => p.State == PrState.Open ? 0 : 1)
                .ThenByDescending(p => p.UpdatedAt)
                .Select(p => new WatchedItem(p.Reference, p.Title, p.Url.Length > 0 ? p.Url : null,
                [
                    ("state", p.Draft ? $"{p.State} (draft)" : p.State),
                    ("author", p.AuthorIsBot ? $"{p.Author} (bot)" : p.Author),
                    ("updatedAt", GitHubMonitor.Time(p.UpdatedAt)),
                    ("head", GitHubMonitor.Short(p.HeadSha)),
                    ("requested", p.RequestedReviewers.Count == 0 ? "nobody" : string.Join(", ", p.RequestedReviewers)),
                    ("reviews", p.Reviews is null ? "not fetched yet" : GitHubMonitor.Reviews(p.Reviews)),
                    ("checks", p.SettledChecks is { } settled && settled != p.Checks ? $"{p.Checks} (last reported {settled})" : p.Checks),
                ])),
        ];
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? DescribeDelivery(string kind, string body)
    {
        try
        {
            var delivery = WebhookNormaliser.Parse(kind, body);
            return delivery.Repo is null ? delivery.Description : $"{delivery.Description} for {delivery.Repo}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A webhook for one of the monitor's repositories becomes that monitor's events, with the same switches (and the
    /// same reasons for keeping quiet) as the poll path. Anything else isn't this monitor's business.
    /// </summary>
    public PollResult? Receive(MonitorDefinition monitor, string kind, string body, Pylon.Core.Events.EventVia via)
    {
        var delivery = WebhookNormaliser.Parse(kind, body);
        if (delivery.Repo is null || !monitor.Targets.Contains(delivery.Repo, StringComparer.OrdinalIgnoreCase))
            return null;

        var ignoreBots = monitor.Option(GitHubEvents.IgnoreBots, true);
        var includeDrafts = monitor.Option(GitHubEvents.IncludeDrafts, false);
        var events = new List<Pylon.Core.Events.EventDraft>();
        var observations = new List<Observation>();
        foreach (var pr in delivery.Changes.GroupBy(c => c.Number))
        {
            var reported = new List<string>();
            var quiet = new List<QuietChange>();
            foreach (var change in pr)
            {
                var reason = !monitor.Events.Contains(change.Name) ? $"'{change.Name}' is switched off for this monitor"
                    : change.AuthorIsBot && ignoreBots && change.Name != GitHubEvents.Checks ? "opened by a bot, and 'ignore bots' is on"
                    : change.ReviewerIsBot && ignoreBots ? "a review by a bot, and 'ignore bots' is on"
                    : change.Draft && !includeDrafts && change.Name is not (GitHubEvents.ReadyForReview or GitHubEvents.Merged or GitHubEvents.Closed) ? "a draft, and 'include drafts' is off"
                    : null;
                if (reason is null)
                {
                    events.Add(change.ToDraft(monitor.Id, via, body));
                    reported.Add(GitHubEvents.Type(change.Name));
                }
                else
                {
                    quiet.Add(new QuietChange(GitHubEvents.Type(change.Name), change.Summary, reason));
                }
            }
            var first = pr.First();
            observations.Add(new Observation
            {
                Resource = first.Reference,
                Title = first.Title,
                Url = first.Url,
                Topic = GitHubEvents.Topic(first.Repo, first.Number),
                Reported = reported,
                Quiet = quiet,
            });
        }

        IReadOnlyList<ObservationNote> notes = delivery.Changes.Count == 0
            ? [new ObservationNote(NoteLevel.Info, $"GitHub sends {delivery.Description}, but it isn't a change Pylon reports.")]
            : [];
        return new PollResult(0, 0, events) { Observations = observations, Notes = notes };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ConnectionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoPattern();
}
