using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Win32;
using Pylon.Client;
using Pylon.Core.Events;
using Pylon.FakeGitHub;

// pylon-e2e [--postern-root ..\postern] [--poll 10]
//
// The poll path with the real executables:
//   fake GitHub (in this process, loopback) <- Postern.exe (Dev, headless, its own pipe and data) <- Pylon.exe (headless)
// Postern auto-approves only "pylon" (a Debug-only switch) and holds one throwaway credential for the fake, which is
// deleted at the end, as are the data folders and the pipe registrations. Exit code 0 means every step was seen.
// Build first: dotnet build pylon.slnx, and dotnet build of Postern.App and Postern.Cli in the Postern checkout.

const string PosternPipe = "postern-e2e";
const string PylonPipe = "pylon-e2e";
const string Repo = "example-org/app";

var posternRoot = Path.GetFullPath(Arg("--postern-root") ?? Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\postern"));
var pylonRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
var poll = int.Parse(Arg("--poll") ?? "10");
var posternExe = Path.Combine(posternRoot, @"src\Postern.App\bin\Debug\net10.0-windows\Postern.exe");
var posternCli = Path.Combine(posternRoot, @"src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe");
var pylonExe = Path.Combine(pylonRoot, @"src\Pylon.App\bin\Debug\net10.0-windows\Pylon.exe");
foreach (var exe in new[] { posternExe, posternCli, pylonExe })
    if (!File.Exists(exe))
        return Fail($"Missing {exe}. Build it first.");

var run = Guid.NewGuid().ToString("N")[..8];
var posternData = Path.Combine(Path.GetTempPath(), $"pylon-e2e-postern-{run}");
var pylonData = Path.Combine(Path.GetTempPath(), $"pylon-e2e-pylon-{run}");
Directory.CreateDirectory(posternData);
Directory.CreateDirectory(pylonData);

var github = new FakeGitHubState();
var requests = Channel.CreateUnbounded<FakeRequest>();
var (fake, fakeAddress) = await FakeGitHubApi.StartAsync(github, log: r => requests.Writer.TryWrite(r));
Say($"fake GitHub on {fakeAddress}");

Process? postern = null, pylon = null;
ChannelReader<PylonEvent>? eventsReader = null;
var failures = 0;
try
{
    File.WriteAllText(Path.Combine(posternData, "providers.json"),
        $$"""{ "providers": [ { "name": "github-fake", "baseUrl": "{{fakeAddress.GetLeftPart(UriPartial.Authority)}}" } ] }""");
    var set = Run(posternCli, ["credential", "set", "github-fake", "--data-dir", posternData], stdin: "not-a-real-token");
    Say($"postern-cli credential set github-fake: {set.Trim()}");

    File.WriteAllText(Path.Combine(pylonData, "monitors.json"), $$"""
        { "monitors": [ {
            "id": "e2e", "name": "E2E", "source": "github", "connection": "github-fake",
            "targets": [ "{{Repo}}" ],
            "events": [ "opened", "ready_for_review", "review_requested", "reviewed", "approved", "checks", "merged", "closed" ],
            "options": { "includeDrafts": false, "ignoreBots": true },
            "pollSeconds": {{poll}}, "enabled": true } ] }
        """);

    postern = Start(posternExe, ["--headless", "--pipe", PosternPipe, "--data-dir", posternData, "--dev-auto-approve", "pylon"]);
    await WaitForPipe(PosternPipe);
    Say("Postern (Dev) is up");
    pylon = Start(pylonExe, ["--headless", "--pipe", PylonPipe, "--postern-pipe", PosternPipe, "--data-dir", pylonData]);
    await WaitForPipe(PylonPipe);
    Say("Pylon (Dev) is up");

    using var client = new PylonClient(new PylonClientOptions { PipeName = PylonPipe });
    var events = Channel.CreateUnbounded<PylonEvent>();
    eventsReader = events.Reader;
    using var stop = new CancellationTokenSource();
    var tail = Task.Run(async () =>
    {
        try
        {
            await foreach (var e in client.SubscribeAsync(new(["github/**"]), cancellationToken: stop.Token))
                await events.Writer.WriteAsync(e);
        }
        catch (OperationCanceledException)
        {
        }
    });

    // The monitor's first poll is a baseline; wait for it so later changes aren't swallowed by it.
    await WaitForRequest("/pulls", TimeSpan.FromSeconds(60));
    await Task.Delay(500);

    var pr = github.Open(Repo, "First change after the baseline", "dev-one");
    await Expect("github.pr.opened", pr.Number);
    github.RequestReview(pr, "dev-two");
    await Expect("github.pr.review_requested", pr.Number);
    github.Review(pr, "dev-two", "CHANGES_REQUESTED");
    await Expect("github.pr.reviewed", pr.Number);
    github.Push(pr);
    await Expect("github.pr.checks", pr.Number, hint: ("checks", "pending"));
    github.SetChecks(pr, "failing");
    await Expect("github.pr.checks", pr.Number, hint: ("checks", "failing"));
    github.SetChecks(pr, "passing");
    await Expect("github.pr.checks", pr.Number, hint: ("checks", "passing"));
    github.Review(pr, "dev-two", "APPROVED");
    await Expect("github.pr.approved", pr.Number);
    var draft = github.Open(Repo, "A draft stays quiet", "dev-three", draft: true);
    github.Merge(pr, "dev-two");
    await Expect("github.pr.merged", pr.Number);
    github.MarkReady(draft);
    await Expect("github.pr.ready_for_review", draft.Number);
    var bot = github.Open(Repo, "Bump a dependency", "dependabot[bot]", bot: true);
    github.Close(draft, "dev-three");
    await Expect("github.pr.closed", draft.Number);

    await Task.Delay(TimeSpan.FromSeconds(poll * 1.5));
    while (events.Reader.TryRead(out var extra))
    {
        failures++;
        Say($"FAIL unexpected event {extra.Type} on {extra.Topic} (bot PR #{bot.Number} must be quiet)");
    }

    var all = new List<FakeRequest>();
    while (requests.Reader.TryRead(out var r))
        all.Add(r);
    var unauthorised = all.Count(r => !r.Authorized);
    Say($"fake saw {all.Count} more request(s): {all.Count(r => r.Status == 304)} were 304, {unauthorised} arrived without a credential");
    if (unauthorised > 0)
    {
        failures++;
        Say("FAIL some requests reached the fake without Postern's credential");
    }

    var status = await client.GetStatusAsync();
    Say($"Pylon status: monitor state {status["monitors"]?[0]?["state"]}, requests {status["monitors"]?[0]?["requests"]}, 304s {status["monitors"]?[0]?["notModified"]}");
    stop.Cancel();
    await tail;
}
catch (Exception ex)
{
    failures++;
    Say($"FAIL {ex.GetType().Name}: {ex.Message}");
}
finally
{
    foreach (var process in new[] { pylon, postern })
    {
        if (process is { HasExited: false })
            process.Kill(entireProcessTree: true);
        process?.WaitForExit(5000);
    }
    await fake.DisposeAsync();
    Say($"cleanup: {Run(posternCli, ["credential", "delete", "github-fake", "--data-dir", posternData]).Trim()}");
    TryDelete(() => Directory.Delete(posternData, recursive: true));
    TryDelete(() => Directory.Delete(pylonData, recursive: true));
    TryDelete(() => File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Client", $"pylon.{PosternPipe}.token")));
    TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree($@"Software\Postern\Pipes\{PosternPipe}", throwOnMissingSubKey: false));
    TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree($@"Software\Pylon\Pipes\{PylonPipe}", throwOnMissingSubKey: false));
}

Say(failures == 0 ? "PASS: every change was seen through Postern, and nothing else was reported" : $"{failures} failure(s)");
return failures == 0 ? 0 : 1;

async Task Expect(string type, int number, (string Key, string Value)? hint = null)
{
    var started = Stopwatch.StartNew();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(poll * 3 + 10));
    while (true)
    {
        PylonEvent e;
        try
        {
            e = await Events().ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            failures++;
            Say($"FAIL no {type} for #{number} within {started.Elapsed.TotalSeconds:0}s");
            return;
        }
        var matches = e.Type == type && e.Topic.EndsWith($"/{number}", StringComparison.Ordinal)
            && (hint is not { } h || e.Hint?.GetValueOrDefault(h.Key) == h.Value);
        if (matches)
        {
            Say($"ok   {type,-28} #{number} after {started.Elapsed.TotalSeconds,4:0.0}s (sequence {e.Sequence})");
            return;
        }
        failures++;
        Say($"FAIL expected {type} for #{number}, got {e.Type} on {e.Topic}");
    }
}

ChannelReader<PylonEvent> Events() => eventsReader ?? throw new InvalidOperationException("Not subscribed yet.");

async Task WaitForRequest(string pathPart, TimeSpan within)
{
    using var timeout = new CancellationTokenSource(within);
    while (true)
    {
        var r = await requests.Reader.ReadAsync(timeout.Token);
        if (r.Path.Contains(pathPart, StringComparison.Ordinal))
        {
            Say($"first poll reached the fake ({r.Status}, credential {(r.Authorized ? "present" : "MISSING")})");
            return;
        }
    }
}

static async Task WaitForPipe(string name)
{
    for (var i = 0; i < 100; i++)
    {
        if (Directory.EnumerateFiles(@"\\.\pipe\", name).Any())
            return;
        await Task.Delay(200);
    }
    throw new TimeoutException($"Nothing appeared on \\\\.\\pipe\\{name}.");
}

static Process Start(string exe, string[] args)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args)
        info.ArgumentList.Add(arg);
    return Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {exe}.");
}

static string Run(string exe, string[] args, string? stdin = null)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, CreateNoWindow = true };
    foreach (var arg in args)
        info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    if (stdin is not null)
    {
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
    }
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit(30000);
    return output;
}

static void TryDelete(Action delete)
{
    try
    {
        delete();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"[e2e] couldn't clean up: {ex.Message}");
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"[e2e] {message}");
    return 2;
}

static void Say(string message) => Console.WriteLine($"[e2e] {message}");

string? Arg(string name) => Array.IndexOf(args, name) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;
