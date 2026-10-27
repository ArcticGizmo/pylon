using Pylon.Core.Events;

namespace Pylon.Core.Monitors;

/// <summary>Per-monitor memory between polls (fingerprints, cursors). Persisted, so a restart doesn't replay old changes.</summary>
public interface IMonitorState
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
    IReadOnlyList<string> Keys();
}

/// <summary>A change a source can report, shown as a checkbox on the Monitors page.</summary>
public sealed record SourceEvent(string Name, string Label, string Description);

/// <summary>What the scheduler needs from one poll, for the Monitors page and backoff.</summary>
public sealed record PollResult(int Requests, int NotModified, IReadOnlyList<EventDraft> Events, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static PollResult Failed(string error, int requests = 0, TimeSpan? retryAfter = null) => new(requests, 0, [], error, retryAfter);
}

/// <summary>Everything a running monitor is given. The HttpClient's base address is the connection root.</summary>
public sealed record MonitorContext(MonitorDefinition Definition, HttpClient Http, IMonitorState State, TimeProvider Time);

/// <summary>One monitor, polling. Implementations keep no state Pylon can't rebuild from <see cref="IMonitorState"/>.</summary>
public interface IMonitorRunner
{
    Task<PollResult> PollAsync(CancellationToken cancellationToken);
}

/// <summary>A kind of thing Pylon can watch (GitHub, the built-in heartbeat).</summary>
public interface ISource
{
    string Name { get; }

    string DisplayName { get; }

    /// <summary>The default icon key for new monitors of this source.</summary>
    string Icon { get; }

    /// <summary>False for sources that call nothing (the heartbeat), so the scheduler doesn't wait on Postern.</summary>
    bool UsesPostern { get; }

    IReadOnlyList<SourceEvent> Events { get; }

    /// <summary>Human-readable problems with a monitor's settings; empty when it's fine.</summary>
    IReadOnlyList<string> Validate(MonitorDefinition monitor);

    /// <summary>The Postern scopes this monitor needs, as <c>METHOD connection/path-glob</c>.</summary>
    IReadOnlyList<string> ScopesFor(MonitorDefinition monitor);

    IMonitorRunner Create(MonitorContext context);
}
