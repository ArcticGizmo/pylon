using Pylon.Broker.Storage;
using Pylon.Core.Monitors;

namespace Pylon.Tests;

public class PollJournalTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 15, 0, TimeSpan.Zero);

    readonly TempDirectory _dir = new();
    readonly ManualTime _time = new(T0);

    public void Dispose() => _dir.Dispose();

    PollJournal Journal(Retention? retention = null) => new(Path.Combine(_dir.Path, "pylon.db"), _time, retention);

    static RequestTrace Call(int status, int? remaining = null, int? cost = null, string? body = null) => new()
    {
        Path = "repos/example/app/pulls",
        Status = status,
        ETag = "\"abc\"",
        RateLimit = remaining is null ? null : new RateLimit(5000, remaining, null, T0.AddHours(1), "core"),
        RateCost = cost,
        Body = body,
    };

    PollRecord Poll(string monitor = "work", IReadOnlyList<RequestTrace>? calls = null, IReadOnlyList<long>? sequences = null, IReadOnlyList<ObservationNote>? notes = null)
    {
        calls ??= [Call(200, 4999, body: "[]"), Call(304, 4999, 0)];
        return new PollRecord
        {
            MonitorId = monitor,
            StartedAt = _time.GetUtcNow(),
            Milliseconds = 42,
            Requests = calls.Count,
            NotModified = calls.Count(c => c.NotModified),
            Calls = calls,
            Notes = notes ?? [],
            Sequences = sequences ?? [],
            Observations = [new Observation { Resource = "example/app#7", Changes = [new FieldChange("state", "open", "merged")], Reported = ["github.pr.merged"] }],
        };
    }

    [Fact]
    public void RoundTripsEverythingButBodies()
    {
        var journal = Journal();
        var stored = journal.Add(Poll(sequences: [12, 13], notes: [new ObservationNote(NoteLevel.Warning, "hmm", "A4")]));
        Assert.True(stored.Id > 0);

        var read = journal.Get(stored.Id)!;
        Assert.Equal("work", read.MonitorId);
        Assert.Equal(T0, read.StartedAt);
        Assert.Equal(2, read.Calls.Count);
        Assert.Equal("\"abc\"", read.Calls[0].ETag);
        Assert.Null(read.Calls[0].Body);
        Assert.Equal(4999, read.RateLimit!.Remaining);
        Assert.Equal([12L, 13L], read.Sequences);
        Assert.Equal("A4", Assert.Single(read.Notes).Assumption);
        Assert.True(read.HasWarnings);
        Assert.Equal("merged", Assert.Single(Assert.Single(read.Observations).Changes).After);

        Assert.Equal("[]", journal.Body(stored.Id, 0));
        Assert.Null(journal.Body(stored.Id, 1));
        Assert.Equal([0], journal.BodiesFor(stored.Id));
        Assert.Equal(stored.Id, journal.PollFor(13));
        Assert.Null(journal.PollFor(14));
    }

    [Fact]
    public void RecentIsNewestFirstPerMonitorAndPages()
    {
        var journal = Journal();
        var ids = Enumerable.Range(0, 5).Select(_ => journal.Add(Poll()).Id).ToList();
        journal.Add(Poll(monitor: "other"));

        var first = journal.Recent("work", null, 3);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(3), first.Select(p => p.Id));
        var rest = journal.Recent("work", first[^1].Id, 3);
        Assert.Equal(ids.Take(2).Reverse(), rest.Select(p => p.Id));
    }

    [Fact]
    public void HoursSumTheCostOf304sSeparately()
    {
        var journal = Journal();
        journal.Add(Poll(calls: [Call(200, 4990, body: "[]"), Call(304, 4990, 0), Call(200, 4989, 1)]));
        journal.Add(Poll(calls: [Call(304, 4989), Call(304, 4988, 1)]));
        _time.Advance(TimeSpan.FromHours(1));
        journal.Add(Poll());

        var hours = journal.Hours("work", T0.AddHours(-1));
        Assert.Equal(2, hours.Count);
        var nine = hours[1];
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), nine.Hour);
        Assert.Equal(2, nine.Polls);
        Assert.Equal(5, nine.Requests);
        Assert.Equal(3, nine.NotModified);
        Assert.Equal(4988, nine.MinRemaining);
        Assert.Equal((1, 1), (nine.Measured200, nine.Cost200));
        Assert.Equal((2, 1), (nine.Measured304, nine.Cost304));
    }

    [Fact]
    public void PruneDropsOldPollsAndKeepsBodiesWithinBudget()
    {
        var journal = Journal(new Retention { PollAge = TimeSpan.FromDays(1), BodyAge = TimeSpan.FromHours(2), MaxBodyBytes = 150 });
        var old = journal.Add(Poll(sequences: [1]));
        _time.Advance(TimeSpan.FromHours(3));
        var a = journal.Add(Poll(calls: [Call(200, body: new string('a', 100))]));
        var b = journal.Add(Poll(calls: [Call(200, body: new string('b', 100))]));

        journal.Prune();
        Assert.NotNull(journal.Get(old.Id));
        Assert.Null(journal.Body(old.Id, 0));
        Assert.Null(journal.Body(a.Id, 0));
        Assert.NotNull(journal.Body(b.Id, 0));

        _time.Advance(TimeSpan.FromDays(1));
        journal.Prune();
        Assert.Null(journal.Get(old.Id));
        Assert.Null(journal.PollFor(1));
    }
}
