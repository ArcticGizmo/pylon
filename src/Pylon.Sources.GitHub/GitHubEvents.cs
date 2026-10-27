using Pylon.Core.Monitors;

namespace Pylon.Sources.GitHub;

/// <summary>The pull request changes a GitHub monitor can report. The event type is <c>github.pr.{name}</c>.</summary>
public static class GitHubEvents
{
    public const string Opened = "opened";
    public const string ReadyForReview = "ready_for_review";
    public const string ReviewRequested = "review_requested";
    public const string Reviewed = "reviewed";
    public const string Approved = "approved";
    public const string Checks = "checks";
    public const string Merged = "merged";
    public const string Closed = "closed";

    public const string IncludeDrafts = "includeDrafts";
    public const string IgnoreBots = "ignoreBots";

    public static readonly IReadOnlyList<SourceEvent> All =
    [
        new(Opened, "PR opened", "A new pull request (drafts only when 'include drafts' is on)."),
        new(ReadyForReview, "Ready for review", "A draft was marked ready for review."),
        new(ReviewRequested, "Review requested", "Someone (or a team) was asked to review."),
        new(Reviewed, "Reviewed", "A review was submitted that comments or requests changes."),
        new(Approved, "Approved", "A review was submitted that approves."),
        new(Checks, "Checks", "Checks on the head commit started (after a push), passed or failed."),
        new(Merged, "Merged", "The pull request was merged."),
        new(Closed, "Closed", "The pull request was closed without merging."),
    ];

    public static string Type(string name) => $"github.pr.{name}";

    public static string Topic(string repo, int number) => $"github/pr/{repo}/{number}";
}
