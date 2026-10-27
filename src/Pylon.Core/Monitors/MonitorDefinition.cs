using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pylon.Core.Monitors;

/// <summary>
/// One configured watch: a source, the Postern connection it calls through, what to watch and which changes to
/// report. Stored in <c>monitors.json</c> (D6), so it can be edited by hand as well as on the Monitors page.
/// </summary>
public sealed partial record MonitorDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }

    /// <summary>The Postern connection name (<c>github</c>, <c>github-work</c>). Unused by sources that call nothing.</summary>
    public string Connection { get; init; } = "";

    /// <summary>A key into the app's icon set (<c>pull-request</c>, <c>heartbeat</c>), not a file path.</summary>
    public string Icon { get; init; } = "";

    /// <summary>A <c>#rrggbb</c> colour, so two monitors on the same source look different in the widget.</summary>
    public string Accent { get; init; } = "#4FB8A8";

    /// <summary>What to watch. For GitHub, <c>owner/repo</c> entries.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>Which changes to report, as the source's short event names (<c>opened</c>, <c>approved</c>).</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>Source-specific switches (<c>includeDrafts</c>, <c>ignoreBots</c>).</summary>
    public IReadOnlyDictionary<string, bool> Options { get; init; } = new Dictionary<string, bool>();

    public int PollSeconds { get; init; } = 60;

    public bool Enabled { get; init; } = true;

    public bool Option(string name, bool fallback) => Options.TryGetValue(name, out var value) ? value : fallback;

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();
}

/// <summary><c>monitors.json</c> in the data directory.</summary>
public static class MonitorsFile
{
    public const string FileName = "monitors.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static IReadOnlyList<MonitorDefinition> Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
    }

    public static IReadOnlyList<MonitorDefinition> Parse(string json)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{FileName}: {ex.Message}", ex);
        }
        var monitors = document?.Monitors ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var monitor in monitors)
        {
            if (!MonitorDefinition.IsValidId(monitor.Id))
                throw new FormatException($"{FileName}: '{monitor.Id}' isn't a monitor id (lowercase letters, digits, '-')");
            if (!seen.Add(monitor.Id))
                throw new FormatException($"{FileName}: monitor id '{monitor.Id}' is used twice");
            if (monitor.PollSeconds < 10)
                throw new FormatException($"{FileName}: '{monitor.Id}' polls every {monitor.PollSeconds}s; the minimum is 10");
        }
        return monitors;
    }

    public static string Serialize(IEnumerable<MonitorDefinition> monitors) =>
        JsonSerializer.Serialize(new Document { Monitors = [.. monitors] }, Json);

    /// <summary>Writes through a temporary file, so a crash mid-write never leaves half a file behind.</summary>
    public static void Save(string dataDirectory, IEnumerable<MonitorDefinition> monitors)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(monitors));
        File.Move(temp, path, overwrite: true);
    }

    sealed class Document
    {
        public List<MonitorDefinition> Monitors { get; init; } = [];
    }
}
