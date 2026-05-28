using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MessageRouterTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // ----- Pattern impls --------------------------------------------------

    [Fact]
    public void RegexPattern_CapturesGroupsFromMatch()
    {
        RegexPattern p = new("test.combat",
            @"^(\S+) hits (\S+) for (\d+) damage\.$");

        Assert.True(p.TryMatch(Line("Goblin hits Forged for 12 damage."), out MatchResult r));
        Assert.Equal("test.combat", r.PatternId);
        Assert.Equal(new[] { "Goblin", "Forged", "12" }, r.Groups);
    }

    [Fact]
    public void RegexPattern_NoMatch_ReturnsFalseAndDefault()
    {
        RegexPattern p = new("test.x", @"^foo$");
        Assert.False(p.TryMatch(Line("bar"), out MatchResult r));
        Assert.Equal(default, r);
    }

    [Fact]
    public void PrefixPattern_HitsOnLeadingMatch()
    {
        PrefixPattern p = new("test.gossip", "*GOSSIP* ");
        Assert.True(p.TryMatch(Line("*GOSSIP* Forged: hello"), out MatchResult r));
        Assert.Empty(r.Groups);
    }

    [Fact]
    public void PrefixPattern_MissesWhenNotAtStart()
    {
        PrefixPattern p = new("test.gossip", "*GOSSIP* ");
        Assert.False(p.TryMatch(Line(" *GOSSIP* foo"), out _));
    }

    [Fact]
    public void ExactPattern_RequiresFullEquality()
    {
        ExactPattern p = new("test.welcome", "Welcome to MajorMUD!");
        Assert.True (p.TryMatch(Line("Welcome to MajorMUD!"), out _));
        Assert.False(p.TryMatch(Line("Welcome to MajorMUD! "), out _));
        Assert.False(p.TryMatch(Line("welcome to majormud!"), out _));
    }

    // ----- Router fan-out + priority + dispose ---------------------------

    [Fact]
    public void Dispatch_FansOutToEveryMatchingPattern()
    {
        MessageRouter router = new();
        List<string> hits = new();

        router.Register(new PrefixPattern("a", "Hello"),  r => hits.Add($"a:{r.Text}"));
        router.Register(new RegexPattern("b",  @"\w+!$"), r => hits.Add($"b:{r.Text}"));
        router.Register(new PrefixPattern("c", "Bye"),    r => hits.Add($"c:{r.Text}"));

        router.Dispatch(Line("Hello world!"));

        Assert.Equal(new[] { "a:Hello world!", "b:Hello world!" }, hits);
    }

    [Fact]
    public void Dispatch_HigherPriorityFiresFirst()
    {
        MessageRouter router = new();
        List<string> order = new();

        router.Register(new PrefixPattern("low",  "X", priority: 0),  _ => order.Add("low"));
        router.Register(new PrefixPattern("high", "X", priority: 10), _ => order.Add("high"));
        router.Register(new PrefixPattern("mid",  "X", priority: 5),  _ => order.Add("mid"));

        router.Dispatch(Line("X marks the spot"));

        Assert.Equal(new[] { "high", "mid", "low" }, order);
    }

    [Fact]
    public void Register_DisposeUnsubscribes()
    {
        MessageRouter router = new();
        int hits = 0;

        IDisposable token = router.Register(new PrefixPattern("p", "X"), _ => hits++);
        router.Dispatch(Line("X1"));
        Assert.Equal(1, hits);

        token.Dispose();
        router.Dispatch(Line("X2"));
        Assert.Equal(1, hits);   // no new fire after dispose
        Assert.Equal(0, router.SubscriptionCount);
    }

    [Fact]
    public void Dispatch_HandlerThatRegistersDuringDispatch_DoesNotMutateActiveIterator()
    {
        MessageRouter router = new();
        int extraHits = 0;

        // First handler registers a second pattern mid-dispatch. Snapshot
        // semantics guarantee the freshly-added pattern doesn't fire for the
        // line we're already dispatching.
        router.Register(new PrefixPattern("a", "X"), _ =>
        {
            router.Register(new PrefixPattern("late", "X"), _ => extraHits++);
        });

        router.Dispatch(Line("X1"));
        Assert.Equal(0, extraHits);   // "late" was registered AFTER snapshot

        router.Dispatch(Line("X2"));
        Assert.Equal(1, extraHits);   // and now it fires
    }
}
