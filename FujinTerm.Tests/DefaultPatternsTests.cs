using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pin the shape of the default pattern registry. Tests deliberately
/// validate ONE representative case per category — full regex correctness
/// gets validated in a cross-check pass against captured BBS output, not
/// here.
/// </summary>
public sealed class DefaultPatternsTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    [Fact]
    public void BuildDefaultPatterns_RegistersEveryKnownId()
    {
        HashSet<string> ids = DefaultPatterns.BuildDefaultPatterns()
            .Select(p => p.Id)
            .ToHashSet();

        // Spot-check one from each category. Full inventory is too rigid —
        // if a category gains a new pattern, this should not fail.
        Assert.Contains(KnownPatterns.UserSneaking,        ids);
        Assert.Contains(KnownPatterns.DirectionFailed,     ids);
        Assert.Contains(KnownPatterns.CommandNoEffect,     ids);
        Assert.Contains(KnownPatterns.UserSearchSucceeded, ids);
        Assert.Contains(KnownPatterns.UserHits,            ids);
        Assert.Contains(KnownPatterns.ConversationGossip,  ids);
        Assert.Contains(KnownPatterns.UserBuys,            ids);
        Assert.Contains(KnownPatterns.RoomExits,           ids);
        Assert.Contains(KnownPatterns.PlayerEnters,        ids);
    }

    [Fact]
    public void BuildDefaultPatterns_NoDuplicateIds()
    {
        List<string> ids = DefaultPatterns.BuildDefaultPatterns().Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Seed_PopulatesCatalogWithoutAttachingHandlers()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        Assert.True(router.PatternCount > 30);   // ~30+ patterns ship; pin a floor.
        Assert.Equal(0, router.SubscriptionCount);
        Assert.True(router.TryGetPattern(KnownPatterns.UserHits, out _));
    }

    private static IMessagePattern PatternById(string id)
        => DefaultPatterns.BuildDefaultPatterns().Single(p => p.Id == id);

    [Fact]
    public void GossipRegex_CapturesPlayerAndMessage()
    {
        IMessagePattern p = PatternById(KnownPatterns.ConversationGossip);

        Assert.True(p.TryMatch(Line("Forged gossips: hello world"), out MatchResult r));
        Assert.Equal("Forged",      r.Groups[0]);
        Assert.Equal("hello world", r.Groups[1]);
    }

    [Fact]
    public void UserHitsRegex_CapturesSourceTargetDamage()
    {
        IMessagePattern p = PatternById(KnownPatterns.UserHits);

        // Megamind's regex: ^(source) (?:critically )?(?:\w+) (target) for (\d+) damage!
        Assert.True(p.TryMatch(Line("Forged slashes Goblin for 17 damage!"), out MatchResult r));
        Assert.Equal("Forged", r.Groups[0]);
        Assert.Equal("Goblin", r.Groups[1]);
        Assert.Equal("17",     r.Groups[2]);
    }

    [Fact]
    public void StatusLineRegex_ParsesHpManaTypeAndState()
    {
        IMessagePattern p = PatternById(KnownPatterns.StatusLine);

        Assert.True(p.TryMatch(Line("[HP=779/MA=571 (Resting) ]:"), out MatchResult r));
        // Group order matches Megamind: hp, type, mana, statea, stateb.
        Assert.Equal("779",      r.Groups[0]);
        Assert.Equal("MA",       r.Groups[1]);
        Assert.Equal("571",      r.Groups[2]);
        Assert.Equal("Resting",  r.Groups[3]);
    }

    [Fact]
    public void RoomExitsRegex_Matches()
    {
        IMessagePattern p = PatternById(KnownPatterns.RoomExits);
        Assert.True(p.TryMatch(Line("Obvious exits: north, south, east"), out _));
    }

    [Fact]
    public void ParadigmLocationRegex_CapturesMapAndRoom()
    {
        IMessagePattern p = PatternById(KnownPatterns.ParadigmLocation);

        // The label is padded to a column, so leading/inter-token whitespace varies.
        Assert.True(p.TryMatch(Line("Location:      1,1729"), out MatchResult r));
        Assert.Equal("1",    r.Groups[0]);
        Assert.Equal("1729", r.Groups[1]);
    }

    [Fact]
    public void ParadigmLocationRegex_IgnoresSiblingBlockLines()
    {
        IMessagePattern p = PatternById(KnownPatterns.ParadigmLocation);

        // The other two lines of the `rm` block must not parse as a Location.
        Assert.False(p.TryMatch(Line("Regen Time:      2m 30s"),     out _));
        Assert.False(p.TryMatch(Line("Room Illu:      -100 (-100)"), out _));
    }

    [Fact]
    public void CombatStatusRegex_CapturesEngagedOrOff()
    {
        IMessagePattern p = PatternById(KnownPatterns.CombatStatus);
        Assert.True(p.TryMatch(Line("*Combat Engaged*"), out MatchResult on));
        Assert.True(p.TryMatch(Line("*Combat Off*"),     out MatchResult off));
        Assert.Equal("Engaged", on.Groups[0]);
        Assert.Equal("Off",     off.Groups[0]);
    }

    [Fact]
    public void RerollRegex_MatchesSuicideLine()
    {
        IMessagePattern p = PatternById(KnownPatterns.Reroll);
        Assert.True(p.TryMatch(Line("After a LONG thought, you take your own life."), out _));
        Assert.False(p.TryMatch(Line("You think for a moment."), out _));
    }

    [Fact]
    public void PartyFollowMoveRegex_CapturesDragDirection()
    {
        IMessagePattern p = PatternById(KnownPatterns.PartyFollowMove);

        // The real drag line carries a leading space before the dashes.
        Assert.True(p.TryMatch(Line(" -- Following your Party leader northeast --"), out MatchResult r));
        Assert.Equal("northeast", r.Groups[0]); // longest-first alternation keeps the compound intact

        Assert.True(p.TryMatch(Line(" -- Following your Party leader up --"), out MatchResult r2));
        Assert.Equal("up", r2.Groups[0]);

        // A non-drag follow line must not match.
        Assert.False(p.TryMatch(Line("You are now following Fujin."), out _));
    }

    [Fact]
    public void LearnSpellRegex_CapturesSpellName()
    {
        IMessagePattern p = PatternById(KnownPatterns.LearnSpell);

        Assert.True(p.TryMatch(Line("You read scroll of cause harm and learn the spell harm."), out MatchResult r));
        Assert.Equal("harm", r.Groups[0]);

        // Multi-word spell name — lazy capture stops at the trailing period.
        Assert.True(p.TryMatch(Line("You read a scroll and learn the spell minor heal."), out MatchResult r2));
        Assert.Equal("minor heal", r2.Groups[0]);
    }
}
