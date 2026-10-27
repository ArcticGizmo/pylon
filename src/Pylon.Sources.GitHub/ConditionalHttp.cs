using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

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

    public int Requests { get; private set; }

    public int NotModified { get; private set; }

    /// <summary>Every URL fetched in this poll and how it was answered, for the payload view.</summary>
    public List<string> Log { get; } = [];

    public void ResetCounters()
    {
        Requests = 0;
        NotModified = 0;
        Log.Clear();
    }

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_cache.TryGetValue(path, out var cached))
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        Requests++;
        using var response = await http.SendAsync(request, cancellationToken);
        CheckThrottle(response);

        if (response.StatusCode == HttpStatusCode.NotModified && cached.Body is not null)
        {
            NotModified++;
            Log.Add($"304 {path}");
            return cached.Body;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Log.Add($"{(int)response.StatusCode} {path}");
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.Headers.TryGetValues("Postern-Error", out var code) ? $"Postern: {code.First()}" : $"HTTP {(int)response.StatusCode}";
            throw new GitHubRequestException($"GET {path} failed ({reason}).", response.StatusCode);
        }

        if (response.Headers.ETag?.ToString() is { Length: > 0 } etag)
        {
            if (_cache.Count >= MaxEntries && !_cache.ContainsKey(path))
                _cache.Clear();
            _cache[path] = (etag, body);
        }
        return body;
    }

    void CheckThrottle(HttpResponseMessage response)
    {
        var remaining = Header(response, "Postern-RateLimit-Remaining") ?? Header(response, "x-ratelimit-remaining");
        var reset = ResetTime(response);
        if (response.StatusCode is HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.Forbidden && remaining == "0"))
            throw new GitHubThrottledException("GitHub rate limit reached.", RetryAfter(response, reset));
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.RetryAfter is not null)
            throw new GitHubThrottledException("GitHub secondary rate limit.", RetryAfter(response, reset));
        if (int.TryParse(remaining, CultureInfo.InvariantCulture, out var left) && left < ReserveRequests && response.StatusCode != HttpStatusCode.NotModified)
            throw new GitHubThrottledException($"Only {left} GitHub requests left; waiting for the reset.", RetryAfter(response, reset));
    }

    TimeSpan RetryAfter(HttpResponseMessage response, DateTimeOffset? reset)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (reset is { } at && at > time.GetUtcNow())
            return at - time.GetUtcNow();
        return TimeSpan.FromMinutes(1);
    }

    static DateTimeOffset? ResetTime(HttpResponseMessage response)
    {
        if (Header(response, "Postern-RateLimit-Reset") is { } iso && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return at;
        if (long.TryParse(Header(response, "x-ratelimit-reset"), CultureInfo.InvariantCulture, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
