using Pylon.Core.Events;
using Pylon.Core.Topics;

namespace Pylon.Tests;

public class PatternTests
{
    [Theory]
    [InlineData("github/pr/org/*/*", "github/pr/org/perch/412", true)]
    [InlineData("github/pr/org/*/*", "github/pr/ORG/perch/412", true)]
    [InlineData("github/pr/org/*/*", "github/pr/other/perch/412", false)]
    [InlineData("github/pr/org/*/*", "github/pr/org/perch", false)]
    [InlineData("github/pr/org/*/*", "github/pr/org/perch/412/extra", false)]
    [InlineData("github/**", "github/pr/org/perch/412", true)]
    [InlineData("github/**", "github", true)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("github/pr/*/*/*", "github/run/org/perch/1", false)]
    public void Topics(string pattern, string topic, bool matches) =>
        Assert.Equal(matches, TopicPattern.Parse(pattern).Matches(topic));

    [Theory]
    [InlineData("github.pr.*", "github.pr.approved", true)]
    [InlineData("github.pr.approved", "github.pr.reviewed", false)]
    [InlineData("github.**", "github.pr.checks", true)]
    public void Types(string pattern, string type, bool matches) =>
        Assert.Equal(matches, TypePattern.Parse(pattern).Matches(type));

    [Theory]
    [InlineData("github/**/pr")]
    [InlineData("github/pr*")]
    [InlineData("github//pr")]
    [InlineData("")]
    public void Rejects(string pattern) => Assert.Throws<FormatException>(() => TopicPattern.Parse(pattern));

    [Fact]
    public void FilterNeedsTopicAndType()
    {
        var filter = EventFilter.Parse(["github/pr/org/*/*"], ["github.pr.approved"]);
        Assert.True(filter.Matches("github", "github/pr/org/perch/1", "github.pr.approved", EventKind.Changed));
        Assert.False(filter.Matches("github", "github/pr/org/perch/1", "github.pr.opened", EventKind.Created));
        Assert.False(filter.Matches("github", "github/pr/else/perch/1", "github.pr.approved", EventKind.Changed));
    }

    [Fact]
    public void ResyncReachesEverySubscriberOfThatSource()
    {
        var github = EventFilter.Parse(["github/pr/org/*/*"], ["github.pr.approved"]);
        var octopus = EventFilter.Parse(["octopus/**"]);
        var anything = EventFilter.Parse(["*/pr/**"]);

        Assert.True(github.Matches("github", "github", "github.resync", EventKind.Resync));
        Assert.False(octopus.Matches("github", "github", "github.resync", EventKind.Resync));
        Assert.True(anything.Matches("github", "github", "github.resync", EventKind.Resync));
        Assert.True(octopus.Matches(EventFilter.GlobalSource, "pylon", "pylon.resync", EventKind.Resync));
    }

    [Fact]
    public void EmptyTopicsMeansEverything() => Assert.True(EventFilter.Parse([]).Matches("x", "x/y", "x.y", EventKind.Changed));
}
