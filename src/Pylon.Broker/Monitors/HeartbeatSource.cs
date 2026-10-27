using System.Globalization;
using Pylon.Core.Events;
using Pylon.Core.Monitors;

namespace Pylon.Broker.Monitors;

/// <summary>
/// One event per poll, calling nothing. Proves the whole path (scheduler, log, subscribers, widget) with no external
/// system and no Postern, and gives a quiet stream something to show.
/// </summary>
public sealed class HeartbeatSource : ISource
{
    public const string SourceName = "heartbeat";
    public const string Tick = "tick";

    public string Name => SourceName;

    public string DisplayName => "Heartbeat";

    public string Icon => "heartbeat";

    public bool UsesPostern => false;

    public IReadOnlyList<SourceEvent> Events { get; } = [new(Tick, "Tick", "One event every poll interval.")];

    public IReadOnlyList<string> Validate(MonitorDefinition monitor) => [];

    public IReadOnlyList<string> ScopesFor(MonitorDefinition monitor) => [];

    public IMonitorRunner Create(MonitorContext context) => new Runner(context);

    sealed class Runner(MonitorContext context) : IMonitorRunner
    {
        public Task<PollResult> PollAsync(CancellationToken cancellationToken)
        {
            var count = long.TryParse(context.State.Get("count"), CultureInfo.InvariantCulture, out var n) ? n + 1 : 1;
            context.State.Set("count", count.ToString(CultureInfo.InvariantCulture));
            var id = context.Definition.Id;
            var draft = new EventDraft
            {
                Source = SourceName,
                MonitorId = id,
                Topic = $"{SourceName}/{id}",
                Type = $"{SourceName}.{Tick}",
                Summary = $"{context.Definition.Name}: beat {count}",
                Status = EventStatus.Neutral,
                Hint = new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) },
                Payload = Payload.Json($$"""{ "monitor": "{{id}}", "count": {{count}}, "at": "{{context.Time.GetUtcNow():O}}" }"""),
            };
            return Task.FromResult(new PollResult(0, 0, [draft]));
        }
    }
}
