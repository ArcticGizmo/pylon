using System.Text.Json.Serialization;

namespace Pylon.Core.Monitors;

/// <summary>The rate-limit headers on one response, as GitHub (or Postern on its behalf) sent them.</summary>
public sealed record RateLimit(int? Limit, int? Remaining, int? Used, DateTimeOffset? Reset, string? Resource);

/// <summary>One HTTP call a poll made, for the monitor detail page. Nothing here is ever sent to subscribers.</summary>
public sealed record RequestTrace
{
    public string Method { get; init; } = "GET";
    public required string Path { get; init; }
    public int Status { get; init; }
    public string? IfNoneMatch { get; init; }
    public string? ETag { get; init; }
    public long Milliseconds { get; init; }
    public int Bytes { get; init; }
    public RateLimit? RateLimit { get; init; }

    /// <summary>
    /// How far the remaining rate limit fell since the call before it in the same poll. Calls a few milliseconds apart
    /// rarely share the window with another app, so a 304 with a cost of 1 is evidence that 304s aren't free (A5).
    /// </summary>
    public int? RateCost { get; init; }

    public string? PosternError { get; init; }

    /// <summary>Why the call was made when it isn't part of the normal strategy (an assumption check).</summary>
    public string? Purpose { get; init; }

    /// <summary>The response body for anything but a 304. Stored apart from the trace, cut, and kept for less time.</summary>
    [JsonIgnore]
    public string? Body { get; init; }

    public bool NotModified => Status == 304;
}

public enum NoteLevel { Info, Warning }

/// <summary>
/// Something worth a person's attention that isn't an event: a baseline, or evidence for or against one of the S1
/// assumptions (<see cref="Assumption"/> is its id in docs/implementation-plan.md, such as <c>A1</c>).
/// </summary>
public sealed record ObservationNote(NoteLevel Level, string Text, string? Assumption = null);

public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary>A change the rules recognised but didn't report, and why (switched off, a bot, a draft, a baseline).</summary>
public sealed record QuietChange(string Event, string Summary, string Reason);

/// <summary>What one poll noticed about one resource (a pull request): what moved, what was reported, what wasn't.</summary>
public sealed record Observation
{
    /// <summary>A short reference such as <c>org/api#101</c>.</summary>
    public required string Resource { get; init; }

    public string? Title { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<FieldChange> Changes { get; init; } = [];

    /// <summary>The event types this poll reported for the resource.</summary>
    public IReadOnlyList<string> Reported { get; init; } = [];

    public IReadOnlyList<QuietChange> Quiet { get; init; } = [];
    public IReadOnlyList<ObservationNote> Notes { get; init; } = [];
}

/// <summary>One thing a monitor is keeping track of, as its stored fingerprint describes it.</summary>
public sealed record WatchedItem(string Resource, string? Title, string? Url, IReadOnlyList<(string Field, string Value)> Fields);

public enum PollOutcome { Ok, Error, Throttled }

/// <summary>One poll as the journal keeps it: what was called, what was noticed, and which events came of it.</summary>
public sealed record PollRecord
{
    public long Id { get; init; }
    public required string MonitorId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public long Milliseconds { get; init; }
    public PollOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public int Requests { get; init; }
    public int NotModified { get; init; }
    public IReadOnlyList<RequestTrace> Calls { get; init; } = [];
    public IReadOnlyList<Observation> Observations { get; init; } = [];
    public IReadOnlyList<ObservationNote> Notes { get; init; } = [];
    public IReadOnlyList<long> Sequences { get; init; } = [];

    /// <summary>The last rate limit seen in this poll: what was left when it finished.</summary>
    public RateLimit? RateLimit => Calls.LastOrDefault(c => c.RateLimit?.Remaining is not null)?.RateLimit;

    public bool HasWarnings => Notes.Any(n => n.Level == NoteLevel.Warning) || Observations.Any(o => o.Notes.Any(n => n.Level == NoteLevel.Warning));
}
