namespace Pylon.Core.Topics;

/// <summary>
/// A glob over separated segments, the same rules as Postern's scopes so the two read alike:
/// <list type="bullet">
/// <item><c>*</c> matches exactly one segment.</item>
/// <item><c>**</c> matches zero or more segments, and only as the last segment (keeps matching linear).</item>
/// <item>Anything else is a literal, compared case-insensitively (GitHub owner and repo names are).</item>
/// </list>
/// </summary>
public abstract class SegmentPattern
{
    readonly string[] _segments;

    protected SegmentPattern(string text, char separator)
    {
        Text = text.Trim();
        Separator = separator;
        if (Text.Length == 0)
            throw new FormatException("Empty pattern.");
        _segments = Text.Split(separator);
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (segment.Length == 0)
                throw new FormatException($"'{text}': empty segment");
            if (segment == "**" && i != _segments.Length - 1)
                throw new FormatException($"'{text}': '**' is only allowed at the end");
            if (segment is not ("*" or "**") && segment.Contains('*'))
                throw new FormatException($"'{text}': '{segment}' — wildcards must be a whole segment (* or **)");
        }
    }

    public string Text { get; }

    public char Separator { get; }

    /// <summary>The first segment, or null when the pattern starts with a wildcard.</summary>
    public string? Head => _segments[0] is "*" or "**" ? null : _segments[0];

    public bool Matches(string value)
    {
        var parts = value.Split(Separator);
        for (var i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] == "**")
                return true;
            if (i >= parts.Length)
                return false;
            if (_segments[i] != "*" && !string.Equals(_segments[i], parts[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return parts.Length == _segments.Length;
    }

    public override string ToString() => Text;
}

/// <summary>A topic pattern such as <c>github/pr/org/*/*</c>.</summary>
public sealed class TopicPattern(string text) : SegmentPattern(text, '/')
{
    public static TopicPattern Parse(string text) => new(text);
}

/// <summary>An event type pattern such as <c>github.pr.*</c>.</summary>
public sealed class TypePattern(string text) : SegmentPattern(text, '.')
{
    public static TypePattern Parse(string text) => new(text);
}
