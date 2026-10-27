using Pylon.Core.Events;
using Pylon.Sources.GitHub;

namespace Pylon.Tests;

public class PullRequestDiffTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    static PullRequestSnapshot Pr(Func<PullRequestSnapshot, PullRequestSnapshot>? change = null)
    {
        var pr = new PullRequestSnapshot
        {
            Repo = "example/app",
            Number = 7,
            Title = "Add widget",
            Author = "author-a",
            State = PrState.Open,
            HeadSha = "aaa",
            UpdatedAt = T0,
            Url = "https://github.com/example/app/pull/7",
            Reviews = [],
        };
        return change is null ? pr : change(pr);
    }

    static IReadOnlyList<PrChange> Changes(PullRequestSnapshot? before, PullRequestSnapshot after, DiffOptions? options = null) =>
        PullRequestDiff.Apply(before, after, options ?? DiffOptions.Everything).Changes;

    static DiffOptions Options(bool drafts = true, bool bots = false, params string[] events) =>
        new(events.Length == 0 ? DiffOptions.Everything.Events : events.ToHashSet(), drafts, bots);

    [Fact]
    public void NewOpenPrIsOpened()
    {
        var change = Assert.Single(Changes(null, Pr()));
        Assert.Equal(GitHubEvents.Opened, change.Name);
        Assert.Equal(EventKind.Created, change.Kind);
        Assert.Contains("example/app#7 opened by author-a", change.Summary);
    }

    [Fact]
    public void NewClosedPrIsHistory() => Assert.Empty(Changes(null, Pr(p => p with { State = PrState.Merged })));

    [Fact]
    public void NothingChangedReportsNothing() => Assert.Empty(Changes(Pr(), Pr(p => p with { UpdatedAt = T0.AddMinutes(1), Title = "Renamed" })));

    [Fact]
    public void DraftsAreQuietUntilReady()
    {
        var options = Options(drafts: false);
        var draft = Pr(p => p with { Draft = true });
        Assert.Empty(Changes(null, draft, options));
        Assert.Empty(Changes(draft, draft with { RequestedReviewers = ["someone"] }, options));
        var ready = Assert.Single(Changes(draft, draft with { Draft = false }, options));
        Assert.Equal(GitHubEvents.ReadyForReview, ready.Name);
    }

    [Fact]
    public void EachNewReviewerIsAReviewRequest()
    {
        var before = Pr(p => p with { RequestedReviewers = ["one"] });
        var changes = Changes(before, before with { RequestedReviewers = ["one", "two", "team:core"] });
        Assert.Equal(["two", "team:core"], changes.Select(c => c.Hint["reviewer"]));
        Assert.All(changes, c => Assert.Equal(EventStatus.Attention, c.Status));
    }

    [Fact]
    public void ReviewsAreComparedById()
    {
        var before = Pr(p => p with { Reviews = [new(1, "r1", false, "COMMENTED")] });
        var after = before with
        {
            Reviews = [new(1, "r1", false, "COMMENTED"), new(2, "r2", false, "APPROVED"), new(3, "r3", false, "CHANGES_REQUESTED"), new(4, "r4", false, "DISMISSED")],
        };
        var changes = Changes(before, after);
        Assert.Equal([GitHubEvents.Approved, GitHubEvents.Reviewed], changes.Select(c => c.Name));
        Assert.Equal(EventStatus.Success, changes[0].Status);
        Assert.Equal("changes_requested", changes[1].Hint["state"]);
    }

    [Fact]
    public void FirstReviewFetchIsABaseline()
    {
        var before = Pr(p => p with { Reviews = null });
        Assert.Empty(Changes(before, before with { Reviews = [new(1, "r1", false, "APPROVED")] }));
    }

    [Fact]
    public void ReviewsNotRefetchedAreCarriedForward()
    {
        var before = Pr(p => p with { Reviews = [new(1, "r1", false, "APPROVED")] });
        var (next, changes) = PullRequestDiff.Apply(before, before with { Reviews = null }, DiffOptions.Everything);
        Assert.Empty(changes);
        Assert.Single(next.Reviews!);
    }

    [Fact]
    public void BotReviewsAndBotPrsAreIgnoredWhenAsked()
    {
        var options = Options(bots: true);
        var before = Pr();
        Assert.Empty(Changes(before, before with { Reviews = [new(9, "copilot[bot]", true, "COMMENTED")] }, options));
        Assert.Empty(Changes(null, Pr(p => p with { Author = "dependabot[bot]", AuthorIsBot = true }), options));
        Assert.Single(Changes(null, Pr(p => p with { Author = "dependabot[bot]", AuthorIsBot = true })));
    }

    [Fact]
    public void ChecksPendingIsReportedOnlyForANewHead()
    {
        var passing = Pr(p => p with { Checks = ChecksState.Passing, SettledChecks = ChecksState.Passing });

        var pushed = Assert.Single(Changes(passing, passing with { HeadSha = "bbb", Checks = ChecksState.Pending }));
        Assert.Equal("pending", pushed.Hint["checks"]);

        Assert.Empty(Changes(passing, passing with { Checks = ChecksState.Pending }));
    }

    [Fact]
    public void ReRunWithTheSameResultIsQuiet()
    {
        var passing = Pr(p => p with { Checks = ChecksState.Passing, SettledChecks = ChecksState.Passing });
        var (rerunning, _) = PullRequestDiff.Apply(passing, passing with { Checks = ChecksState.Pending }, DiffOptions.Everything);
        Assert.Empty(Changes(rerunning, rerunning with { Checks = ChecksState.Passing }));
    }

    [Fact]
    public void ChecksFlipIsReported()
    {
        var passing = Pr(p => p with { Checks = ChecksState.Passing, SettledChecks = ChecksState.Passing });
        var failing = Assert.Single(Changes(passing, passing with { Checks = ChecksState.Failing }));
        Assert.Equal(EventStatus.Failure, failing.Status);
    }

    [Fact]
    public void SettledResultOnNewHeadIsRemembered()
    {
        var before = Pr(p => p with { Checks = ChecksState.Pending });
        var (next, changes) = PullRequestDiff.Apply(before, before with { Checks = ChecksState.Failing }, DiffOptions.Everything);
        Assert.Single(changes);
        Assert.Equal(ChecksState.Failing, next.SettledChecks);
        Assert.Empty(Changes(next, next));
    }

    [Fact]
    public void MergedAndClosed()
    {
        var merged = Assert.Single(Changes(Pr(), Pr(p => p with { State = PrState.Merged })));
        Assert.Equal(EventStatus.Merged, merged.Status);

        var closed = Assert.Single(Changes(Pr(), Pr(p => p with { State = PrState.Closed })));
        Assert.Equal(EventKind.Removed, closed.Kind);
    }

    [Fact]
    public void SwitchedOffEventsAreNotReportedButStateMovesOn()
    {
        var options = Options(events: [GitHubEvents.Merged]);
        var before = Pr(p => p with { Reviews = [] });
        var (next, changes) = PullRequestDiff.Apply(before, before with { Reviews = [new(5, "r", false, "APPROVED")] }, options);
        Assert.Empty(changes);
        Assert.Single(next.Reviews!);
    }
}
