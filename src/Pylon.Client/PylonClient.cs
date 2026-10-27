using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pylon.Core.Events;

namespace Pylon.Client;

/// <summary>Which events to subscribe to: topic patterns (<c>github/pr/org/*/*</c>) and optional type patterns (<c>github.pr.*</c>).</summary>
public sealed record SubscriptionRequest(IReadOnlyList<string> Topics, IReadOnlyList<string>? Types = null);

/// <summary>
/// One app's connection to Pylon.
/// <code>
/// using var pylon = new PylonClient();
/// await pylon.RunAsync(new(["github/pr/org/*/*"], ["github.pr.*"]),
///     onEvent: (e, ct) => RefetchAsync(e.ResourceId, ct),
///     onResync: (reason, ct) => RefreshEverythingAsync(ct),
///     cancellationToken);
/// </code>
/// <see cref="RunAsync"/> follows the consumer contract for you: a full refresh on connect and on every
/// <c>resync</c>, and <c>since=</c> on reconnect. Keep your own slow fallback refresh as well.
/// </summary>
public sealed class PylonClient : IDisposable
{
    readonly Lazy<HttpClient> _http;

    public PylonClient(PylonClientOptions? options = null)
    {
        Options = options ?? new PylonClientOptions();
        _http = new Lazy<HttpClient>(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Pylon is Windows-only.");
            return new HttpClient(PipeConnector.CreateHandler(Options), disposeHandler: true)
            {
                BaseAddress = new Uri("http://pylon/_pylon/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        });
    }

    public PylonClientOptions Options { get; }

    public bool IsAvailable => OperatingSystem.IsWindows() && PipeConnector.PipeExists(Options.PipeName);

    /// <summary>One connection's worth of events. Ends when Pylon closes the stream (a restart, or you were too slow).</summary>
    public async IAsyncEnumerable<PylonEvent> SubscribeAsync(SubscriptionRequest request, long? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("events?");
        foreach (var topic in request.Topics)
            query.Append("topic=").Append(Uri.EscapeDataString(topic)).Append('&');
        foreach (var type in request.Types ?? [])
            query.Append("type=").Append(Uri.EscapeDataString(type)).Append('&');
        if (since is { } from)
            query.Append("since=").Append(from);

        using var message = new HttpRequestMessage(HttpMethod.Get, query.ToString().TrimEnd('&', '?'));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.Value.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Pylon refused the subscription ({(int)response.StatusCode}): {detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var parsed = JsonSerializer.Deserialize<PylonEvent>(data.ToString(), PylonEvent.Json);
                    data.Clear();
                    if (parsed is not null)
                        yield return parsed;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line.AsSpan(5).TrimStart(' '));
        }
    }

    /// <summary>
    /// Subscribes until cancelled, reconnecting with <c>since=</c> after the last event seen. <paramref name="onResync"/>
    /// runs once before the first connect and for every <c>resync</c> event.
    /// </summary>
    public async Task RunAsync(SubscriptionRequest request, Func<PylonEvent, CancellationToken, Task> onEvent,
        Func<string, CancellationToken, Task> onResync, CancellationToken cancellationToken, Action<Exception>? onError = null)
    {
        long? last = null;
        var delay = TimeSpan.FromMilliseconds(500);
        var refreshed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!refreshed)
                {
                    await onResync("connect", cancellationToken);
                    refreshed = true;
                }
                await foreach (var e in SubscribeAsync(request, last, cancellationToken))
                {
                    delay = TimeSpan.FromMilliseconds(500);
                    last = e.Sequence;
                    if (e.Kind == EventKind.Resync)
                        await onResync(e.Hint?.GetValueOrDefault("reason") ?? "resync", cancellationToken);
                    else
                        await onEvent(e, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or PylonUnavailableException)
            {
                // No refresh here: reconnecting with since= gets a replay, or a resync if the gap can't be replayed.
                onError?.Invoke(ex);
            }
            await Task.Delay(delay, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    public async Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken = default) =>
        JsonNode.Parse(await _http.Value.GetStringAsync("status", cancellationToken))?.AsObject() ?? throw new InvalidDataException("Empty status.");

    /// <summary>Dev builds only: a GitHub webhook body, normalised the same way push mode will be.</summary>
    public Task<JsonObject> IngestGitHubAsync(string githubEvent, string body, string? monitorId = null, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "dev/ingest/github" + (monitorId is null ? "" : $"?monitor={Uri.EscapeDataString(monitorId)}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", githubEvent);
        return SendDevAsync(request, cancellationToken);
    }

    /// <summary>Dev builds only: a raw event (<c>source</c>, <c>topic</c>, <c>type</c>, <c>summary</c>, ...).</summary>
    public Task<JsonObject> EmitAsync(string json, CancellationToken cancellationToken = default) =>
        SendDevAsync(new HttpRequestMessage(HttpMethod.Post, "dev/emit") { Content = new StringContent(json, Encoding.UTF8, "application/json") }, cancellationToken);

    async Task<JsonObject> SendDevAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await _http.Value.SendAsync(request, cancellationToken))
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !response.Headers.Contains("Pylon-Error"))
                throw new InvalidOperationException("Pylon's dev ingest is off. Run a Debug build, or start Pylon with --dev-ingest.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Pylon said {(int)response.StatusCode}: {text}");
            return JsonNode.Parse(text)?.AsObject() ?? [];
        }
    }

    public void Dispose()
    {
        if (_http.IsValueCreated)
            _http.Value.Dispose();
    }
}
