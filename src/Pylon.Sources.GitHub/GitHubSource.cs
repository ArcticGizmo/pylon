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

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ConnectionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoPattern();
}
