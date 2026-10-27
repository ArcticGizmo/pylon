using Pylon.Core.Events;
using Pylon.Sources.GitHub;

namespace Pylon.Tests;

public class GitHubParsingTests
{
    [Fact]
    public void PullsListReadsWhatTheSnapshotNeeds()
    {
        const string json = """
            [{
              "number": 12, "title": "Fix it", "state": "closed", "draft": false, "merged_at": "2026-10-27T09:00:00Z",
              "user": { "login": "renovate[bot]", "type": "Bot" }, "head": { "sha": "abc" },
              "created_at": "2026-10-26T09:00:00Z", "updated_at": "2026-10-27T09:00:00Z",
              "html_url": "https://github.com/example/app/pull/12",
              "requested_reviewers": [{ "login": "r1" }], "requested_teams": [{ "slug": "core" }],
              "unknown_field": { "ignored": true }
            }]
            """;
        var pr = Assert.Single(GitHubRest.ParsePulls("example/app", json));
        Assert.Equal(PrState.Merged, pr.State);
        Assert.True(pr.AuthorIsBot);
        Assert.Equal(["r1", "team:core"], pr.RequestedReviewers);
        Assert.Equal("abc", pr.HeadSha);
        Assert.Null(pr.Reviews);
    }

    [Theory]
    [InlineData("""{"check_runs":[{"status":"completed","conclusion":"success"}]}""", """{"state":"pending","total_count":0}""", ChecksState.Passing)]
    [InlineData("""{"check_runs":[{"status":"in_progress","conclusion":null}]}""", null, ChecksState.Pending)]
    [InlineData("""{"check_runs":[{"status":"in_progress"},{"status":"completed","conclusion":"failure"}]}""", null, ChecksState.Failing)]
    [InlineData("""{"check_runs":[{"status":"completed","conclusion":"skipped"}]}""", """{"state":"failure","total_count":1}""", ChecksState.Failing)]
    [InlineData("""{"check_runs":[]}""", """{"state":"pending","total_count":0}""", ChecksState.None)]
    [InlineData("""{"check_runs":[]}""", """{"state":"success","total_count":2}""", ChecksState.Passing)]
    public void ChecksCombine(string runs, string? status, string expected) => Assert.Equal(expected, GitHubRest.CombineChecks(runs, status));

    [Fact]
    public void WebhookApprovalBecomesTheSameEventAsPolling()
    {
        const string body = """
            { "action": "submitted", "review": { "state": "approved", "user": { "login": "r2" } },
              "pull_request": { "number": 7, "html_url": "https://github.com/example/app/pull/7" },
              "repository": { "full_name": "example/app" }, "sender": { "login": "r2" } }
            """;
        var e = Assert.Single(WebhookNormaliser.Normalise("pull_request_review", body, EventVia.Test));
        Assert.Equal("github/pr/example/app/7", e.Topic);
        Assert.Equal("github.pr.approved", e.Type);
        Assert.Equal(EventVia.Test, e.Via);
        Assert.Equal(body, e.Payload!.Content);
    }

    [Theory]
    [InlineData("opened", false, "github.pr.opened")]
    [InlineData("closed", true, "github.pr.merged")]
    [InlineData("closed", false, "github.pr.closed")]
    [InlineData("ready_for_review", false, "github.pr.ready_for_review")]
    public void WebhookPullRequestActions(string action, bool merged, string type)
    {
        var body = $$"""
            { "action": "{{action}}", "number": 3,
              "pull_request": { "number": 3, "title": "T", "merged": {{(merged ? "true" : "false")}}, "user": { "login": "a" } },
              "repository": { "full_name": "example/app" } }
            """;
        Assert.Equal(type, Assert.Single(WebhookNormaliser.Normalise("pull_request", body, EventVia.Push)).Type);
    }

    [Fact]
    public void WebhookCheckRunFansOutToEachPr()
    {
        const string body = """
            { "action": "completed", "check_run": { "status": "completed", "conclusion": "failure", "pull_requests": [{ "number": 1 }, { "number": 2 }] },
              "repository": { "full_name": "example/app" } }
            """;
        var events = WebhookNormaliser.Normalise("check_run", body, EventVia.Push);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(EventStatus.Failure, e.Status));
    }

    [Fact]
    public void UnknownWebhooksProduceNothing()
    {
        Assert.Empty(WebhookNormaliser.Normalise("star", """{ "action": "created", "repository": { "full_name": "example/app" } }""", EventVia.Push));
        Assert.Empty(WebhookNormaliser.Normalise("pull_request", """{ "action": "labeled", "number": 1, "pull_request": { "number": 1 }, "repository": { "full_name": "example/app" } }""", EventVia.Push));
    }

    [Fact]
    public void ScopesArePerOwner()
    {
        var monitor = new Pylon.Core.Monitors.MonitorDefinition
        {
            Id = "m", Name = "M", Source = "github", Connection = "github-work",
            Targets = ["example/app", "example/lib", "other/x"],
            Events = [GitHubEvents.Opened, GitHubEvents.Checks],
        };
        Assert.Equal(
            [
                "GET github-work/repos/example/*/pulls/**",
                "GET github-work/repos/example/*/commits/*/check-runs",
                "GET github-work/repos/example/*/commits/*/status",
                "GET github-work/repos/other/*/pulls/**",
                "GET github-work/repos/other/*/commits/*/check-runs",
                "GET github-work/repos/other/*/commits/*/status",
            ],
            new GitHubSource().ScopesFor(monitor));
    }

    [Fact]
    public void ValidationExplainsEachProblem()
    {
        var monitor = new Pylon.Core.Monitors.MonitorDefinition
        {
            Id = "m", Name = "M", Source = "github", Connection = "GitHub!", Targets = ["example/*", "nope"], Events = ["opened", "starred"],
        };
        Assert.Equal(4, new GitHubSource().Validate(monitor).Count);
    }
}
