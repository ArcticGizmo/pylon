namespace Pylon.Core.Events;

/// <summary>What Pylon observed, kept for the stream widget (D2). Never sent to subscribers.</summary>
public sealed record Payload(string ContentType, string Content)
{
    public static Payload Json(string content) => new("application/json", content);
}

/// <summary>An event a source has produced but Pylon hasn't sequenced yet.</summary>
public sealed record EventDraft
{
    public required string Source { get; init; }
    public required string Topic { get; init; }
    public required string Type { get; init; }
    public EventKind Kind { get; init; } = EventKind.Changed;
    public string? MonitorId { get; init; }
    public string? ResourceId { get; init; }
    public IReadOnlyDictionary<string, string>? Hint { get; init; }
    public required string Summary { get; init; }
    public EventStatus Status { get; init; } = EventStatus.Info;
    public EventVia Via { get; init; } = EventVia.Poll;
    public Payload? Payload { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }

    /// <summary>Tells subscribers to refresh everything from <paramref name="source"/>: Pylon may have missed changes.</summary>
    public static EventDraft Resync(string source, string reason, string? monitorId = null) => new()
    {
        Source = source,
        Topic = source,
        Type = $"{source}.resync",
        Kind = EventKind.Resync,
        MonitorId = monitorId,
        Summary = $"{source}: resync ({reason})",
        Status = EventStatus.Neutral,
        Via = EventVia.Pylon,
        Hint = new Dictionary<string, string> { ["reason"] = reason },
    };
}

/// <summary>A sequenced event plus what the stream widget shows for it.</summary>
public sealed record EventRecord(
    long Sequence,
    string Source,
    string? MonitorId,
    string Topic,
    string Type,
    EventKind Kind,
    string? ResourceId,
    IReadOnlyDictionary<string, string>? Hint,
    DateTimeOffset ObservedAtUtc,
    string Summary,
    EventStatus Status,
    EventVia Via,
    bool HasPayload)
{
    public static EventRecord From(long sequence, EventDraft draft, DateTimeOffset now) => new(
        sequence, draft.Source, draft.MonitorId, draft.Topic, draft.Type, draft.Kind, draft.ResourceId, draft.Hint,
        draft.ObservedAtUtc ?? now, draft.Summary, draft.Status, draft.Via, draft.Payload is not null);

    public PylonEvent ToContract() => new(Sequence, Source, MonitorId, Topic, Type, Kind, ResourceId, Hint, ObservedAtUtc);
}
