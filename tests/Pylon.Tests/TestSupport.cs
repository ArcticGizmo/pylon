using Pylon.Core.Events;

namespace Pylon.Tests;

/// <summary>A throwaway data folder per test, deleted afterwards.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pylon-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal static class Drafts
{
    public static EventDraft Make(string topic = "test/a", string type = "test.thing", string? payload = null, string source = "test") => new()
    {
        Source = source,
        Topic = topic,
        Type = type,
        Summary = $"{type} on {topic}",
        Via = EventVia.Test,
        Payload = payload is null ? null : Payload.Json(payload),
    };
}
