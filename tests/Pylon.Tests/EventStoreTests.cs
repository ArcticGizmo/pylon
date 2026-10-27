using Pylon.Broker.Storage;
using Pylon.Core.Events;

namespace Pylon.Tests;

public class EventStoreTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));

    EventStore Store(Retention? retention = null) => new(Path.Combine(_dir.Path, "pylon.db"), _time, retention);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SequencesAreOrderedAndRoundTrip()
    {
        var store = Store();
        var records = store.Append([Drafts.Make("a/1"), Drafts.Make("a/2", payload: """{"x":1}""") with { Hint = new Dictionary<string, string> { ["k"] = "v" } }]);
        Assert.Equal([1L, 2L], records.Select(r => r.Sequence));

        var read = store.ReadAfter(0, 10);
        Assert.Equal(["a/1", "a/2"], read.Select(r => r.Topic));
        Assert.False(read[0].HasPayload);
        Assert.True(read[1].HasPayload);
        Assert.Equal("v", read[1].Hint!["k"]);
        Assert.Equal(EventVia.Test, read[1].Via);
        Assert.Equal("""{"x":1}""", store.GetPayload(2)!.Content);
    }

    [Fact]
    public void SequencesSurviveARestartAndPruning()
    {
        var store = Store(new Retention { MaxEvents = 2 });
        store.Append([Drafts.Make(), Drafts.Make(), Drafts.Make(), Drafts.Make()]);
        store.Prune();
        Assert.Equal(3, store.OldestSequence());

        var reopened = Store();
        Assert.Equal(4, reopened.LatestSequence());
        Assert.Equal(5, reopened.Append([Drafts.Make()])[0].Sequence);
    }

    [Fact]
    public void OldEventsAndPayloadsArePruned()
    {
        var store = Store(new Retention { EventAge = TimeSpan.FromDays(30), PayloadAge = TimeSpan.FromDays(7) });
        store.Append([Drafts.Make(payload: "{}")]);
        _time.Advance(TimeSpan.FromDays(8));
        store.Append([Drafts.Make(payload: "{}")]);

        var (events, payloads) = store.Prune();
        Assert.Equal(0, events);
        Assert.Equal(1, payloads);
        Assert.Null(store.GetPayload(1));
        Assert.False(store.ReadAfter(0, 10)[0].HasPayload);

        _time.Advance(TimeSpan.FromDays(25));
        Assert.Equal(1, store.Prune().Events);
    }

    [Fact]
    public void PayloadsOverTheTotalArePrunedOldestFirst()
    {
        var store = Store(new Retention { MaxPayloadBytes = 25 });
        store.Append([Drafts.Make(payload: new string('a', 10)), Drafts.Make(payload: new string('b', 10)), Drafts.Make(payload: new string('c', 10))]);
        store.Prune();
        Assert.Null(store.GetPayload(1));
        Assert.NotNull(store.GetPayload(2));
        Assert.NotNull(store.GetPayload(3));
    }

    [Fact]
    public void HugePayloadsAreCut()
    {
        var store = Store(new Retention { MaxPayloadSize = 100 });
        store.Append([Drafts.Make(payload: new string('x', 1000))]);
        Assert.Contains("cut: payload was 1000 characters", store.GetPayload(1)!.Content);
    }

    [Fact]
    public void HistoryReadsNewestFirst()
    {
        var store = Store();
        store.Append([.. Enumerable.Range(1, 5).Select(i => Drafts.Make($"a/{i}"))]);
        Assert.Equal([5L, 4L], store.ReadBefore(null, 2).Select(r => r.Sequence));
        Assert.Equal([3L, 2L], store.ReadBefore(4, 2).Select(r => r.Sequence));
    }

    [Fact]
    public void MonitorStateIsPerMonitor()
    {
        var store = Store();
        var a = store.StateFor("a");
        a.Set("k", "1");
        a.Set("k", "2");
        store.StateFor("b").Set("k", "other");
        Assert.Equal("2", a.Get("k"));
        Assert.Equal(["k"], a.Keys());
        store.ClearState("a");
        Assert.Null(a.Get("k"));
        Assert.Equal("other", store.StateFor("b").Get("k"));
    }
}
