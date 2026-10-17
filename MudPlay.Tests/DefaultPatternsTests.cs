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

    // Compiled regexes are shared between pattern instances. Same text with other
    // options is another regex, and each instance keeps its own id.
    [Fact]
    public void RegexPattern_SameText_SharesNothingButTheCompiledRegex()
    {
        RegexPattern strict = new("strict", "^bob waves$");
        RegexPattern again = new("again", "^bob waves$");
        RegexPattern loose = new("loose", "^bob waves$", options: System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.True(strict.TryMatch(Line("bob waves"), out MatchResult first));
        Assert.True(again.TryMatch(Line("bob waves"), out MatchResult second));
        Assert.Equal(("strict", "again"), (first.PatternId, second.PatternId));

        Assert.False(strict.TryMatch(Line("BOB WAVES"), out _));
        Assert.True(loose.TryMatch(Line("BOB WAVES"), out _));
    }

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
    public void Seed_PopulatesTheCatalog()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        Assert.All(DefaultPatterns.BuildDefaultPatterns(), p => Assert.True(router.TryGetPattern(p.Id, out _), p.Id));
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
    // The Stock engine's other exit refusals.
    [InlineData("There is a closed door in that direction!")]
    [InlineData("You have not progressed far enough to go through this exit!")]
    [InlineData("You need to cast a spell to go that way!")]
    [InlineData("You do not have the appropriate item to go that direction!")]
    [InlineData("You may not go through this exit!")]
    [InlineData("You are too good to go through this exit!")]
    [InlineData("You are too evil to go through this exit!")]
    [InlineData("You may not pass through that exit at this point in time.")]
    [InlineData("You may not drag anyone through this exit.")]
    [InlineData("Your sysop must purchase the Dragon's Teeth add-on before you may move through this exit.")]
    [InlineData("You do not have enough to cover the toll of 5 gold crowns.")]
    // "Closed" is a shut door or gate, "closed" a locked one.
    [InlineData("The gate is Closed!")]
    [InlineData("The door is Closed!")]
    // Item and ability exits print their own refusal in place of the generic line.
    [InlineData("You can barely keep afloat in this water, much less swim!")]
    [InlineData("A shimmering field blocks your passage!")]
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

    // A touch attack that misses "reaches for you" rather than swinging "at you".
    // Unmatched, a room of spirits that only missed never woke the re-engage
    // (reports paradigm-20261003-194358, paradigm-20261003-201253).
    [Fact]
    public void MobMissesRegex_MatchesAReachForYou()
    {
        IMessagePattern p = PatternById(KnownPatterns.MobMisses);

        Assert.True(p.TryMatch(Line("The vengeful spirit reaches for you!"), out MatchResult m));
        Assert.Equal("vengeful spirit", m.Groups[0]);
        Assert.True(p.TryMatch(Line("The whale shark lunges at you!"), out _));
        Assert.False(p.TryMatch(Line("The barmaid has a letter for your brother."), out _));
    }

    // Shrugging off a monster's spell is not a swing of ours, though the line starts
    // "You", names the monster and ends in "!".
    [Fact]
    public void UserMissesRegex_SkipsANegatedMonsterCast()
    {
        IMessagePattern p = PatternById(KnownPatterns.UserMisses);

        Assert.False(p.TryMatch(Line("You negate vengeful spirit's cast of necromantic beam!"), out _));
        Assert.True(p.TryMatch(Line("You punch acid slime!"), out _));
        Assert.True(p.TryMatch(Line("You swing at the kobold, but miss!"), out _));
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

    // The engine reports damage as "You took N damage." — not an item pickup.
    [Theory]
    [InlineData("You took 12 damage.")]
    [InlineData("You took 12 damage!")]
    public void PlayerGetsRegex_IgnoresTheDamageReport(string line)
        => Assert.False(PatternById(KnownPatterns.PlayerGets).TryMatch(Line(line), out _));

    [Fact]
    public void PlayerGetsRegex_StillMatchesAnItemNamedWithANumber()
        => Assert.True(PatternById(KnownPatterns.PlayerGets).TryMatch(Line("You took 3 orc-head."), out _));

    // Each trapped exit prints its own "disarm failed and set it off" wording.
    [Theory]
    [InlineData("You try to disarm the trap, but instead trigger it!")]
    [InlineData("You trigger the trap, and a large spear shoots out!")]
    [InlineData("You fail to disarm the trap, and blades sweep out and slice you!")]
    [InlineData("You attempt to disarm the trap, but trigger it instead!")]
    [InlineData("An arrow shoots out of the wall and strikes you!")]
    public void TrapDisarmTriggeredRegex_MatchesEachTrapsWording(string line)
        => Assert.True(PatternById(KnownPatterns.TrapDisarmTriggered).TryMatch(Line(line), out _));

    // Our alignment moved: the game stripped gear the new band can't wear, or a
    // victim forgave us.
    [Fact]
    public void AlignmentMovedLines_Match()
    {
        Assert.True(PatternById(KnownPatterns.AlignmentGearRemoved)
            .TryMatch(Line("Your white gold ring has been removed."), out _));
        Assert.True(PatternById(KnownPatterns.AlignmentForgiven)
            .TryMatch(Line("The gods have forgiven you for your action."), out _));
        Assert.False(PatternById(KnownPatterns.AlignmentGearRemoved)
            .TryMatch(Line("Raijin has been removed from your followers."), out _));
    }

    // The Stock engine words a light's burn-out per item; only the torch flickers.
    [Theory]
    [InlineData("Your torch flickers and goes out.")]
    [InlineData("Your lamp runs out of oil, and goes out.")]
    [InlineData("The light in the moon-lamp flickers, and goes out.")]
    [InlineData("Your light ball disappears out of existance.")]
    [InlineData("scaled lantern is no longer lit!")]
    [InlineData("glowing pearl is no longer lit!")]
    public void LightBurnedOutRegex_MatchesEachStockLightsLine(string line)
        => Assert.True(PatternById(KnownPatterns.LightBurnedOut).TryMatch(Line(line), out _));

    [Theory]
    [InlineData("You lit the torch.")]
    [InlineData("You do not have torch lit.")]
    [InlineData("You have removed torch and extinguished it.")]
    [InlineData("Raijin's torch just went out.")]             // another player's light
    [InlineData("A shimmering rune flickers and fades.")]
    public void LightBurnedOutRegex_IgnoresOtherLightLines(string line)
        => Assert.False(PatternById(KnownPatterns.LightBurnedOut).TryMatch(Line(line), out _));

    // The wording on record says "sneaking in from"; the Stock engine's strings say
    // "sneak in from", with above / below for the vertical pair and a drag suffix.
    [Theory]
    [InlineData("You notice Bob sneaking in from the east.", "east")]
    [InlineData("You notice Bob sneak in from the east.", "east")]
    [InlineData("You notice Bob sneak in from above.", "above")]
    [InlineData("You notice Bob sneak in from below.", "below")]
    [InlineData("You notice Bob sneak in from the northwest (Dragging Raijin).", "northwest")]
    public void SneakArrivalRegex_ReadsBothWordings(string line, string direction)
    {
        Assert.True(PatternById(KnownPatterns.SneakArrivalNotice).TryMatch(Line(line), out MatchResult r));
        Assert.Equal("Bob", r.Groups[0]);
        Assert.Equal(direction, r.Groups[1]);
        // The generic arrival must not take it as a monster named "You notice Bob".
        Assert.False(PatternById(KnownPatterns.RoomEntryArrival).TryMatch(Line(line), out _));
    }

    // The departure form shown to the room being left is not an arrival.
    [Theory]
    [InlineData("You notice Bob sneaking out to the east.")]
    [InlineData("You notice Bob sneaking out upwards.")]
    public void SneakArrivalRegex_IgnoresTheSneakingOutForm(string line)
        => Assert.False(PatternById(KnownPatterns.SneakArrivalNotice).TryMatch(Line(line), out _));

    // The room's line for a player's death: "has died." on record, "is dead." from
    // the Stock engine.
    [Theory]
    [InlineData("Raijin has died.")]
    [InlineData("Raijin is dead.")]
    public void PartyMemberDiedRegex_ReadsBothWordings(string line)
    {
        Assert.True(PatternById(KnownPatterns.PartyMemberDied).TryMatch(Line(line), out MatchResult r));
        Assert.Equal("Raijin", r.Groups[0]);
    }

    // A monster's own death sentence can end the same way; it names no one.
    [Theory]
    [InlineData("The roc hatchling lets out a squeal, and is dead.")]
    [InlineData("The crazed lunatic hugs his dead cat, and dies.")]
    public void PartyMemberDiedRegex_IgnoresAMonstersDeathSentence(string line)
        => Assert.False(PatternById(KnownPatterns.PartyMemberDied).TryMatch(Line(line), out _));

    // A Stock `buy` that bought nothing, for a reason other than the price.
    [Theory]
    [InlineData("You cannot buy rope and grapple here!")]
    [InlineData("rope is not a known item.")]
    [InlineData("You cannot carry that much!")]
    [InlineData("A strange force stops you from getting this item.")]
    public void UserBuyRefusedRegex_MatchesTheStockRefusals(string line)
        => Assert.True(PatternById(KnownPatterns.UserBuyRefused).TryMatch(Line(line), out _));

    // What each refusal names, so a consumer can tell whose buy it answers.
    [Theory]
    [InlineData("You cannot buy rope and grapple here!", "rope and grapple", "")]
    [InlineData("rope is not a known item.", "", "rope")]
    [InlineData("You cannot carry that much!", "", "")]
    [InlineData("A strange force stops you from getting this item.", "", "")]
    public void UserBuyRefusedRegex_CarriesTheWareOrTheTypedWords(string line, string ware, string typed)
    {
        Assert.True(PatternById(KnownPatterns.UserBuyRefused).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(ware, r.Groups[0]);
        Assert.Equal(typed, r.Groups[1]);
    }

    [Fact]
    public void UserBuyNotInShopRegex_MatchesTheEnginesLine()
    {
        Assert.True(PatternById(KnownPatterns.UserBuyNotInShop).TryMatch(
            Line("You cannot BUY if you are not in a shop!"), out _));
        Assert.False(PatternById(KnownPatterns.UserBuyNotInShop).TryMatch(
            Line("You cannot SELL if you are not in a shop!"), out _));
    }

    [Theory]
    [InlineData("You cannot afford rope and grapple.")]       // UserBuyFailed's
    [InlineData("You cannot sell rope and grapple here.")]    // UserSellRefused's
    [InlineData("You just bought rope and grapple for 1 gold crown, 5 silver nobles.")]
    public void UserBuyRefusedRegex_LeavesTheOtherShopLinesAlone(string line)
        => Assert.False(PatternById(KnownPatterns.UserBuyRefused).TryMatch(Line(line), out _));

    // The two wear refusals that say nothing about who may wear the item. They have
    // a pattern of their own so the alignment-reading refusal handler never sees them.
    [Theory]
    [InlineData("painted shield may not be worn!", "painted shield")]
    [InlineData("You have no more room to wear that item!", "")]
    public void UserEquipCannotBeWornRegex_MatchesBothLines(string line, string item)
    {
        Assert.True(PatternById(KnownPatterns.UserEquipCannotBeWorn).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(item, r.Groups[0]);
        Assert.False(PatternById(KnownPatterns.UserEquipFailed).TryMatch(Line(line), out _));
    }

    [Fact]
    public void UserEquipCannotBeWornRegex_LeavesTheAlignmentRefusalAlone()
        => Assert.False(PatternById(KnownPatterns.UserEquipCannotBeWorn).TryMatch(
            Line("You may not wear that item!"), out _));

    // Item names carry hyphens, apostrophes and full stops in both data sets; the
    // wear and remove lines of such an item were once not read at all.
    [Theory]
    [InlineData("You are now wearing phoenix feather.", "phoenix feather")]
    [InlineData("You are now wearing gnomish fish-helm.", "gnomish fish-helm")]
    [InlineData("You are now wearing rime-runed cuirass.", "rime-runed cuirass")]
    [InlineData("You are now wearing giant's girdle.", "giant's girdle")]
    [InlineData("You lit the torch.", "torch")]
    public void UserEquippedRegex_ReadsEveryKindOfItemName(string line, string item)
    {
        Assert.True(PatternById(KnownPatterns.UserEquipped).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(item, r.Groups[0]);
    }

    [Theory]
    [InlineData("You have removed silver necklace.", "silver necklace")]
    [InlineData("You have removed half-plate corselet.", "half-plate corselet")]
    [InlineData("You have removed giant's girdle.", "giant's girdle")]
    [InlineData("You have removed torch and extinguished it.", "torch")]
    public void UserRemovedRegex_ReadsEveryKindOfItemName(string line, string item)
    {
        Assert.True(PatternById(KnownPatterns.UserRemoved).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(item, r.Groups[0]);
    }

    [Fact]
    public void UserEquipCannotBeWornRegex_ReadsAHyphenatedName()
    {
        Assert.True(PatternById(KnownPatterns.UserEquipCannotBeWorn).TryMatch(
            Line("jeweled main-gauche may not be worn!"), out MatchResult r));
        Assert.Equal("jeweled main-gauche", r.Groups[0]);
    }

    // Two answers to a wear that put nothing on: the item named is the one typed in
    // the first, the piece in the way in the second.
    [Theory]
    [InlineData("You do not have phoenix feather left unequipped.", "phoenix feather")]
    [InlineData("You do not have rime-runed cuirass left unequipped.", "rime-runed cuirass")]
    public void UserEquipNotInPackRegex_NamesTheItem(string line, string item)
    {
        Assert.True(PatternById(KnownPatterns.UserEquipNotInPack).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(item, r.Groups[0]);
    }

    [Fact]
    public void UserEquipOccupantStuckRegex_NamesThePieceInTheWay()
    {
        Assert.True(PatternById(KnownPatterns.UserEquipOccupantStuck).TryMatch(
            Line("You are already wearing ring of the damned and it may not be removed."), out MatchResult r));
        Assert.Equal("ring of the damned", r.Groups[0]);
    }

    // `set follow`: the `pro` sheet's row and the command's two replies.
    [Theory]
    [InlineData("Follow Mode:        Blind", "Blind")]
    [InlineData("Follow Mode:        Normal", "Normal")]
    public void FollowModeRowRegex_ReadsTheMode(string line, string mode)
    {
        Assert.True(PatternById(KnownPatterns.FollowModeRow).TryMatch(Line(line), out MatchResult r));
        Assert.Equal(mode, r.Groups[0]);
    }

    [Fact]
    public void FollowModeReplies_Match()
    {
        Assert.True(PatternById(KnownPatterns.FollowModeSetBlind).TryMatch(
            Line("You will only see the fact that you have moved when following."), out _));
        Assert.True(PatternById(KnownPatterns.FollowModeSetNormal).TryMatch(
            Line("You will see your normal room descriptions when following."), out _));
        Assert.False(PatternById(KnownPatterns.FollowModeRow).TryMatch(
            Line("Valid follow options: BLIND, NORMAL"), out _));
    }

    // A Stock comma-list price still reads as one buy line.
    [Fact]
    public void UserBuysRegex_TakesAStockCoinListPrice()
    {
        Assert.True(PatternById(KnownPatterns.UserBuys).TryMatch(
            Line("You just bought rope and grapple for 1 gold crown, 5 silver nobles."), out MatchResult r));
        Assert.Equal("rope and grapple", r.Groups[1]);
        Assert.Equal("1 gold crown, 5 silver nobles", r.Groups[2]);
    }

    // The three lines a Stock follower gets before "You are no longer following X."
    // when it can't make a follow move; the heavy one has its own pattern.
    [Theory]
    [InlineData("You can't seem to move anywhere!")]
    [InlineData("You are too stunned to move anywhere!")]
    public void MovementFailedStuckRegex_MatchesHeldAndStunned(string line)
        => Assert.True(PatternById(KnownPatterns.MovementFailedStuck).TryMatch(Line(line), out _));

    // The `hide` refusals share the opening words and are no failed move.
    [Theory]
    [InlineData("You can't seem to move anywhere to hide!")]
    [InlineData("You are too stunned to move anywhere to hide!")]
    public void MovementFailedStuckRegex_IgnoresTheHideRefusals(string line)
        => Assert.False(PatternById(KnownPatterns.MovementFailedStuck).TryMatch(Line(line), out _));

    // The line on a row of its own, and on the end of the wait's row of dots, which
    // the game prints without a line end.
    [Theory]
    [InlineData("Your meditation has been interrupted - you may not exit now!")]
    [InlineData("................Your meditation has been interrupted - you may not exit now!")]
    public void RealmExitInterruptedRegex_MatchesTheCalledOffWait(string line)
        => Assert.True(PatternById(KnownPatterns.RealmExitInterrupted).TryMatch(Line(line), out _));

    [Theory]
    [InlineData("You may not perform any commands while waiting to exit!")]
    [InlineData("................")]
    [InlineData("Bob says \"Your meditation has been interrupted - you may not exit now!\"")]
    public void RealmExitInterruptedRegex_IgnoresTheWaitsOtherLines(string line)
        => Assert.False(PatternById(KnownPatterns.RealmExitInterrupted).TryMatch(Line(line), out _));
}
