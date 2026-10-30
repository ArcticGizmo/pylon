using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pylon.FakeGitHub;

public sealed record FakeReview(long Id, string Author, string State, DateTimeOffset SubmittedAt);

public sealed class FakePullRequest
{
    public required string Repo { get; init; }
    public required int Number { get; init; }
    public required string Title { get; set; }
    public required string Author { get; init; }
    public bool AuthorIsBot { get; init; }
    public bool Draft { get; set; }

    /// <summary>open, closed or merged.</summary>
    public string State { get; set; } = "open";

    public required string HeadSha { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<string> RequestedReviewers { get; } = [];
    public List<FakeReview> Reviews { get; } = [];

    /// <summary>none, pending, passing or failing: one check run called "build".</summary>
    public string Checks { get; set; } = "none";

    public string Url => $"https://github.com/{Repo}/pull/{Number}";
}

/// <summary>A webhook delivery GitHub would have sent for a change: the <c>X-GitHub-Event</c> name and the body.</summary>
public sealed record FakeWebhook(string Event, string Body, string Description);

/// <summary>
/// The fake's pull requests. Every change bumps <c>updated_at</c> except checks, which is the assumption the poll
/// strategy makes about real GitHub (S1 checks it). The quirk switches make it break those assumptions on purpose, so
/// the monitor's assumption checks can be seen working. Thread-safe: the test bench's UI and its HTTP server both use it.
/// </summary>
public sealed class FakeGitHubState
{
    readonly Lock _gate = new();
    readonly List<FakePullRequest> _pulls = [];
    readonly Dictionary<string, string> _listETags = new(StringComparer.OrdinalIgnoreCase);
    readonly TimeProvider _time;
    long _nextReviewId = 9000;
    int _remaining = 5000;

    public FakeGitHubState(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Off breaks A1: a submitted review leaves <c>updated_at</c> where it was.</summary>
    public bool ReviewsBumpUpdatedAt { get; set; } = true;

    /// <summary>On breaks A2 (harmlessly): a check finishing moves <c>updated_at</c>.</summary>
    public bool ChecksBumpUpdatedAt { get; set; }

    /// <summary>On breaks A4: the pulls list keeps the ETag it last served, so it answers 304 even after a change.</summary>
    public bool StaleListETags { get; set; }

    /// <summary>The ETag to serve for a pulls list: its real one, unless stale ETags are on and one was served before.</summary>
    internal string ListETag(string path, string body)
    {
        lock (_gate)
        {
            if (StaleListETags && _listETags.TryGetValue(path, out var stale))
                return stale;
            return _listETags[path] = ETag(body);
        }
    }

    /// <summary>Raised after every change with the webhook GitHub would have sent for it.</summary>
    public event Action<FakeWebhook>? Changed;

    public int RateLimitRemaining
    {
        get
        {
            lock (_gate)
                return _remaining;
        }
    }

    public IReadOnlyList<FakePullRequest> Pulls(string? repo = null)
    {
        lock (_gate)
            return [.. _pulls.Where(p => repo is null || p.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))];
    }

    public FakePullRequest? Find(string repo, int number)
    {
        lock (_gate)
            return _pulls.FirstOrDefault(p => p.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && p.Number == number);
    }

    internal int Spend(bool free)
    {
        lock (_gate)
            return free ? _remaining : --_remaining;
    }

    public FakePullRequest Open(string repo, string title, string author, bool draft = false, bool bot = false)
    {
        FakePullRequest pr;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            pr = new FakePullRequest
            {
                Repo = repo,
                Number = _pulls.Where(p => p.Repo == repo).Select(p => p.Number).DefaultIfEmpty(100).Max() + 1,
                Title = title,
                Author = author,
                AuthorIsBot = bot,
                Draft = draft,
                HeadSha = NewSha(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _pulls.Add(pr);
        }
        Raise("pull_request", PullRequestEvent(pr, "opened", author), $"{pr.Repo}#{pr.Number} opened");
        return pr;
    }

    public void MarkReady(FakePullRequest pr) => Mutate(pr, p => p.Draft = false, "pull_request", p => PullRequestEvent(p, "ready_for_review", p.Author), "ready for review");

    public void RequestReview(FakePullRequest pr, string reviewer) =>
        Mutate(pr, p =>
        {
            if (!p.RequestedReviewers.Contains(reviewer))
                p.RequestedReviewers.Add(reviewer);
        }, "pull_request", p =>
        {
            var body = PullRequestEvent(p, "review_requested", p.Author);
            body[reviewer.StartsWith("team:", StringComparison.Ordinal) ? "requested_team" : "requested_reviewer"] = reviewer.StartsWith("team:", StringComparison.Ordinal)
                ? new JsonObject { ["slug"] = reviewer[5..] }
                : User(reviewer, false);
            return body;
        }, $"review requested from {reviewer}");

    /// <summary><paramref name="state"/> is APPROVED, CHANGES_REQUESTED or COMMENTED, as GitHub spells it.</summary>
    public void Review(FakePullRequest pr, string author, string state)
    {
        FakeReview? review = null;
        Mutate(pr, p =>
        {
            review = new FakeReview(Interlocked.Increment(ref _nextReviewId), author, state, _time.GetUtcNow());
            p.Reviews.Add(review);
            // Removing the reviewer would show in the list; with A1 broken the review has to be invisible there.
            if (ReviewsBumpUpdatedAt)
                p.RequestedReviewers.Remove(author);
        }, "pull_request_review", p =>
        {
            var body = PullRequestEvent(p, "submitted", author);
            body["review"] = new JsonObject
            {
                ["id"] = review!.Id,
                ["user"] = User(author, false),
                ["state"] = state.ToLowerInvariant(),
                ["submitted_at"] = review.SubmittedAt.ToString("O"),
            };
            return body;
        }, $"{state.ToLowerInvariant()} by {author}", bump: ReviewsBumpUpdatedAt);
    }

    /// <summary>A new commit: new head SHA, and checks start again.</summary>
    public void Push(FakePullRequest pr) =>
        Mutate(pr, p =>
        {
            p.HeadSha = NewSha();
            p.Checks = "pending";
        }, "pull_request", p => PullRequestEvent(p, "synchronize", p.Author), "new commit pushed");

    /// <summary>Doesn't bump <c>updated_at</c> unless <see cref="ChecksBumpUpdatedAt"/>: real GitHub is assumed not to (A2).</summary>
    public void SetChecks(FakePullRequest pr, string checks)
    {
        lock (_gate)
        {
            pr.Checks = checks;
            if (ChecksBumpUpdatedAt)
                pr.UpdatedAt = _time.GetUtcNow();
        }
        var status = checks == "pending" ? "in_progress" : "completed";
        var body = new JsonObject
        {
            ["action"] = checks == "pending" ? "created" : "completed",
            ["check_run"] = new JsonObject
            {
                ["name"] = "build",
                ["head_sha"] = pr.HeadSha,
                ["status"] = status,
                ["conclusion"] = checks switch { "passing" => "success", "failing" => "failure", _ => null },
                ["pull_requests"] = new JsonArray(new JsonObject { ["number"] = pr.Number }),
            },
            ["repository"] = Repository(pr.Repo),
            ["sender"] = User("github-actions[bot]", true),
        };
        Raise("check_run", body, $"{pr.Repo}#{pr.Number} checks {checks}");
    }

    public void Merge(FakePullRequest pr, string by) =>
        Mutate(pr, p => p.State = "merged", "pull_request", p => PullRequestEvent(p, "closed", by), $"merged by {by}");

    public void Close(FakePullRequest pr, string by) =>
        Mutate(pr, p => p.State = "closed", "pull_request", p => PullRequestEvent(p, "closed", by), $"closed by {by}");

    void Mutate(FakePullRequest pr, Action<FakePullRequest> change, string githubEvent, Func<FakePullRequest, JsonObject> body, string description, bool bump = true)
    {
        lock (_gate)
        {
            change(pr);
            if (bump)
                pr.UpdatedAt = _time.GetUtcNow();
        }
        Raise(githubEvent, body(pr), $"{pr.Repo}#{pr.Number} {description}");
    }

    // Webhook bodies are shown in the test bench's editor, so "+00:00" rather than "+00:00".
    static readonly System.Text.Json.JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    void Raise(string githubEvent, JsonObject body, string description) =>
        Changed?.Invoke(new FakeWebhook(githubEvent, body.ToJsonString(Readable), description));

    static JsonObject PullRequestEvent(FakePullRequest pr, string action, string sender) => new()
    {
        ["action"] = action,
        ["number"] = pr.Number,
        ["pull_request"] = FakeGitHubJson.PullRequest(pr),
        ["repository"] = Repository(pr.Repo),
        ["sender"] = User(sender, sender.EndsWith("[bot]", StringComparison.Ordinal)),
    };

    internal static JsonObject User(string login, bool bot) => new()
    {
        ["login"] = login,
        ["type"] = bot ? "Bot" : "User",
        ["html_url"] = $"https://github.com/{login}",
    };

    static JsonObject Repository(string repo) => new()
    {
        ["full_name"] = repo,
        ["name"] = repo.Split('/')[1],
        ["owner"] = new JsonObject { ["login"] = repo.Split('/')[0] },
        ["html_url"] = $"https://github.com/{repo}",
    };

    static string NewSha() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    internal static string ETag(string body) => "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16].ToLowerInvariant() + "\"";
}
