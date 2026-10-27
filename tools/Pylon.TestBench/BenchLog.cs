namespace Pylon.TestBench;

/// <summary>Everything the bench saw, newest first: requests the fake answered, what it sent Pylon, and what Pylon emitted.</summary>
internal sealed class BenchLog
{
    public enum Kind { Fake, Sent, Pylon, Error }

    public sealed record Entry(DateTimeOffset At, Kind Kind, string Text);

    const int Max = 500;

    readonly Lock _gate = new();
    readonly List<Entry> _entries = [];

    /// <summary>Raised on whichever thread added the entry.</summary>
    public event Action? Changed;

    public void Add(Kind kind, string text)
    {
        lock (_gate)
        {
            _entries.Insert(0, new Entry(DateTimeOffset.Now, kind, text));
            if (_entries.Count > Max)
                _entries.RemoveAt(_entries.Count - 1);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate)
            return [.. _entries];
    }
}
