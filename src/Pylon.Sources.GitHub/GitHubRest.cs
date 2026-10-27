using System.Text.Json;

namespace Pylon.Sources.GitHub;

/// <summary>
/// Reads the REST responses the poll strategy uses into snapshots. Only the fields <see cref="PullRequestSnapshot"/>
/// needs are read, so GitHub adding fields changes nothing.
/// </summary>
public static class GitHubRest
{
    public static string PullsPath(string repo) => $"repos/{repo}/pulls?state=all&sort=updated&direction=desc&per_page=50";

    public static string ReviewsPath(string repo, int number) => $"repos/{repo}/pulls/{number}/reviews?per_page=100";

    public static string CheckRunsPath(string repo, string sha) => $"repos/{repo}/commits/{sha}/check-runs?per_page=100";

    public static string StatusPath(string repo, string sha) => $"repos/{repo}/commits/{sha}/status";

    public static IReadOnlyList<PullRequestSnapshot> ParsePulls(string repo, string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<PullRequestSnapshot>();
        foreach (var pr in document.RootElement.EnumerateArray())
        {
            var merged = pr.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String;
            var state = merged ? PrState.Merged : String(pr, "state") == "closed" ? PrState.Closed : PrState.Open;
            var reviewers = new List<string>();
            if (pr.TryGetProperty("requested_reviewers", out var users) && users.ValueKind == JsonValueKind.Array)
                reviewers.AddRange(users.EnumerateArray().Select(u => String(u, "login")).OfType<string>());
            if (pr.TryGetProperty("requested_teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
                reviewers.AddRange(teams.EnumerateArray().Select(t => String(t, "slug")).OfType<string>().Select(s => "team:" + s));

            var user = pr.TryGetProperty("user", out var u2) ? u2 : default;
            list.Add(new PullRequestSnapshot
            {
                Repo = repo,
                Number = pr.GetProperty("number").GetInt32(),
                Title = String(pr, "title") ?? "",
                Author = user.ValueKind == JsonValueKind.Object ? String(user, "login") ?? "?" : "?",
                AuthorIsBot = user.ValueKind == JsonValueKind.Object && IsBot(user),
                Draft = pr.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                State = state,
                HeadSha = pr.TryGetProperty("head", out var head) ? String(head, "sha") ?? "" : "",
                CreatedAt = pr.TryGetProperty("created_at", out var created) && created.TryGetDateTimeOffset(out var createdAt) ? createdAt : default,
                UpdatedAt = pr.TryGetProperty("updated_at", out var updated) && updated.TryGetDateTimeOffset(out var at) ? at : default,
                Url = String(pr, "html_url") ?? "",
                RequestedReviewers = reviewers,
            });
        }
        return list;
    }

    public static IReadOnlyList<ReviewSnapshot> ParseReviews(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(r =>
        {
            var user = r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            return new ReviewSnapshot(
                r.GetProperty("id").GetInt64(),
                user.ValueKind == JsonValueKind.Object ? String(user, "login") ?? "?" : "?",
                user.ValueKind == JsonValueKind.Object && IsBot(user),
                String(r, "state") ?? "");
        })];
    }

    /// <summary>
    /// Folds check runs and the legacy commit status into one state: any failure wins, then anything still running,
    /// then passing. The combined status says <c>pending</c> when there are no statuses at all, so its
    /// <c>total_count</c> decides whether it counts.
    /// </summary>
    public static string CombineChecks(string? checkRunsJson, string? statusJson)
    {
        var failing = false;
        var pending = false;
        var any = false;

        if (checkRunsJson is not null)
        {
            using var runs = JsonDocument.Parse(checkRunsJson);
            if (runs.RootElement.TryGetProperty("check_runs", out var list))
                foreach (var run in list.EnumerateArray())
                {
                    any = true;
                    if (String(run, "status") != "completed")
                        pending = true;
                    else if (String(run, "conclusion") is "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure")
                        failing = true;
                }
        }

        if (statusJson is not null)
        {
            using var status = JsonDocument.Parse(statusJson);
            var root = status.RootElement;
            if (root.TryGetProperty("total_count", out var count) && count.GetInt32() > 0)
            {
                any = true;
                switch (String(root, "state"))
                {
                    case "failure" or "error": failing = true; break;
                    case "pending": pending = true; break;
                }
            }
        }

        return failing ? ChecksState.Failing : pending ? ChecksState.Pending : any ? ChecksState.Passing : ChecksState.None;
    }

    static bool IsBot(JsonElement user) =>
        String(user, "type") == "Bot" || (String(user, "login")?.EndsWith("[bot]", StringComparison.Ordinal) ?? false);

    static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
