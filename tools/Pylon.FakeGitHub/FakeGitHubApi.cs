using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Pylon.FakeGitHub;

/// <summary>One request the fake answered. <see cref="Authorized"/> shows Postern injected a credential on the way.</summary>
public sealed record FakeRequest(DateTimeOffset At, string Path, int Status, bool Authorized = false);

/// <summary>Serves <see cref="FakeGitHubState"/> as GitHub's REST API: ETags on every GET, 304s that cost nothing.</summary>
public static class FakeGitHubApi
{
    public static void Map(IEndpointRouteBuilder routes, FakeGitHubState state, Action<FakeRequest>? log = null)
    {
        IResult Json(HttpContext context, string body, bool list = false)
        {
            var path = context.Request.Path + context.Request.QueryString;
            var etag = list ? state.ListETag(path, body) : FakeGitHubState.ETag(body);
            var notModified = context.Request.Headers.IfNoneMatch.ToString() == etag;
            var remaining = state.Spend(free: notModified);
            var headers = context.Response.Headers;
            headers.ETag = etag;
            headers["x-ratelimit-limit"] = "5000";
            headers["x-ratelimit-remaining"] = remaining.ToString();
            // One fixed window per hour, like GitHub's, so calls in the same poll can be compared.
            var now = DateTimeOffset.UtcNow;
            headers["x-ratelimit-reset"] = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1).ToUnixTimeSeconds().ToString();
            headers["x-ratelimit-resource"] = "core";
            var status = notModified ? (int)HttpStatusCode.NotModified : 200;
            log?.Invoke(new FakeRequest(DateTimeOffset.Now, path, status, context.Request.Headers.Authorization.Count > 0));
            return notModified ? Results.StatusCode(304) : Results.Text(body, "application/json");
        }

        routes.MapGet("/user", (HttpContext c) => Json(c, """{"login":"fake-user","type":"User"}"""));

        routes.MapGet("/repos/{owner}/{repo}/pulls", (HttpContext c, string owner, string repo) =>
            Json(c, FakeGitHubJson.Pulls(state.Pulls($"{owner}/{repo}")).ToJsonString(), list: true));

        routes.MapGet("/repos/{owner}/{repo}/pulls/{number:int}/reviews", (HttpContext c, string owner, string repo, int number) =>
            state.Find($"{owner}/{repo}", number) is { } pr ? Json(c, FakeGitHubJson.Reviews(pr).ToJsonString()) : NotFound(c, log));

        routes.MapGet("/repos/{owner}/{repo}/commits/{sha}/check-runs", (HttpContext c, string owner, string repo, string sha) =>
            Json(c, FakeGitHubJson.CheckRuns(state.Pulls($"{owner}/{repo}").FirstOrDefault(p => p.HeadSha == sha)).ToJsonString()));

        routes.MapGet("/repos/{owner}/{repo}/commits/{sha}/status", (HttpContext c) => Json(c, FakeGitHubJson.Status().ToJsonString()));
    }

    static IResult NotFound(HttpContext context, Action<FakeRequest>? log)
    {
        log?.Invoke(new FakeRequest(DateTimeOffset.Now, context.Request.Path + context.Request.QueryString, 404));
        return Results.Json(new { message = "Not Found" }, statusCode: 404);
    }

    /// <summary>Hosts the fake on a loopback port (0 picks a free one). Returns the app and its base address.</summary>
    public static async Task<(WebApplication App, Uri BaseAddress)> StartAsync(FakeGitHubState state, int port = 0, Action<FakeRequest>? log = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        Map(app, state, log);
        await app.StartAsync();
        var address = app.Urls.First();
        return (app, new Uri(address.EndsWith('/') ? address : address + "/"));
    }
}
