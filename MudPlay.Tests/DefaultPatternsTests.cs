using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

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

    [Theory]
    // Every reply that means "you didn't move". The three refusal lines come from a
    // realm-custom level cap that isn't in the stock 1.11p data, so routing can't
    // avoid it in advance — recognising the refusal is the only way the walker
    // learns the step failed instead of stalling (report stock-20260913-233911).
    [InlineData("There is no exit in that direction!")]
    [InlineData("The door is closed in that direction!")]
    [InlineData("You have progressed too far for this room.")]
    [InlineData("You have progressed too far to go through this exit!")]
    [InlineData("You are not permitted in that room!")]
    public void DirectionFailedRegex_MatchesEveryRefusal(string line)
        => Assert.True(PatternById(KnownPatterns.DirectionFailed).TryMatch(Line(line), out _));

    [Fact]
    public void DirectionFailedRegex_DoesNotSwallowTheTrainerRefusal()
        // Same opening words, entirely different event — it's a failed TRAIN, not a
        // failed move, and the train loop branches on it.
        => Assert.False(PatternById(KnownPatterns.DirectionFailed).TryMatch(
            Line("You have progressed too far to use the training provided here."), out _));

    [Theory]
    // The live stock reply to `open <dir>` on a door someone else already opened is
    // PAST tense. Matching only the present form left it unrecognised, so the door
    // FSM sat in WaitingOpen until it timed out and failed the loop step with "door
    // open failed" — on a door that was standing open (report stock-20260913-232235).
    [InlineData("The door was already open.")]
    [InlineData("The door is already open.")]
    [InlineData("The gate was already open.")]
    [InlineData("The gate is already open.")]
    public void DoorAlreadyOpenRegex_MatchesBothTenses(string line)
        => Assert.True(PatternById(KnownPatterns.DoorAlreadyOpen).TryMatch(Line(line), out _));

    [Theory]
    [InlineData("The door was already locked.")]
    [InlineData("The chest was already open.")]
    public void DoorAlreadyOpenRegex_DoesNotOverreach(string line)
        => Assert.False(PatternById(KnownPatterns.DoorAlreadyOpen).TryMatch(Line(line), out _));

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
    public void UserHitsRegex_MatchesApostropheInSpellName()
    {
        // "You summon god's wrath upon <target> for N damage!" (the gwra
        // alt-attack spell) went completely unmatched — the apostrophe in
        // "god's" broke the target capture's character class, so no
        // UserHits subscriber (round/monster-observation stats, the
        // idle-stall watchdog's activity clock, attack-cast confirmation)
        // ever saw a gwra hit land. Report paradigm-20260914-055853.
        IMessagePattern p = PatternById(KnownPatterns.UserHits);

        Assert.True(p.TryMatch(
            Line("You summon god's wrath upon whale shark for 121 damage!"), out MatchResult r));
        Assert.Equal("You",                          r.Groups[0]);
        Assert.Equal("god's wrath upon whale shark",  r.Groups[1]);
        Assert.Equal("121",                           r.Groups[2]);
    }

    [Fact]
    public void MobAttacksYouRegex_RequiresTheArticle()
    {
        // MUST stay article-required: dropping it (tried once, reverted) makes
        // this match ordinary non-combat "<name> <verb> you" lines too — party
        // gives/tells, and even the player's own self-buff visual line — which
        // OnAnyCombatLine (its sole subscriber) takes as proof of live combat,
        // wrongly blocking rest and permanently feeding the idle-stall watchdog's
        // clock off unrelated chat. Confirmed false-positive-free against real
        // session logs only once "The " stayed required. See UserDodges for the
        // correctly-scoped fix for the dodge shape that motivated the attempt.
        IMessagePattern p = PatternById(KnownPatterns.MobAttacksYou);

        Assert.False(p.TryMatch(Line("Bob gives you a longsword."), out _));
        Assert.False(p.TryMatch(Line("A shimmering golden mist descends over you!"), out _));
        Assert.True(p.TryMatch(Line("The whale shark lunges at you!"), out _));
    }

    [Fact]
    public void UserDodgesRegex_MatchesArticlelessBlankVerbDodge()
    {
        // Paradigm's full-dodge wording drops both the leading "The" and the
        // attack verb ("whale shark  at you, but you dodge out of the way!",
        // confirmed on the wire across two dozen distinct monster names) — now
        // wired into CombatStateTracker's activity clock (it wasn't before), so
        // this shape counts as combat activity without widening MobAttacksYou's
        // much larger blast radius. Report paradigm-20260914-055853.
        IMessagePattern p = PatternById(KnownPatterns.UserDodges);

        Assert.True(p.TryMatch(
            Line("whale shark  at you, but you dodge out of the way!"), out _));
        // A second, differently-shaped dodge line seen in the same logs — no
        // article either, but with its own verb ("reaches"), confirming the
        // fix isn't narrowly tied to the blank-verb case specifically.
        Assert.True(p.TryMatch(
            Line("Reaches for you with a tentacle, but you dodge!"), out _));
        // The normal, article-led wording must still match.
        Assert.True(p.TryMatch(
            Line("The kobold thief lunges at you, but you dodge!"), out _));
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
    public void IncomingDamageRegex_IsArticleFree_AndOnlyOurOwnHealth()
    {
        IMessagePattern p = PatternById(KnownPatterns.IncomingDamage);

        // The ordinary case, and the one MobHits already covered.
        Assert.True(p.TryMatch(Line("The bugbear captain cleaves you for 8 damage!"),
                               out MatchResult r));
        Assert.Equal("8", r.Groups[1]);

        // A PROPER NOUN TAKES NO ARTICLE — the game prints "Goru-Nezar swings
        // at you!", never "The Goru-Nezar ...". MobHits requires the article, so
        // every named monster's attack line was invisible to it.
        Assert.True(p.TryMatch(Line("Goru-Nezar whomps you for 40 damage!"), out r));
        Assert.Equal("40", r.Groups[1]);
        Assert.True(p.TryMatch(Line("Lady Sentara smites you for 55 damage!"), out _));

        // Damage whose line names no attacker at all — most spell wordings.
        Assert.True(p.TryMatch(Line("Evil vibrations tear through you for 23 damage!"), out _));
        Assert.True(p.TryMatch(Line("A shining spark strikes you for 12 damage!"), out _));
        Assert.True(p.TryMatch(Line("The spinefin casts acid jet on you for 3 damage!"), out _));

        // OUR OWN swing names a number too, and is not damage to us.
        Assert.False(p.TryMatch(Line("You hit bugbear captain for 41 damage!"), out _));
        Assert.False(p.TryMatch(Line("You critically hit dark monk for 14 damage!"), out _));
        // A blow on a party member: the literal "you for" is what makes it ours.
        Assert.False(p.TryMatch(Line("The bugbear captain cleaves Bob for 8 damage!"), out _));
        // Chatter, and a miss (no number).
        Assert.False(p.TryMatch(Line("The barmaid smiles at you."), out _));
        Assert.False(p.TryMatch(Line("Goru-Nezar swings at you!"), out _));
    }

    [Fact]
    public void RoomEntryArrivalRegex_MatchesTheServersGenericForms()
    {
        IMessagePattern p = PatternById(KnownPatterns.RoomEntryArrival);

        // THE STOCK TYPO: the compass arrival omits the word "room", while its two
        // vertical siblings carry it. That one string is what a monster prints when
        // it pursues you or wanders in, so missing it meant auto-combat never
        // learned the mob was in the room — unrecoverable in a dark room, which
        // never re-displays an "Also here:" to fix the roster.
        Assert.True(p.TryMatch(Line("bugbear captain moves into the from the northeast."),
                               out MatchResult r));
        Assert.Equal("bugbear captain", r.Groups[0]);
        Assert.Equal("northeast",       r.Groups[1]);

        // The complete forms, unchanged.
        Assert.True(p.TryMatch(Line("bugbear captain moves into the room from above."), out _));
        Assert.True(p.TryMatch(Line("bugbear captain moves into the room from below."), out _));
        Assert.True(p.TryMatch(Line("bugbear captain moves into the room from nowhere."), out _));
        // A monster's OWN entrance sentence, which always matched.
        Assert.True(p.TryMatch(Line("A tall elite orc guard strides in from the east!"), out _));

        // The spawn path's other wording, with no "in"/"into" at all.
        Assert.True(p.TryMatch(Line("bugbear captain just arrived from nowhere."), out r));
        Assert.Equal("bugbear captain", r.Groups[0]);
        Assert.Equal("nowhere",         r.Groups[1]);

        // ...and that branch demands a REAL direction, because "<name> just
        // arrived from <somewhere>" is a shape ordinary chat produces.
        Assert.False(p.TryMatch(Line("Bob just arrived from the store."), out _));
        Assert.False(p.TryMatch(Line("Bob just arrived from downtown."), out _));
        // SneakArrivalNotice owns the sneak line.
        Assert.False(p.TryMatch(Line("You notice Bob sneaking in from the east."), out _));
    }

    [Fact]
    public void RoomEntryDepartureRegex_MatchesTheServersGenericForms()
    {
        IMessagePattern p = PatternById(KnownPatterns.RoomEntryDeparture);

        // The mirror of the arrival: a monster with no departure sentence of its
        // own leaves with "just left", which carries neither "out" nor "the room",
        // so nothing dropped it from the roster and the engine kept swinging at a
        // monster that had walked away.
        Assert.True(p.TryMatch(Line("bugbear captain just left to the northeast."),
                               out MatchResult r));
        Assert.Equal("bugbear captain", r.Groups[0]);
        Assert.Equal("northeast",       r.Groups[1]);

        // The vertical pair spells the direction as an adverb, with no "to the".
        Assert.True(p.TryMatch(Line("bugbear captain just left upwards."), out r));
        Assert.Equal("upwards", r.Groups[1]);
        Assert.True(p.TryMatch(Line("bugbear captain just left downwards."), out _));

        // The two shapes confirmed from real logs, unchanged.
        Assert.True(p.TryMatch(Line("The orc rogue walks out of the room to the above!"), out _));
        Assert.True(p.TryMatch(Line("dark goblin archer exits the room to the northeast."), out _));
        Assert.True(p.TryMatch(Line("The kobold scurries out to the west."), out _));

        // Chat that shares the shape stays out.
        Assert.False(p.TryMatch(Line("Bob says: I just left to the store."), out _));
        Assert.False(p.TryMatch(Line("Bob gossips: I just left downtown."), out _));
    }

    [Fact]
    public void RoomSpawnArrivalRegex_MatchesDirectionlessSpawn_NotDirectionalOrTitle()
    {
        IMessagePattern p = PatternById(KnownPatterns.RoomSpawnArrival);

        // Confirmed spawn flavor (paradigm-20260908-210658): no direction, arbitrary
        // multi-word verb phrase, ending "into the room".
        Assert.True(p.TryMatch(Line("A slimeworm crashes through the ground into the room!"), out _));
        Assert.True(p.TryMatch(Line("An ancient wyrm surges up into the room."), out _));

        // Must NOT swallow the directional walk-in (RoomEntryArrival owns that) or a
        // plain room-title / exits line.
        Assert.False(p.TryMatch(Line("A cave bear lumbers in from the south!"), out _));
        Assert.False(p.TryMatch(Line("Underground Lake"), out _));
        Assert.False(p.TryMatch(Line("Obvious exits: north, southwest"), out _));
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
        Assert.False(p.TryMatch(Line("You are now following MudPlay."), out _));
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

    [Fact]
    public void LearnSpellFromItemRegex_CapturesSpellName()
    {
        IMessagePattern p = PatternById(KnownPatterns.LearnSpellFromItem);

        // ParaMud teaching-item wording ("read <code>" → "You add <name> to your spellbook!").
        Assert.True(p.TryMatch(Line("You add agony to your spellbook!"), out MatchResult r));
        Assert.Equal("agony", r.Groups[0]);

        Assert.True(p.TryMatch(Line("You add greater curse to your spellbook!"), out MatchResult r2));
        Assert.Equal("greater curse", r2.Groups[0]);

        // Unrelated "You add … to your pack." lines must NOT match.
        Assert.False(p.TryMatch(Line("You add a torch to your pack."), out _));
    }

    // A third party grabbing ground cash — count-less, "some", NO trailing period.
    // The coin plural is captured whole (CashManager keys off the leading word).
    [Fact]
    public void CashPickedUpByOtherRegex_CapturesCoinPlural()
    {
        IMessagePattern p = PatternById(KnownPatterns.CashPickedUpByOther);

        Assert.True(p.TryMatch(Line("Tristian picks up some gold crowns"), out MatchResult r));
        Assert.Equal("Tristian",    r.Groups[0]);
        Assert.Equal("gold crowns", r.Groups[1]);
    }

    // The period-terminated PlayerGets item line must NOT collide with the
    // cash-pickup-by-other pattern — the trailing "." (and "a", not "some")
    // keeps an item pickup out of the coin path.
    [Fact]
    public void CashPickedUpByOtherRegex_IgnoresPeriodTerminatedItemGet()
    {
        IMessagePattern cash = PatternById(KnownPatterns.CashPickedUpByOther);
        IMessagePattern item = PatternById(KnownPatterns.PlayerGets);

        Assert.False(cash.TryMatch(Line("Bob picks up a rusty sword."), out _));
        Assert.True(item.TryMatch(Line("Bob picks up a rusty sword."), out _));
    }
}
