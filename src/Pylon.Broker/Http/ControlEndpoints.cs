using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Pylon.Core.Events;
using Pylon.Sources.GitHub;

namespace Pylon.Broker.Http;

/// <summary>
/// <c>/_pylon/status</c>, and in dev only the ingest endpoints the test bench and <c>pylon-cli emit</c> use. Ingest
/// is off in a normal run: anything that can write to the stream could make the widget show events that never
/// happened.
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(WebApplication app, PylonRuntime runtime)
    {
        app.MapGet("/_pylon/status", () => Results.Json(new
        {
            version = PylonRuntime.Version,
            pipe = runtime.Options.PipeName,
            processId = Environment.ProcessId,
            latestSequence = runtime.Store.LatestSequence(),
            subscribers = runtime.Hub.SubscriberCount,
            devIngest = runtime.Options.DevIngest,
            postern = runtime.Postern?.State.ToString(),
            monitors = runtime.Monitors.Status().Select(s => new
            {
                s.Id,
                s.Name,
                s.Source,
                state = s.State.ToString(),
                s.Detail,
                s.LastPollAt,
                s.NextPollAt,
                s.LastEventAt,
                s.Requests,
                s.NotModified,
                s.Events,
            }),
        }, PylonEvent.Json));

        app.MapGet("/_pylon/events", (HttpContext context) => EventsEndpoint.Handle(context, runtime.Hub));

        if (!runtime.Options.DevIngest)
            return;

        // POST /_pylon/dev/ingest/github with X-GitHub-Event: pull_request and a webhook body.
        app.MapPost("/_pylon/dev/ingest/{source}", async (HttpContext context, string source) =>
        {
            if (source != GitHubSource.SourceName)
            {
                await PylonErrors.Write(context, StatusCodes.Status404NotFound, "unknown-source", $"No webhook normaliser for '{source}'.");
                return;
            }
            var githubEvent = context.Request.Headers["X-GitHub-Event"].FirstOrDefault();
            if (string.IsNullOrEmpty(githubEvent))
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "missing-event", "Send the webhook's event name in X-GitHub-Event.");
                return;
            }
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            IReadOnlyList<EventDraft> drafts;
            try
            {
                drafts = WebhookNormaliser.Normalise(githubEvent, body, EventVia.Test, context.Request.Query["monitor"].FirstOrDefault());
            }
            catch (JsonException ex)
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-json", ex.Message);
                return;
            }
            var records = runtime.Hub.Publish(drafts);
            await context.Response.WriteAsJsonAsync(new { sequences = records.Select(r => r.Sequence), types = records.Select(r => r.Type) });
        });

        // POST /_pylon/dev/emit with a raw event: {source, topic, type, kind?, summary, status?, hint?, payload?}
        app.MapPost("/_pylon/dev/emit", async (HttpContext context) =>
        {
            JsonObject? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException ex)
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-json", ex.Message);
                return;
            }
            if (body is null || Str(body, "source") is not { } source || Str(body, "topic") is not { } topic || Str(body, "type") is not { } type)
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-event", "An event needs source, topic and type.");
                return;
            }
            EventDraft draft;
            try
            {
                draft = new EventDraft
                {
                    Source = source,
                    Topic = topic,
                    Type = type,
                    Kind = Str(body, "kind") is { } kind ? Enum.Parse<EventKind>(kind, ignoreCase: true) : EventKind.Changed,
                    MonitorId = Str(body, "monitorId"),
                    ResourceId = Str(body, "resourceId"),
                    Summary = Str(body, "summary") ?? $"{type} on {topic}",
                    Status = Str(body, "status") is { } status ? Enum.Parse<EventStatus>(status, ignoreCase: true) : EventStatus.Info,
                    Hint = body["hint"] is JsonObject hint ? hint.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : null,
                    Via = EventVia.Test,
                    Payload = body["payload"] is { } payload ? Payload.Json(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) : null,
                };
            }
            catch (ArgumentException ex)
            {
                await PylonErrors.Write(context, StatusCodes.Status400BadRequest, "bad-event", ex.Message);
                return;
            }
            var records = runtime.Hub.Publish([draft]);
            await context.Response.WriteAsJsonAsync(new { sequences = records.Select(r => r.Sequence) });
        });
    }

    static string? Str(JsonObject body, string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
