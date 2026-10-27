using System.Text.Json.Nodes;

namespace Pylon.FakeGitHub;

/// <summary>The REST shapes, with the fields GitHub returns that Pylon reads plus a few it ignores, so ignoring works.</summary>
public static class FakeGitHubJson
{
    public static JsonObject PullRequest(FakePullRequest pr) => new()
    {
        ["number"] = pr.Number,
        ["title"] = pr.Title,
        ["state"] = pr.State == "open" ? "open" : "closed",
        ["draft"] = pr.Draft,
        ["merged_at"] = pr.State == "merged" ? pr.UpdatedAt.ToString("O") : null,
        ["merged"] = pr.State == "merged",
        ["user"] = FakeGitHubState.User(pr.Author, pr.AuthorIsBot),
        ["head"] = new JsonObject { ["sha"] = pr.HeadSha, ["ref"] = $"feature/{pr.Number}" },
        ["base"] = new JsonObject { ["ref"] = "main" },
        ["created_at"] = pr.CreatedAt.ToString("O"),
        ["updated_at"] = pr.UpdatedAt.ToString("O"),
        ["html_url"] = pr.Url,
        ["requested_reviewers"] = new JsonArray([.. pr.RequestedReviewers.Where(r => !r.StartsWith("team:", StringComparison.Ordinal)).Select(r => (JsonNode)FakeGitHubState.User(r, false))]),
        ["requested_teams"] = new JsonArray([.. pr.RequestedReviewers.Where(r => r.StartsWith("team:", StringComparison.Ordinal)).Select(r => (JsonNode)new JsonObject { ["slug"] = r[5..] })]),
        ["labels"] = new JsonArray(),
        ["comments"] = 0,
    };

    public static JsonArray Pulls(IEnumerable<FakePullRequest> pulls) =>
        new([.. pulls.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Number).Take(50).Select(p => (JsonNode)PullRequest(p))]);

    public static JsonArray Reviews(FakePullRequest pr) => new([.. pr.Reviews.Select(r => (JsonNode)new JsonObject
    {
        ["id"] = r.Id,
        ["user"] = FakeGitHubState.User(r.Author, r.Author.EndsWith("[bot]", StringComparison.Ordinal)),
        ["state"] = r.State,
        ["submitted_at"] = r.SubmittedAt.ToString("O"),
    })]);

    public static JsonObject CheckRuns(FakePullRequest? pr) => pr is null || pr.Checks == "none"
        ? new JsonObject { ["total_count"] = 0, ["check_runs"] = new JsonArray() }
        : new JsonObject
        {
            ["total_count"] = 1,
            ["check_runs"] = new JsonArray(new JsonObject
            {
                ["name"] = "build",
                ["status"] = pr.Checks == "pending" ? "in_progress" : "completed",
                ["conclusion"] = pr.Checks switch { "passing" => "success", "failing" => "failure", _ => null },
            }),
        };

    /// <summary>No legacy statuses: the combined status says pending with a total of zero, as GitHub's does.</summary>
    public static JsonObject Status() => new() { ["state"] = "pending", ["total_count"] = 0, ["statuses"] = new JsonArray() };
}
