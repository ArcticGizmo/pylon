namespace Pylon.Core.Events;

/// <summary>
/// What the stream widget shows. Empty sets mean "everything"; text must match every word somewhere in the summary,
/// topic or type, so "perch approved" finds approvals on perch without a query language. Lives here rather than in the
/// app so it's tested without a UI.
/// </summary>
public sealed record StreamFilter
{
    /// <summary>Monitor ids to show. <see cref="NoMonitor"/> stands for events with none (test bench, <c>pylon-cli emit</c>).</summary>
    public IReadOnlySet<string> Monitors { get; init; } = new HashSet<string>();

    public IReadOnlySet<EventStatus> Statuses { get; init; } = new HashSet<EventStatus>();

    public string Text { get; init; } = "";

    public const string NoMonitor = "";

    public bool IsEmpty => Monitors.Count == 0 && Statuses.Count == 0 && string.IsNullOrWhiteSpace(Text);

    public bool Matches(EventRecord record)
    {
        if (Monitors.Count > 0 && !Monitors.Contains(record.MonitorId ?? NoMonitor))
            return false;
        if (Statuses.Count > 0 && !Statuses.Contains(record.Status))
            return false;
        foreach (var word in Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!record.Summary.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !record.Topic.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !record.Type.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
