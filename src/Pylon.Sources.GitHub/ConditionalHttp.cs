using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Pylon.Core.Monitors;

namespace Pylon.Sources.GitHub;

/// <summary>GitHub (or Postern on its behalf) said to slow down or stop; the scheduler backs off until <see cref="RetryAfter"/>.</summary>
public sealed class GitHubThrottledException(string message, TimeSpan retryAfter) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>A non-success answer that isn't throttling: bad scope, missing repo, Postern refusing.</summary>
public sealed class GitHubRequestException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// GETs with <c>If-None-Match</c>, remembering each URL's ETag and body, so a 304 (which costs no rate limit) answers
/// with the body from last time. In memory only: after a restart the first poll of each URL costs one request.
/// </summary>
public sealed class ConditionalHttp(HttpClient http, TimeProvider time)
{
    /// <summary>Below this many requests left, stop until the reset rather than run the budget dry for other apps.</summary>
    public const int ReserveRequests = 100;

    const int MaxEntries = 2000;

    readonly Dictionary<string, (string ETag, string Body)> _cache = new(StringComparer.Ordinal);

    public int Requests => Calls.Count;

    public int NotModified => Calls.Count(c => c.NotModified);

    /// <summary>Every call made in this poll and how it was answered, for the journal and the payload view.</summary>
    public List<RequestTrace> Calls { get; } = [];

    public void ResetCounters() => Calls.Clear();

    public Task<string> GetAsync(string path, CancellationToken cancellationToken) => SendAsync(path, conditional: true, null, cancellationToken);

    /// <summary>
    /// The same GET without <c>If-None-Match</c>, so GitHub has to send the body. Costs a request; only the assumption
    /// checks use it, to see whether a 304 hid a change.
    /// </summary>
    public Task<string> GetFreshAsync(string path, string purpose, CancellationToken cancellationToken) => SendAsync(path, conditional: false, purpose, cancellationToken);

    /// <summary>The body last served for <paramref name="path"/>, if there is one.</summary>
    public string? Cached(string path) => _cache.TryGetValue(path, out var cached) ? cached.Body : null;

    async Task<string> SendAsync(string path, bool conditional, string? purpose, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        var sentETag = conditional && _cache.TryGetValue(path, out var cached) ? cached.ETag : null;
        if (sentETag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", sentETag);

        var started = time.GetTimestamp();
        using var response = await http.SendAsync(request, cancellationToken);
        var notModified = response.StatusCode == HttpStatusCode.NotModified && sentETag is not null;
        var body = notModified ? null : await response.Content.ReadAsStringAsync(cancellationToken);
        var rate = ReadRateLimit(response);
        var previous = Calls.LastOrDefault(c => c.RateLimit?.Remaining is not null)?.RateLimit;
        Calls.Add(new RequestTrace
        {
            Path = path,
            Status = (int)response.StatusCode,
            IfNoneMatch = sentETag,
            ETag = response.Headers.ETag?.ToString(),
            Milliseconds = (long)time.GetElapsedTime(started).TotalMilliseconds,
            Bytes = body?.Length ?? 0,
            RateLimit = rate,
            RateCost = previous?.Remaining is { } before && rate?.Remaining is { } after && previous.Reset == rate.Reset ? before - after : null,
            PosternError = Header(response, "Postern-Error"),
            Purpose = purpose,
            Body = body,
        });
        CheckThrottle(response, rate);

        if (notModified)
            return _cache[path].Body;

        if (!response.IsSuccessStatusCode)
        {
            var reason = Header(response, "Postern-Error") is { } code ? $"Postern: {code}" : $"HTTP {(int)response.StatusCode}";
            throw new GitHubRequestException($"GET {path} failed ({reason}).", response.StatusCode);
        }

        if (response.Headers.ETag?.ToString() is { Length: > 0 } etag)
        {
            if (_cache.Count >= MaxEntries && !_cache.ContainsKey(path))
                _cache.Clear();
            _cache[path] = (etag, body!);
        }
        return body!;
    }

    void CheckThrottle(HttpResponseMessage response, RateLimit? rate)
    {
        var remaining = rate?.Remaining;
        if (response.StatusCode is HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.Forbidden && remaining == 0))
            throw new GitHubThrottledException("GitHub rate limit reached.", RetryAfter(response, rate?.Reset));
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.RetryAfter is not null)
            throw new GitHubThrottledException("GitHub secondary rate limit.", RetryAfter(response, rate?.Reset));
        if (remaining is { } left && left < ReserveRequests && response.StatusCode != HttpStatusCode.NotModified)
            throw new GitHubThrottledException($"Only {left} GitHub requests left; waiting for the reset.", RetryAfter(response, rate?.Reset));
    }

    TimeSpan RetryAfter(HttpResponseMessage response, DateTimeOffset? reset)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (reset is { } at && at > time.GetUtcNow())
            return at - time.GetUtcNow();
        return TimeSpan.FromMinutes(1);
    }

    /// <summary>Postern's headers when it sends them (it tracks the budget across every app), else GitHub's own.</summary>
    static RateLimit? ReadRateLimit(HttpResponseMessage response)
    {
        var remaining = Int(Header(response, "Postern-RateLimit-Remaining") ?? Header(response, "x-ratelimit-remaining"));
        var limit = Int(Header(response, "Postern-RateLimit-Limit") ?? Header(response, "x-ratelimit-limit"));
        var used = Int(Header(response, "x-ratelimit-used"));
        DateTimeOffset? reset = null;
        if (Header(response, "Postern-RateLimit-Reset") is { } iso && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            reset = at;
        else if (long.TryParse(Header(response, "x-ratelimit-reset"), CultureInfo.InvariantCulture, out var unix))
            reset = DateTimeOffset.FromUnixTimeSeconds(unix);
        var resource = Header(response, "x-ratelimit-resource");
        return remaining is null && limit is null && reset is null ? null : new RateLimit(limit, remaining, used, reset, resource);
    }

    static int? Int(string? value) => int.TryParse(value, CultureInfo.InvariantCulture, out var n) ? n : null;

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
