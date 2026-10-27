using Pylon.Core.Events;

namespace Pylon.Tests;

public class StreamFilterTests
{
    static EventRecord Record(string summary = "example/perch#4 approved by r1", string? monitor = "work", EventStatus status = EventStatus.Success) => new(
        1, "github", monitor, "github/pr/example/perch/4", "github.pr.approved", EventKind.Changed, null, null,
        DateTimeOffset.UtcNow, summary, status, EventVia.Poll, false);

    [Fact]
    public void EmptyShowsEverything() => Assert.True(new StreamFilter().Matches(Record()));

    [Fact]
    public void EveryWordMustMatchSomewhere()
    {
        Assert.True(new StreamFilter { Text = "perch approved" }.Matches(Record()));
        Assert.True(new StreamFilter { Text = "github.pr" }.Matches(Record()));
        Assert.False(new StreamFilter { Text = "perch merged" }.Matches(Record()));
    }

    [Fact]
    public void MonitorsIncludingNone()
    {
        var onlyWork = new StreamFilter { Monitors = new HashSet<string> { "work" } };
        Assert.True(onlyWork.Matches(Record()));
        Assert.False(onlyWork.Matches(Record(monitor: null)));
        Assert.True(new StreamFilter { Monitors = new HashSet<string> { StreamFilter.NoMonitor } }.Matches(Record(monitor: null)));
    }

    [Fact]
    public void Statuses()
    {
        var failures = new StreamFilter { Statuses = new HashSet<EventStatus> { EventStatus.Failure } };
        Assert.False(failures.Matches(Record()));
        Assert.True(failures.Matches(Record(status: EventStatus.Failure)));
    }
}
