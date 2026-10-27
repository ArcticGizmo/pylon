using System.Text.Json;
using Pylon.Client;
using Pylon.Core.Events;

// pylon-cli: watch Pylon's event stream, and push test events into a dev Pylon.
//
//   pylon-cli status
//   pylon-cli tail                                         every event, live
//   pylon-cli tail --topic "github/pr/org/*/*" --type "github.pr.approved" --since 1200
//   pylon-cli tail --json                                  one JSON event per line
//   pylon-cli emit --github-event pull_request --file opened.json      (dev ingest: a webhook body)
//   pylon-cli emit --file event.json                                   (dev ingest: a raw event)
//
// --pipe NAME (or %PYLON_PIPE%) picks the Pylon; a Debug build defaults to pylon-dev, a Release build to pylon.

const int ExitOk = 0, ExitUsage = 2, ExitPylon = 3;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    return Usage();

var command = args[0];
var rest = args.Skip(1).ToList();
string? Take(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count)
        return null;
    var value = rest[i + 1];
    rest.RemoveRange(i, 2);
    return value;
}
List<string> TakeAll(string name)
{
    var values = new List<string>();
    while (Take(name) is { } value)
        values.Add(value);
    return values;
}
bool Flag(string name) => rest.Remove(name);

#if DEBUG
const string DefaultPipe = "pylon-dev";
#else
const string DefaultPipe = PylonClientOptions.DefaultPipeName;
#endif
var pipe = Take("--pipe") ?? (Environment.GetEnvironmentVariable("PYLON_PIPE") is { Length: > 0 } fromEnvironment ? fromEnvironment : DefaultPipe);
using var pylon = new PylonClient(new PylonClientOptions { PipeName = pipe });
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

if (!pylon.IsAvailable)
{
    Console.Error.WriteLine($"pylon: nothing is listening on \\\\.\\pipe\\{pipe}. Start Pylon.exe (a Debug build listens on pylon-dev).");
    return ExitPylon;
}

try
{
    switch (command)
    {
        case "status":
            Console.WriteLine((await pylon.GetStatusAsync(stop.Token)).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return ExitOk;

        case "tail":
        {
            var topics = TakeAll("--topic");
            var types = TakeAll("--type");
            long? since = Take("--since") is { } s && long.TryParse(s, out var parsed) ? parsed : null;
            var json = Flag("--json");
            var request = new SubscriptionRequest(topics.Count == 0 ? ["**"] : topics, types);
            Console.Error.WriteLine($"pylon: tailing \\\\.\\pipe\\{pipe} ({string.Join(", ", request.Topics)}). Ctrl+C to stop.");
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await foreach (var e in pylon.SubscribeAsync(request, since, stop.Token))
                    {
                        since = e.Sequence;
                        Console.WriteLine(json ? JsonSerializer.Serialize(e, PylonEvent.Json) : Line(e));
                    }
                    Console.Error.WriteLine("pylon: stream closed; reconnecting...");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or PylonUnavailableException && ex is not PylonVerificationException)
                {
                    Console.Error.WriteLine($"pylon: {ex.Message}; retrying in 2 s...");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            return ExitOk;
        }

        case "emit":
        {
            var file = Take("--file");
            if (file is null)
                return Usage();
            var body = await File.ReadAllTextAsync(file, stop.Token);
            var result = Take("--github-event") is { } githubEvent
                ? await pylon.IngestGitHubAsync(githubEvent, body, Take("--monitor"), stop.Token)
                : await pylon.EmitAsync(body, stop.Token);
            Console.WriteLine(result.ToJsonString());
            return ExitOk;
        }

        default:
            return Usage();
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return ExitOk;
}
catch (Exception ex) when (ex is PylonUnavailableException or InvalidOperationException or HttpRequestException or IOException)
{
    Console.Error.WriteLine($"pylon: {ex.Message}");
    return ExitPylon;
}

static string Line(PylonEvent e)
{
    var hint = e.Hint is { Count: > 0 } h ? " " + string.Join(' ', h.Select(p => $"{p.Key}={p.Value}")) : "";
    return $"{e.ObservedAtUtc.ToLocalTime():HH:mm:ss} #{e.Sequence,-6} {e.Kind.ToString().ToLowerInvariant(),-8} {e.Type,-28} {e.Topic}{hint}";
}

static int Usage()
{
    Console.Error.WriteLine("""
        pylon-cli status
        pylon-cli tail [--topic PATTERN]... [--type PATTERN]... [--since SEQUENCE] [--json]
        pylon-cli emit --file FILE [--github-event NAME [--monitor ID]]     (dev Pylon only)
        Options: --pipe NAME (default: pylon-dev for a Debug build, pylon for Release; or %PYLON_PIPE%)
        """);
    return ExitUsage;
}
