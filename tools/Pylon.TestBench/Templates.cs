using Pylon.FakeGitHub;

namespace Pylon.TestBench;

/// <summary>
/// Starting points for the payload editor. The GitHub ones are produced by the fake itself on a scratch copy, so
/// they're the same shape the live fake sends and the same shape the normaliser is tested against.
/// </summary>
internal static class Templates
{
    public const string RawPylonEvent = "Raw Pylon event";

    public static IReadOnlyList<string> Names { get; } =
    [
        "PR opened", "Ready for review", "Review requested", "Review: approved", "Review: changes requested", "Review: commented",
        "Checks: pending", "Checks: passing", "Checks: failing", "Merged", "Closed", RawPylonEvent,
    ];

    /// <summary>The X-GitHub-Event name and body, or (null, body) for a raw Pylon event.</summary>
    public static (string? Event, string Body) Build(string name, string repo, string author, string reviewer)
    {
        if (name == RawPylonEvent)
            return (null, $$"""
                {
                  "source": "test",
                  "topic": "test/{{repo}}/manual",
                  "type": "test.manual",
                  "kind": "changed",
                  "summary": "Hand-made event from the test bench",
                  "status": "attention",
                  "hint": { "note": "anything small" },
                  "payload": { "free": "form", "anything": [1, 2, 3] }
                }
                """);

        var scratch = new FakeGitHubState();
        FakeWebhook? last = null;
        scratch.Changed += hook => last = hook;
        var pr = scratch.Open(repo, "Example change from the test bench", author);
        switch (name)
        {
            case "PR opened": break;
            case "Ready for review": scratch.MarkReady(pr); break;
            case "Review requested": scratch.RequestReview(pr, reviewer); break;
            case "Review: approved": scratch.Review(pr, reviewer, "APPROVED"); break;
            case "Review: changes requested": scratch.Review(pr, reviewer, "CHANGES_REQUESTED"); break;
            case "Review: commented": scratch.Review(pr, reviewer, "COMMENTED"); break;
            case "Checks: pending": scratch.SetChecks(pr, "pending"); break;
            case "Checks: passing": scratch.SetChecks(pr, "passing"); break;
            case "Checks: failing": scratch.SetChecks(pr, "failing"); break;
            case "Merged": scratch.Merge(pr, reviewer); break;
            case "Closed": scratch.Close(pr, author); break;
        }
        return (last!.Event, last.Body);
    }
}
