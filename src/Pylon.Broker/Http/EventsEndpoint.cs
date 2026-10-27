using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pylon.Broker.Events;
using Pylon.Core.Events;
using Pylon.Core.Topics;

namespace Pylon.Broker.Http;

/// <summary>
/// <c>GET /_pylon/events?topic=github/pr/org/*/*&amp;type=github.pr.*&amp;since=18000</c> as server-sent events (D3).
/// <list type="bullet">
/// <item><c>Last-Event-ID</c> (what a reconnecting SSE client sends) wins over <c>since</c>.</item>
/// <item>No <c>since</c>: live events only. The consumer contract says to refresh fully on connect anyway.</item>
/// <item><c>since</c> still in the log: everything after it, then live, with no gap or duplicate between the two.</item>
/// <item><c>since</c> pruned, or from a different log (ahead of it): a <c>resync</c> first, then live.</item>
/// <item>A subscriber too slow to keep up gets a <c>resync</c> and the stream ends; it reconnects with <c>since</c>.</item>
/// </list>
/// </summary>
internal static class EventsEndpoint
{
    public const int ReplayBatch = 500;

    public static TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    public static async Task Handle(HttpContext context, EventHub hub)
    {
        EventFilter filter;
        try
        {
            filter = EventFilter.Parse(context.Request.Query["topic"].OfType<string>(), context.Request.Query["type"].OfType<string>());
        }
        catch (FormatException ex)
        {
            await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-filter", ex.Message);
            return;
        }

        long? since = null;
        var sinceText = context.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? context.Request.Query["since"].FirstOrDefault();
        if (!string.IsNullOrEmpty(sinceText))
        {
            if (!long.TryParse(sinceText, out var parsed) || parsed < 0)
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-since", $"'{sinceText}' isn't a sequence number.");
                return;
            }
            since = parsed;
        }

        var cancellationToken = context.RequestAborted;
        var (subscription, liveAfter) = hub.Subscribe(filter);
        using (subscription)
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            await response.WriteAsync($": pylon {liveAfter}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            var lastSent = since ?? liveAfter;
            if (since is { } from)
                lastSent = await Replay(response, hub, filter, from, liveAfter, cancellationToken);

            try
            {
                lastSent = await Live(response, subscription, lastSent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (subscription.Overflowed)
                await Write(response, Resync(lastSent, "slow-consumer"), cancellationToken);
        }
    }

    static async Task<long> Replay(HttpResponse response, EventHub hub, EventFilter filter, long since, long liveAfter, CancellationToken cancellationToken)
    {
        var oldest = hub.Store.OldestSequence();
        var gap = since > liveAfter || (since < liveAfter && (oldest is null || since + 1 < oldest));
        if (gap)
        {
            await Write(response, Resync(liveAfter, since > liveAfter ? "unknown-sequence" : "replay-gap"), cancellationToken);
            return liveAfter;
        }

        var cursor = since;
        while (cursor < liveAfter)
        {
            var batch = hub.Store.ReadAfter(cursor, ReplayBatch);
            if (batch.Count == 0)
                break;
            foreach (var record in batch)
            {
                if (record.Sequence > liveAfter)
                    return liveAfter;
                if (filter.Matches(record))
                    await Write(response, record.ToContract(), cancellationToken, flush: false);
                cursor = record.Sequence;
            }
            await response.Body.FlushAsync(cancellationToken);
        }
        return liveAfter;
    }

    static async Task<long> Live(HttpResponse response, Subscription subscription, long lastSent, CancellationToken cancellationToken)
    {
        var reader = subscription.Reader;
        var wait = reader.WaitToReadAsync(cancellationToken).AsTask();
        while (true)
        {
            using (var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(KeepAlive, tick.Token);
                var winner = await Task.WhenAny(wait, delay);
                await tick.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (winner != wait)
                {
                    // Keeps idle connections from looking dead, and finds a vanished client within one interval.
                    await response.WriteAsync(": keepalive\n\n", cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    continue;
                }
            }
            if (!await wait)
                break;
            while (reader.TryRead(out var record))
            {
                if (record.Sequence <= lastSent)
                    continue;
                await Write(response, record.ToContract(), cancellationToken, flush: false);
                lastSent = record.Sequence;
            }
            await response.Body.FlushAsync(cancellationToken);
            wait = reader.WaitToReadAsync(cancellationToken).AsTask();
        }
        return lastSent;
    }

    static PylonEvent Resync(long sequence, string reason) => new(
        sequence, EventFilter.GlobalSource, null, EventFilter.GlobalSource, "pylon.resync", EventKind.Resync, null,
        new Dictionary<string, string> { ["reason"] = reason }, DateTimeOffset.UtcNow);

    static async Task Write(HttpResponse response, PylonEvent e, CancellationToken cancellationToken, bool flush = true)
    {
        await response.WriteAsync($"id: {e.Sequence}\ndata: {JsonSerializer.Serialize(e, PylonEvent.Json)}\n\n", cancellationToken);
        if (flush)
            await response.Body.FlushAsync(cancellationToken);
    }
}
