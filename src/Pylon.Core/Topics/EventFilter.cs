using Pylon.Core.Events;

namespace Pylon.Core.Topics;

/// <summary>
/// Which events a subscriber gets: any matching topic pattern, and (when given) any matching type pattern.
/// A <c>resync</c> goes to every subscriber whose patterns could cover that source, even though its topic is just the
/// source name: missing one would leave the subscriber believing stale state.
/// </summary>
public sealed class EventFilter
{
    public static readonly EventFilter All = new([TopicPattern.Parse("**")], []);

    /// <summary>Pylon's own resyncs (a pruned replay gap, a slow consumer) are about every source, so everyone gets them.</summary>
    public const string GlobalSource = "pylon";

    public EventFilter(IReadOnlyList<TopicPattern> topics, IReadOnlyList<TypePattern> types)
    {
        if (topics.Count == 0)
            throw new ArgumentException("At least one topic pattern is needed.", nameof(topics));
        Topics = topics;
        Types = types;
    }

    public IReadOnlyList<TopicPattern> Topics { get; }

    public IReadOnlyList<TypePattern> Types { get; }

    public static EventFilter Parse(IEnumerable<string> topics, IEnumerable<string>? types = null)
    {
        var topicPatterns = topics.Where(t => !string.IsNullOrWhiteSpace(t)).Select(TopicPattern.Parse).ToList();
        if (topicPatterns.Count == 0)
            topicPatterns.Add(TopicPattern.Parse("**"));
        return new(topicPatterns, (types ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(TypePattern.Parse).ToList());
    }

    public bool Matches(EventRecord record) => Matches(record.Source, record.Topic, record.Type, record.Kind);

    public bool Matches(string source, string topic, string type, EventKind kind)
    {
        if (kind == EventKind.Resync)
            return source == GlobalSource || Topics.Any(t => t.Head is null || string.Equals(t.Head, source, StringComparison.OrdinalIgnoreCase));
        if (!Topics.Any(t => t.Matches(topic)))
            return false;
        return Types.Count == 0 || Types.Any(t => t.Matches(type));
    }
}
