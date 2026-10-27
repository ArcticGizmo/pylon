using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Postern.Client;
using Pylon.Broker.Events;
using Pylon.Broker.Http;
using Pylon.Broker.Monitors;
using Pylon.Broker.Pipe;
using Pylon.Broker.Storage;
using Pylon.Core.Monitors;
using Pylon.Sources.GitHub;

namespace Pylon.Broker;

public sealed class PylonOptions
{
    public string PipeName { get; init; } = Profile.PipeName;
    public string DataDirectory { get; init; } = Profile.DataDirectory;

    /// <summary>Maps <c>/_pylon/dev/*</c>, so the test bench and <c>pylon-cli emit</c> can write to the stream.</summary>
    public bool DevIngest { get; init; }

    public bool PublishDiscovery { get; init; } = true;
    public string PosternPipeName { get; init; } = Profile.PosternPipeName;

    /// <summary>The name Pylon registers with in Postern, shown in its approval prompt and Apps page.</summary>
    public string PosternAppName { get; init; } = "pylon";

    public Retention Retention { get; init; } = new();
}

/// <summary>Swap-ins for tests and the snapshot tool. Anything left null gets the real implementation.</summary>
public sealed class PylonOverrides
{
    public TimeProvider? Time { get; init; }
    public Action<ILoggingBuilder>? Logging { get; init; }

    /// <summary>Monitors call through this instead of Postern (connection name in, client with its base address out).</summary>
    public Func<string, HttpClient>? MonitorHttp { get; init; }

    public IReadOnlyList<ISource>? Sources { get; init; }
}

/// <summary>
/// Pylon as an in-process service: the tray app (or a test) builds it, starts it and talks to its parts directly.
/// There's no admin channel over the pipe; editing monitors happens in-process only.
/// </summary>
public sealed class PylonRuntime : IAsyncDisposable
{
    public static string Version { get; } =
        typeof(PylonRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    readonly WebApplication _app;
    readonly CancellationTokenSource _prune = new();
    Task? _pruneTask;
    bool _started;

    PylonRuntime(PylonOptions options, PylonOverrides overrides)
    {
        Options = options;
        Time = overrides.Time ?? TimeProvider.System;
        Store = new EventStore(Path.Combine(options.DataDirectory, "pylon.db"), Time, options.Retention);
        Hub = new EventHub(Store);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = "Pylon" });
        builder.Logging.ClearProviders();
        overrides.Logging?.Invoke(builder.Logging);
        builder.WebHost.UseNamedPipes(o =>
        {
            o.CurrentUserOnly = false; // it would replace our descriptor; see PipeServerFactory
            o.CreateNamedPipeServerStream = PipeServerFactory.Create(PipeServerFactory.CurrentUserOnly());
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.ListenNamedPipe(options.PipeName);
        });
        _app = builder.Build();

        if (overrides.MonitorHttp is null)
            Postern = new PosternGateway(new PosternOptions { AppName = options.PosternAppName, PipeName = options.PosternPipeName });
        Monitors = new MonitorHost(overrides.Sources ?? [new GitHubSource(), new HeartbeatSource()], Hub, Postern, overrides.MonitorHttp,
            options.DataDirectory, Time, _app.Logger);

        ControlEndpoints.Map(_app, this);
    }

    public static PylonRuntime Create(PylonOptions options, PylonOverrides? overrides = null) => new(options, overrides ?? new PylonOverrides());

    public PylonOptions Options { get; }
    public TimeProvider Time { get; }
    public EventStore Store { get; }
    public EventHub Hub { get; }
    public MonitorHost Monitors { get; }

    /// <summary>Null when tests route monitors around Postern.</summary>
    public PosternGateway? Postern { get; }

    public ILogger Logger => _app.Logger;

    /// <summary>Starts listening and polling. Throws <see cref="PipeInUseException"/> if the name is already taken.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (Find<PipeInUseException>(ex) is { } inUse)
        {
            throw inUse;
        }
        _started = true;
        if (Options.PublishDiscovery)
            Discovery.Publish(Options.PipeName);
        Monitors.Start();
        _pruneTask = Task.Run(() => PruneLoop(_prune.Token));
        _app.Logger.LogInformation("Pylon {Version} listening on \\\\.\\pipe\\{Pipe} (pid {Pid}); latest sequence {Sequence}; dev ingest {DevIngest}",
            Version, Options.PipeName, Environment.ProcessId, Store.LatestSequence(), Options.DevIngest ? "on" : "off");
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        if (Options.PublishDiscovery)
            Discovery.Withdraw(Options.PipeName);
        await _prune.CancelAsync();
        if (_pruneTask is not null)
            await _pruneTask.ContinueWith(_ => { }, TaskScheduler.Default);
        await Monitors.DisposeAsync();
        Hub.CloseAll(); // ends open event streams, so Kestrel isn't left waiting on them
        await _app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _app.DisposeAsync();
        Postern?.Dispose();
        _prune.Dispose();
    }

    async Task PruneLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (events, payloads) = Store.Prune();
                if (events + payloads > 0)
                    _app.Logger.LogInformation("Pruned {Events} event(s) and {Payloads} payload(s)", events, payloads);
                await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _app.Logger.LogWarning(ex, "Pruning failed");
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
                return match;
            if (ex is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (Find<T>(inner) is { } found)
                        return found;
        }
        return null;
    }
}
