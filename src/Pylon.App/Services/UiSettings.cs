using System.Text.Json;
using Pylon.Core.Events;

namespace Pylon.App.Services;

public enum StreamMode { Compact, Expanded }

/// <summary>
/// Window placement and the widget's filter, in <c>ui.json</c> next to the data. Conveniences only: a missing or broken
/// file just means defaults.
/// </summary>
internal sealed class UiSettings
{
    public bool StreamVisible { get; set; }
    public StreamMode StreamMode { get; set; } = StreamMode.Compact;
    public double? StreamX { get; set; }
    public double? StreamY { get; set; }
    public double CompactWidth { get; set; } = 400;
    public double CompactHeight { get; set; } = 240;
    public double ExpandedWidth { get; set; } = 460;
    public double ExpandedHeight { get; set; } = 620;
    public List<string> FilterMonitors { get; set; } = [];
    public List<EventStatus> FilterStatuses { get; set; } = [];
    public string FilterText { get; set; } = "";

    static readonly JsonSerializerOptions Json = new(PylonEvent.Json) { WriteIndented = true };

    string? _path;

    public static UiSettings Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "ui.json");
        UiSettings settings;
        try
        {
            settings = File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            settings = new();
        }
        settings._path = path;
        return settings;
    }

    public StreamFilter Filter
    {
        get => new()
        {
            Monitors = FilterMonitors.ToHashSet(),
            Statuses = FilterStatuses.ToHashSet(),
            Text = FilterText,
        };
        set
        {
            FilterMonitors = [.. value.Monitors];
            FilterStatuses = [.. value.Statuses];
            FilterText = value.Text;
        }
    }

    public void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException)
        {
        }
    }
}
