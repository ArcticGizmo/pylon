using System.Text.Json;
using Pylon.Core.Events;

namespace Pylon.Sources.GitHub;

/// <summary>
/// Turns a GitHub webhook delivery (the <c>X-GitHub-Event</c> name plus its JSON body) into the same events the poll
/// path produces, so the stream widget and subscribers can't tell which way a change arrived except by
/// <see cref="EventDraft.Via"/>. Used by the dev ingest endpoint now and by push mode later.
/// <para>
/// No fingerprinting happens here: a webhook says exactly what happened. Unknown events and actions produce nothing
/// rather than an error, because GitHub sends many more than Pylon reports.
/// </para>
/// </summary>
public static class WebhookNormaliser
{
    public static IReadOnlyList<EventDraft> Normalise(string githubEvent, string body, EventVia via, string? monitorId = null)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var repo = Str(Prop(root, "repository"), "full_name");
        if (repo is null)
            return [];

        var drafts = new List<EventDraft>();
        void Add(int number, string? url, string name, EventKind kind, EventStatus status, string summary, Dictionary<string, string>? hint = null) =>
            drafts.Add(new EventDraft
            {
                Source = GitHubSource.SourceName,
                MonitorId = monitorId,
                Topic = GitHubEvents.Topic(repo, number),
                Type = GitHubEvents.Type(name),
                Kind = kind,
                ResourceId = url,
                Hint = hint,
                Summary = summary,
                Status = status,
                Via = via,
                Payload = Payload.Json(body),
            });

        var action = Str(root, "action");
        var sender = Str(Prop(root, "sender"), "login") ?? "someone";
        switch (githubEvent)
        {
            case "pull_request":
            {
                var pr = Prop(root, "pull_request");
                var number = Num(pr) ?? Num(root);
                if (number is not { } n)
                    break;
                var reference = $"{repo}#{n}";
                var title = Str(pr, "title") ?? "";
                var url = Str(pr, "html_url");
                switch (action)
                {
                    case "opened":
                        Add(n, url, GitHubEvents.Opened, EventKind.Created, EventStatus.Info, $"{reference} opened by {Str(Prop(pr, "user"), "login") ?? sender}: {title}",
                            new() { ["draft"] = Bool(pr, "draft") ? "true" : "false" });
                        break;
                    case "ready_for_review":
                        Add(n, url, GitHubEvents.ReadyForReview, EventKind.Changed, EventStatus.Info, $"{reference} ready for review: {title}");
                        break;
                    case "review_requested":
                        var reviewer = Str(Prop(root, "requested_reviewer"), "login") ?? (Str(Prop(root, "requested_team"), "slug") is { } team ? "team:" + team : "someone");
                        Add(n, url, GitHubEvents.ReviewRequested, EventKind.Changed, EventStatus.Attention, $"{reference} review requested from {reviewer}",
                            new() { ["reviewer"] = reviewer });
                        break;
                    case "closed" when Bool(pr, "merged"):
                        Add(n, url, GitHubEvents.Merged, EventKind.Changed, EventStatus.Merged, $"{reference} merged: {title}");
                        break;
                    case "closed":
                        Add(n, url, GitHubEvents.Closed, EventKind.Removed, EventStatus.Neutral, $"{reference} closed: {title}");
                        break;
                }
                break;
            }
            case "pull_request_review" when action == "submitted":
            {
                var pr = Prop(root, "pull_request");
                if (Num(pr) is not { } n)
                    break;
                var review = Prop(root, "review");
                var by = Str(Prop(review, "user"), "login") ?? sender;
                var reference = $"{repo}#{n}";
                var url = Str(pr, "html_url");
                switch (Str(review, "state")?.ToLowerInvariant())
                {
                    case "approved":
                        Add(n, url, GitHubEvents.Approved, EventKind.Changed, EventStatus.Success, $"{reference} approved by {by}", new() { ["state"] = "approved" });
                        break;
                    case "changes_requested":
                        Add(n, url, GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Attention, $"{reference} changes requested by {by}", new() { ["state"] = "changes_requested" });
                        break;
                    case "commented":
                        Add(n, url, GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Info, $"{reference} reviewed by {by}", new() { ["state"] = "commented" });
                        break;
                }
                break;
            }
            case "check_suite" or "check_run":
            {
                var item = Prop(root, githubEvent);
                var status = Str(item, "status");
                var checks = status != "completed"
                    ? ChecksState.Pending
                    : Str(item, "conclusion") is "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" ? ChecksState.Failing : ChecksState.Passing;
                if (Prop(item, "pull_requests") is { ValueKind: JsonValueKind.Array } prs)
                    foreach (var pr in prs.EnumerateArray())
                        if (Num(pr) is { } n)
                            Add(n, $"https://github.com/{repo}/pull/{n}", GitHubEvents.Checks, EventKind.Changed,
                                checks switch { ChecksState.Passing => EventStatus.Success, ChecksState.Failing => EventStatus.Failure, _ => EventStatus.Pending },
                                $"{repo}#{n} checks {checks}", new() { ["checks"] = checks });
                break;
            }
        }
        return drafts;
    }

    static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    static string? Str(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static bool Bool(JsonElement element, string name) => Prop(element, name).ValueKind == JsonValueKind.True;

    static int? Num(JsonElement element) =>
        Prop(element, "number") is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var n) ? n : null;
}
