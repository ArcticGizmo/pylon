using System.Text.Json;
using Pylon.Core.Events;

namespace Pylon.Sources.GitHub;

/// <summary>A delivery read: the repository it's about (null if none), what it was, and the changes it carries.</summary>
public sealed record WebhookDelivery(string? Repo, string Description, IReadOnlyList<WebhookChange> Changes);

/// <summary>
/// One change a webhook carries, plus what a monitor's switches need to decide about it. Check deliveries don't say
/// who opened the PR or whether it's a draft, so those are false for them.
/// </summary>
public sealed record WebhookChange(
    string Repo,
    int Number,
    string? Url,
    string? Title,
    string Name,
    EventKind Kind,
    EventStatus Status,
    string Summary,
    IReadOnlyDictionary<string, string>? Hint,
    bool AuthorIsBot,
    bool Draft,
    bool ReviewerIsBot)
{
    public string Reference => $"{Repo}#{Number}";

    public EventDraft ToDraft(string? monitorId, EventVia via, string body) => new()
    {
        Source = GitHubSource.SourceName,
        MonitorId = monitorId,
        Topic = GitHubEvents.Topic(Repo, Number),
        Type = GitHubEvents.Type(Name),
        Kind = Kind,
        ResourceId = Url,
        Hint = Hint,
        Summary = Summary,
        Status = Status,
        Via = via,
        Payload = Payload.Json(body),
    };
}

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
    /// <summary>Every change in the delivery as an event, with no monitor's switches applied.</summary>
    public static IReadOnlyList<EventDraft> Normalise(string githubEvent, string body, EventVia via, string? monitorId = null) =>
        [.. Parse(githubEvent, body).Changes.Select(c => c.ToDraft(monitorId, via, body))];

    /// <summary>What the delivery is about and the changes it carries, before any monitor's switches.</summary>
    public static WebhookDelivery Parse(string githubEvent, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var action = Str(root, "action");
        var description = action is null ? githubEvent : $"{githubEvent} ({action})";
        var repo = Str(Prop(root, "repository"), "full_name");
        if (repo is null)
            return new WebhookDelivery(null, description, []);

        var changes = new List<WebhookChange>();
        var authorIsBot = false;
        var draft = false;
        string? title = null;
        void Add(int number, string? url, string name, EventKind kind, EventStatus status, string summary, Dictionary<string, string>? hint = null, bool reviewerIsBot = false) =>
            changes.Add(new WebhookChange(repo, number, url, title, name, kind, status, summary, hint, authorIsBot, draft, reviewerIsBot));

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
                title = Str(pr, "title") ?? "";
                authorIsBot = IsBot(Prop(pr, "user"));
                draft = Bool(pr, "draft");
                var url = Str(pr, "html_url");
                switch (action)
                {
                    case "opened":
                        Add(n, url, GitHubEvents.Opened, EventKind.Created, EventStatus.Info, $"{reference} opened by {Str(Prop(pr, "user"), "login") ?? sender}: {title}",
                            new() { ["draft"] = draft ? "true" : "false" });
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
                var byBot = IsBot(Prop(review, "user"));
                title = Str(pr, "title") ?? "";
                authorIsBot = IsBot(Prop(pr, "user"));
                draft = Bool(pr, "draft");
                var reference = $"{repo}#{n}";
                var url = Str(pr, "html_url");
                switch (Str(review, "state")?.ToLowerInvariant())
                {
                    case "approved":
                        Add(n, url, GitHubEvents.Approved, EventKind.Changed, EventStatus.Success, $"{reference} approved by {by}", new() { ["state"] = "approved" }, byBot);
                        break;
                    case "changes_requested":
                        Add(n, url, GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Attention, $"{reference} changes requested by {by}", new() { ["state"] = "changes_requested" }, byBot);
                        break;
                    case "commented":
                        Add(n, url, GitHubEvents.Reviewed, EventKind.Changed, EventStatus.Info, $"{reference} reviewed by {by}", new() { ["state"] = "commented" }, byBot);
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
        return new WebhookDelivery(repo, description, changes);
    }

    static bool IsBot(JsonElement user) =>
        Str(user, "type") == "Bot" || (Str(user, "login")?.EndsWith("[bot]", StringComparison.Ordinal) ?? false);

    static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    static string? Str(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static bool Bool(JsonElement element, string name) => Prop(element, name).ValueKind == JsonValueKind.True;

    static int? Num(JsonElement element) =>
        Prop(element, "number") is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var n) ? n : null;
}
