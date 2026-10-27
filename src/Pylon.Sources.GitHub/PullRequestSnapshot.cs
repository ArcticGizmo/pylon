namespace Pylon.Sources.GitHub;

public static class PrState
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Merged = "merged";
}

public static class ChecksState
{
    public const string None = "none";
    public const string Pending = "pending";
    public const string Passing = "passing";
    public const string Failing = "failing";

    public static bool IsSettled(string state) => state is Passing or Failing;
}

public sealed record ReviewSnapshot(long Id, string Author, bool AuthorIsBot, string State);

/// <summary>
/// Just the fields that decide whether a pull request changed in a way a monitor reports. Anything else GitHub
/// returns (comment counts, mergeability, labels) is deliberately left out so it can't cause events.
/// </summary>
public sealed record PullRequestSnapshot
{
    public required string Repo { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public bool AuthorIsBot { get; init; }
    public bool Draft { get; init; }
    public required string State { get; init; }
    public required string HeadSha { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<string> RequestedReviewers { get; init; } = [];

    /// <summary>Null until fetched: reviews are only fetched for open PRs whose <see cref="UpdatedAt"/> moved.</summary>
    public IReadOnlyList<ReviewSnapshot>? Reviews { get; init; }

    public string Checks { get; init; } = ChecksState.None;

    /// <summary>
    /// The last passing/failing result reported for <see cref="HeadSha"/>, so re-running checks that end the same
    /// way doesn't report again. Cleared when the head moves.
    /// </summary>
    public string? SettledChecks { get; init; }

    public string Reference => $"{Repo}#{Number}";
}
