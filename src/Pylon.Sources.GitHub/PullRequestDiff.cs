using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.Sources.GitHub;

public sealed record PrChange(string Name, EventKind Kind, EventStatus Status, string Summary, IReadOnlyDictionary<string, string> Hint);

public sealed record DiffOptions(IReadOnlySet<string> Events, bool IncludeDrafts, bool IgnoreBots)
{
    public static DiffOptions Everything { get; } = new(GitHubEvents.All.Select(e => e.Name).ToHashSet(), IncludeDrafts: true, IgnoreBots: false);

    /// <summary>When set, every change the rules recognise is kept quiet with this reason (a baseline).</summary>
    public string? Silence { get; init; }

    /// <summary>For baselines: the snapshot still moves on (settled checks and all), but nothing is reported.</summary>
    public static DiffOptions Baseline(string reason) => Everything with { Silence = reason };
}

/// <summary>The snapshot to store, the changes to report, and the ones recognised but kept quiet.</summary>
public sealed record DiffResult(PullRequestSnapshot Next, IReadOnlyList<PrChange> Changes, IReadOnlyList<QuietChange> Quiet)
{
    public void Deconstruct(out PullRequestSnapshot next, out IReadOnlyList<PrChange> changes)
    {
        next = Next;
        changes = Changes;
    }
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
/// Anything recognised but not reported comes back in <see cref="DiffResult.Quiet"/> with the reason, so the monitor
/// detail page can answer "why didn't I hear about that?". The returned snapshot is what to store: it carries forward
/// reviews that weren't refetched and the last settled checks result.
/// </summary>
public static class PullRequestDiff
{
    public static DiffResult Apply(PullRequestSnapshot? previous, PullRequestSnapshot current, DiffOptions options)
    {
        var sameHead = previous is not null && previous.HeadSha == current.HeadSha;
        var next = current with
        {
            Reviews = current.Reviews ?? previous?.Reviews,
            SettledChecks = sameHead ? previous!.SettledChecks : null,
        };

        var changes = new List<PrChange>();
        var quiet = new List<QuietChange>();
        var botReason = current.AuthorIsBot && options.IgnoreBots ? "opened by a bot, and 'ignore bots' is on" : null;
        var draftReason = current.Draft && !options.IncludeDrafts ? "a draft, and 'include drafts' is off" : null;
        var muted = botReason ?? draftReason;
        var reference = current.Reference;

        void Consider(string name, EventKind kind, EventStatus status, string summary, Dictionary<string, string>? hint = null, string? reason = null)
        {
            reason = options.Silence ?? reason ?? (options.Events.Contains(name) ? null : $"'{name}' is switched off for this monitor");
            if (reason is null)
                changes.Add(new PrChange(name, kind, status, summary, hint ?? []));
            else
                quiet.Add(new QuietChange(GitHubEvents.Type(name), summary, reason));
        }

        if (previous is null)
        {
            if (current.State == PrState.Open)
                Consider(GitHubEvents.Opened, EventKind.Created, EventStatus.Info, $"{reference} opened by {current.Author}: {current.Title}",
                    new() { ["draft"] = current.Draft ? "true" : "false" }, muted);
        }
        else
        {
            if (previous.Draft && !current.Draft && current.State == PrState.Open)
                Consider(GitHubEvents.ReadyForReview, EventKind.Changed, EventStatus.Info, $"{reference} ready for review: {current.Title}", reason: botReason);

            if (current.State == PrState.Open)
            {
                foreach (var reviewer in current.RequestedReviewers.Except(previous.RequestedReviewers, StringComparer.OrdinalIgnoreCase))
                    Consider(GitHubEvents.ReviewRequested, EventKind.Changed, EventStatus.Attention, $"{reference} review requested from {reviewer}",
                        new() { ["reviewer"] = reviewer }, muted);
            }

            if (previous.Reviews is not null && current.Reviews is not null)
            {
                var seen = previous.Reviews.Select(r => r.Id).ToHashSet();
                foreach (var review in current.Reviews.Where(r => !seen.Contains(r.Id)))
                {
                    var reason = muted ?? (review.AuthorIsBot && options.IgnoreBots ? "a review by a bot, and 'ignore bots' is on" : null);
                    switch (review.State.ToUpperInvariant())
                    {
                        case "APPROVED":
                            Consider(GitHubEvents.Approved, EventKind.Changed, EventStatus.Success, $"{reference} approved by {review.Author}",
                                new() { ["state"] = "approved" }, reason);
                            break;
                        case "CHANGES_REQUESTED":
                            Consider(GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Attention, $"{reference} changes requested by {review.Author}",
                                new() { ["state"] = "changes_requested" }, reason);
                            break;
                        case "COMMENTED":
                            Consider(GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Info, $"{reference} reviewed by {review.Author}",
                                new() { ["state"] = "commented" }, reason);
                            break;
                    }
                }
            }

            if (current.State == PrState.Open && current.Checks != ChecksState.None)
            {
                var summary = $"{reference} checks {current.Checks}";
                var hint = new Dictionary<string, string> { ["checks"] = current.Checks };
                if (!sameHead)
                    Consider(GitHubEvents.Checks, EventKind.Changed, ChecksStatus(current.Checks), summary, hint, muted);
                else if (ChecksState.IsSettled(current.Checks) && current.Checks != previous.SettledChecks)
                    Consider(GitHubEvents.Checks, EventKind.Changed, ChecksStatus(current.Checks), summary, hint, muted);
                else if (current.Checks != previous.Checks)
                    quiet.Add(new QuietChange(GitHubEvents.Type(GitHubEvents.Checks), summary, ChecksState.IsSettled(current.Checks)
                        ? $"{current.Checks} again on the same commit, which was already reported"
                        : "re-running on the same commit; only a new commit reports pending"));
            }

            if (previous.State != PrState.Merged && current.State == PrState.Merged)
                Consider(GitHubEvents.Merged, EventKind.Changed, EventStatus.Merged, $"{reference} merged: {current.Title}", reason: botReason);
            else if (previous.State == PrState.Open && current.State == PrState.Closed)
                Consider(GitHubEvents.Closed, EventKind.Removed, EventStatus.Neutral, $"{reference} closed: {current.Title}", reason: botReason);
        }

        if (ChecksState.IsSettled(current.Checks))
            next = next with { SettledChecks = current.Checks };
        return new DiffResult(next, changes, quiet);
    }

    static EventStatus ChecksStatus(string checks) => checks switch
    {
        ChecksState.Passing => EventStatus.Success,
        ChecksState.Failing => EventStatus.Failure,
        _ => EventStatus.Pending,
    };
}
