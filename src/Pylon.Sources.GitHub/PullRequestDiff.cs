using Pylon.Core.Events;

namespace Pylon.Sources.GitHub;

public sealed record PrChange(string Name, EventKind Kind, EventStatus Status, string Summary, IReadOnlyDictionary<string, string> Hint);

public sealed record DiffOptions(IReadOnlySet<string> Events, bool IncludeDrafts, bool IgnoreBots)
{
    public static DiffOptions Everything { get; } = new(GitHubEvents.All.Select(e => e.Name).ToHashSet(), IncludeDrafts: true, IgnoreBots: false);

    /// <summary>For baselines: the snapshot still moves on (settled checks and all), but nothing is reported.</summary>
    public static DiffOptions Nothing { get; } = new(new HashSet<string>(), IncludeDrafts: true, IgnoreBots: false);
}

/// <summary>
/// The fingerprint rules: what counts as a reportable change between two snapshots of one pull request, and what is
/// noise. Pure, so every rule has a test (docs/implementation-plan.md S1 lists the assumptions behind them).
/// <list type="bullet">
/// <item>A PR seen for the first time is <c>opened</c> only if it's open. One first seen closed is history, not news.</item>
/// <item>Reviews are compared by id, and only once both snapshots have fetched them; the first fetch is a baseline.</item>
/// <item>Checks report <c>pending</c> only when the head commit moved (a push). On the same commit, only a passing or
/// failing result that differs from the last one reported counts, so a re-run that ends the same way is quiet.</item>
/// <item>With <c>ignoreBots</c>, PRs opened by bots and reviews by bots report nothing.</item>
/// <item>Without <c>includeDrafts</c>, drafts report nothing until they're marked ready.</item>
/// </list>
/// The returned snapshot is what to store: it carries forward reviews that weren't refetched and the last settled
/// checks result.
/// </summary>
public static class PullRequestDiff
{
    public static (PullRequestSnapshot Next, IReadOnlyList<PrChange> Changes) Apply(PullRequestSnapshot? previous, PullRequestSnapshot current, DiffOptions options)
    {
        var sameHead = previous is not null && previous.HeadSha == current.HeadSha;
        var next = current with
        {
            Reviews = current.Reviews ?? previous?.Reviews,
            SettledChecks = sameHead ? previous!.SettledChecks : null,
        };

        var changes = new List<PrChange>();
        var quiet = (current.AuthorIsBot && options.IgnoreBots) || (current.Draft && !options.IncludeDrafts);
        var reference = current.Reference;

        void Add(string name, EventKind kind, EventStatus status, string summary, Dictionary<string, string>? hint = null)
        {
            if (options.Events.Contains(name))
                changes.Add(new PrChange(name, kind, status, summary, hint ?? []));
        }

        if (previous is null)
        {
            if (current.State == PrState.Open && !quiet)
                Add(GitHubEvents.Opened, EventKind.Created, EventStatus.Info, $"{reference} opened by {current.Author}: {current.Title}",
                    new() { ["draft"] = current.Draft ? "true" : "false" });
        }
        else
        {
            var botQuiet = current.AuthorIsBot && options.IgnoreBots;
            if (previous.Draft && !current.Draft && current.State == PrState.Open && !botQuiet)
                Add(GitHubEvents.ReadyForReview, EventKind.Changed, EventStatus.Info, $"{reference} ready for review: {current.Title}");

            if (current.State == PrState.Open && !quiet)
            {
                foreach (var reviewer in current.RequestedReviewers.Except(previous.RequestedReviewers, StringComparer.OrdinalIgnoreCase))
                    Add(GitHubEvents.ReviewRequested, EventKind.Changed, EventStatus.Attention, $"{reference} review requested from {reviewer}",
                        new() { ["reviewer"] = reviewer });
            }

            if (previous.Reviews is not null && current.Reviews is not null && !quiet)
            {
                var seen = previous.Reviews.Select(r => r.Id).ToHashSet();
                foreach (var review in current.Reviews.Where(r => !seen.Contains(r.Id)))
                {
                    if (review.AuthorIsBot && options.IgnoreBots)
                        continue;
                    switch (review.State.ToUpperInvariant())
                    {
                        case "APPROVED":
                            Add(GitHubEvents.Approved, EventKind.Changed, EventStatus.Success, $"{reference} approved by {review.Author}",
                                new() { ["state"] = "approved" });
                            break;
                        case "CHANGES_REQUESTED":
                            Add(GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Attention, $"{reference} changes requested by {review.Author}",
                                new() { ["state"] = "changes_requested" });
                            break;
                        case "COMMENTED":
                            Add(GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Info, $"{reference} reviewed by {review.Author}",
                                new() { ["state"] = "commented" });
                            break;
                    }
                }
            }

            if (current.State == PrState.Open && !quiet && current.Checks != ChecksState.None)
            {
                var report = sameHead
                    ? ChecksState.IsSettled(current.Checks) && current.Checks != previous.SettledChecks
                    : true;
                if (report)
                    Add(GitHubEvents.Checks, EventKind.Changed, ChecksStatus(current.Checks), $"{reference} checks {current.Checks}",
                        new() { ["checks"] = current.Checks });
            }

            if (previous.State != PrState.Merged && current.State == PrState.Merged && !botQuiet)
                Add(GitHubEvents.Merged, EventKind.Changed, EventStatus.Merged, $"{reference} merged: {current.Title}");
            else if (previous.State == PrState.Open && current.State == PrState.Closed && !botQuiet)
                Add(GitHubEvents.Closed, EventKind.Removed, EventStatus.Neutral, $"{reference} closed: {current.Title}");
        }

        if (ChecksState.IsSettled(current.Checks))
            next = next with { SettledChecks = current.Checks };
        return (next, changes);
    }

    static EventStatus ChecksStatus(string checks) => checks switch
    {
        ChecksState.Passing => EventStatus.Success,
        ChecksState.Failing => EventStatus.Failure,
        _ => EventStatus.Pending,
    };
}
