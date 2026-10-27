using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pylon.Core.Events;

public enum EventKind { Changed, Created, Removed, Resync }

/// <summary>The status dot in the stream widget. Presentation only: subscribers never see it.</summary>
public enum EventStatus { Info, Attention, Success, Failure, Pending, Merged, Neutral }

/// <summary>How Pylon learned about the change. <c>Test</c> is the dev ingest endpoint (the test bench, <c>pylon-cli emit</c>).</summary>
public enum EventVia { Poll, Push, Test, Pylon }

/// <summary>
/// What a subscriber receives: a hint that something changed, never the new state. See scope.md, "Event contract",
/// plus <see cref="Type"/> (D1) so subscribers and monitors can filter on what happened, not just where.
/// </summary>
public sealed record PylonEvent(
    long Sequence,
    string Source,
    string? MonitorId,
    string Topic,
    string Type,
    EventKind Kind,
    string? ResourceId,
    IReadOnlyDictionary<string, string>? Hint,
    DateTimeOffset ObservedAtUtc)
{
    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
