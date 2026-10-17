using System.IO;
using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Game.Conditions;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// One applied line, several spells: which records latch goes by who could have cast
// each where the character stands. The message records are the Paradigm seed's for
// the freezing family, the game rows SpellCasterIndexTests' copies of the Paradigm
// set's. Where the character stands is given the way the client gives it, through
// CastReach.Standing, so each thing it goes by is one a test can take away.
public sealed class ConditionTrackerCastReachTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _root;
    private readonly LogService _log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
    private readonly MessageStore _messages = new();
    private readonly SpellCasterIndex _casters;
    private readonly ConditionTracker _tracker;
    private readonly List<MessageRecord> _applied = new();

    // What the client holds of the room: a lit room with no spell, its position
    // confirmed, and a roster read whole that lists nobody.
    private RoomConfidence _confidence = RoomConfidence.Confirmed;
    private int _roomSpell;
    private bool _dark;
    private bool _enteredBlind;
    private bool _blinded;
    private RoomEntitiesObservation? _roster = Roster();
    private bool _rosterIsWhole = true;
    // Off a PvP realm unless a test says so, with nobody in the party.
    private bool _pvp;
    private readonly HashSet<string> _party = new(StringComparer.OrdinalIgnoreCase);

    public ConditionTrackerCastReachTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-cast-reach-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        (_, _casters) = SpellCasterIndexTests.NewIndex(_root);

        _tracker = new ConditionTracker(_messages, _log);
        _tracker.AttachLineExtractor(_feed.Lines);
        _tracker.ConditionApplied += _applied.Add;
        _tracker.SetCastReach(new CastReach(_casters, Standing));
    }

    private CastReach.Here? Standing() => CastReach.Standing(
        _confidence, RoomWith(_roomSpell), _dark, _enteredBlind, _blinded,
        _roster, _rosterIsWhole, _casters.KnowsMonster, _pvp, _party.Contains);

    private static Room RoomWith(int spell) => new()
    {
        Key = new RoomKey(1, 1), Name = "Somewhere", Spell = spell,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    public void Dispose()
    {
        _tracker.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static MessageRecord Record(
        string name, MessageFlags flags, string target, string applied, string endsWith, params int[] spells) =>
        new(
            Id: MessageRecord.ComputeId(name, "", target, "", applied, endsWith),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: target,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith,
            Links: spells.Select(n => new GameDataLink("Spells", n)).ToArray());

    // glacial mist (#1042) can be learned by players; galacial mist (#1336), the two
    // monster ice storms (#1363, #5027, one record text under two links, as the seed
    // has it) are cast by monsters alone; freezing (#1015) comes at the end of a
    // blizzard, one of which can be learned.
    private void AddTheFreezingFamily(bool withTheLearnableMist = true)
    {
        _messages.Messages.Add(Record("freezing water", MessageFlags.None,
            "You are freezing cold!", "{null}", "{null}", 511, 5242));
        if (withTheLearnableMist)
            _messages.Messages.Add(Record("glacial mist", MessageFlags.Confused,
                "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 1042));
        _messages.Messages.Add(Record("galacial mist", MessageFlags.Confused,
            "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 1336));
        _messages.Messages.Add(Record("ice storm", MessageFlags.None,
            "A freezing storm of ice surrounds you!", "You are freezing", "You are no longer freezing", 1363));
        _messages.Messages.Add(Record("ice storm", MessageFlags.None,
            "A freezing storm of ice surrounds you!", "You are freezing", "You are no longer freezing", 5027));
        _messages.Messages.Add(Record("freezing", MessageFlags.None,
            "Icy hail pelts you, freezing your bones!", "You are freezing", "You are no longer freezing", 1015));
    }

    private void Feed(string text) => FeedAt(text, DateTimeOffset.UtcNow);

    private void FeedAt(string text, DateTimeOffset when) => _feed.Line(text, when);

    private IEnumerable<string> AppliedNames => _applied.Select(r => r.Name);

    private static RoomEntitiesObservation Roster(params string[] monsters) =>
        new("Also here: …", monsters.Select(Monster).ToArray(), DateTimeOffset.UtcNow);

    private static RoomEntity Monster(string name) => new("fierce " + name, name, EntityKind.Monster, 1);

    private static RoomEntity Player(string name) => new(name, name, EntityKind.Player, null);

    private void Monsters(params string[] names) => _roster = Roster(names);

    private void Present(params RoomEntity[] who) => _roster = new("Also here: …", who, DateTimeOffset.UtcNow);

    // ----- the freezing family ---------------------------------------

    [Fact]
    public void AnIceStormCasterHere_AndNoMistCaster_LatchesTheStormAndNoConfusion()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");

        Feed("You are freezing");

        Assert.Contains("ice storm", AppliedNames);
        Assert.DoesNotContain("galacial mist", AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // The same room with the whole family. Glacial mist and the blizzard that ends in
    // freezing can be learned, and are hostile: with no player here outside the
    // party, nobody here casts them (user, 2026-10-17).
    [Fact]
    public void AnIceStormCasterHere_TheMistAPlayerCanLearn_IsLeftOut_WithNoPlayerToCastIt()
    {
        AddTheFreezingFamily();
        Monsters("ice sorceress");

        Feed("You are freezing");

        Assert.Equal(new[] { "ice storm" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
        Assert.Contains(_log.Snapshot(), e =>
            e.Message.Contains("'glacial mist' (Confused)", StringComparison.Ordinal)
            && e.Message.Contains("player outside the party", StringComparison.Ordinal));
    }

    // On a PvP realm a player outside the party can cast them at the character.
    [Fact]
    public void OnAPvpRealm_APlayerOutsideTheParty_KeepsTheLearnableMistInReach()
    {
        AddTheFreezingFamily();
        _pvp = true;
        Present(Monster("ice sorceress"), Player("Bob"));

        Feed("You are freezing");

        Assert.Equal(
            new[] { "freezing", "glacial mist", "ice storm" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsConfused);
    }

    // A party member's mist doesn't land on the character, and off a PvP realm nobody
    // else's does either (user, 2026-10-17).
    [Theory]
    [InlineData(true, true)]     // a PvP realm, the player in the party
    [InlineData(false, false)]   // not a PvP realm, the player not in the party
    public void APartyMember_OrAPlayerOffAPvpRealm_DoesNotKeepTheMistInReach(bool pvp, bool inParty)
    {
        AddTheFreezingFamily();
        _pvp = pvp;
        if (inParty) _party.Add("Bob");
        Present(Monster("ice sorceress"), Player("Bob"));

        Feed("You are freezing");

        Assert.Equal(new[] { "ice storm" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // On a PvP realm, a room that can't be seen, a roster that may be short, or a
    // name nobody can place leaves the players unknown, and the mist in.
    [Theory]
    [InlineData("dark")]
    [InlineData("not whole")]
    [InlineData("unplaced name")]
    public void OnAPvpRealm_PlayersNotKnown_LeaveTheLearnableMistIn(string why)
    {
        AddTheFreezingFamily();
        _pvp = true;
        _dark = why == "dark";
        _rosterIsWhole = why != "not whole";
        _party.Add("Bob");
        Present(Monster("ice sorceress"), Player("Bob"),
            new RoomEntity("Shadowy", "Shadowy", why == "unplaced name" ? EntityKind.Unknown : EntityKind.Player, null));
        if (why != "unplaced name") _party.Add("Shadowy");

        Assert.Null(Standing()!.Value.Strangers);

        Feed("You are freezing");

        Assert.Contains("glacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    // Made-up records sharing one applied text: the white queen's (#1336), a
    // learnable single-target hostile spell (#9107, Targets 8, otherwise the ice
    // sorceress's) and a learnable single-target buff (#9112, Targets 2).
    private void AddALearnableCurseAndBuff()
    {
        _messages.Messages.Add(Record("queen's chill (test)", MessageFlags.None,
            "{null}", "You feel a chill", "The chill passes", 1336));
        _messages.Messages.Add(Record("learned curse (test)", MessageFlags.Confused,
            "{null}", "You feel a chill", "The chill passes", 9107));
        _messages.Messages.Add(Record("learned buff (test)", MessageFlags.None,
            "{null}", "You feel a chill", "The chill passes", 9112));
    }

    // A single-target hostile spell is ruled out like the mist; a buff players learn
    // never is, whoever is here.
    [Fact]
    public void ALearnableSingleTargetCurse_IsLeftOut_AndALearnableBuffIsNot()
    {
        AddALearnableCurseAndBuff();
        _pvp = true;
        _party.Add("Jroc");
        Present(Monster("white queen"), Player("Jroc"));

        Feed("You feel a chill!");

        Assert.Equal(
            new[] { "learned buff (test)", "queen's chill (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.False(_tracker.IsConfused);
    }

    [Fact]
    public void ALearnableSingleTargetCurse_IsInReach_OfAPlayerOutsideThePartyOnAPvpRealm()
    {
        AddALearnableCurseAndBuff();
        _pvp = true;
        Present(Monster("white queen"), Player("Bob"));

        Feed("You feel a chill!");

        Assert.Equal(3, _applied.Count);
        Assert.True(_tracker.IsConfused);
    }

    // Off a PvP realm no player can aim the mist at the character, seen or not.
    [Fact]
    public void OffAPvpRealm_NoPlayerCanCastAtUs_EvenInTheDark()
    {
        _dark = true;
        Present(Player("Bob"), new RoomEntity("Shadowy", "Shadowy", EntityKind.Unknown, null));

        Assert.Empty(Standing()!.Value.Strangers!);
    }

    // The white queen casts galacial mist and the other ice storm.
    [Fact]
    public void AMistCasterHere_LatchesConfusedAsBefore()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("white queen");

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.Contains("ice storm", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void TheReportsLine_InTheUnderwaterPassage_LatchesNothing()
    {
        AddTheFreezingFamily();
        _roomSpell = 511;

        Feed("You are freezing cold!");

        Assert.Empty(_applied);
        Assert.Equal(MessageFlags.None, _tracker.ActiveFlags);
    }

    [Fact]
    public void WithNoCasterOfAnyOfThemInSight_EveryRecordLatchesAsBefore()
    {
        AddTheFreezingFamily();
        Monsters();

        Feed("You are freezing");

        Assert.Equal(4, _applied.Count);   // the two ice storm records are one Id
        Assert.True(_tracker.IsConfused);
    }

    // ----- what the roster and the position have to be ---------------
    // Each of these is the first test's room, where galacial mist is left out, with
    // one thing taken away, and then it is not.

    [Theory]
    [InlineData(RoomConfidence.Unknown)]
    [InlineData(RoomConfidence.Pending)]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Lost)]
    [InlineData(RoomConfidence.PendingRespawn)]
    public void WithThePositionNotConfirmed_NothingIsLeftOut(RoomConfidence confidence)
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _confidence = confidence;

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("entered blind")]
    [InlineData("blinded")]
    public void InARoomThatCantBeSeen_AMonstersSpellIsNotLeftOut(string why)
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _dark = why == "dark";
        _enteredBlind = why == "entered blind";
        _blinded = why == "blinded";

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void WithARosterNotKnownToBeWhole_AMonstersSpellIsNotLeftOut()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _rosterIsWhole = false;

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }


    // ----- a chain from the room's spell ------------------------------

    // Two records that are not the seeds', sharing an applied text: one for drowning
    // (#513), which the Underwater Passage's freezing water (#511) reaches through
    // holding breath (#512); one for galacial mist (#1336), which only the white
    // queen casts.
    private void AddAChainedPair(MessageFlags monstersFlags)
    {
        _messages.Messages.Add(Record("drowning (test)", MessageFlags.None,
            "{null}", "You are choking for air", "You can breathe again", 513));
        _messages.Messages.Add(Record("the queen's (test)", monstersFlags,
            "{null}", "You are choking for air", "You can breathe again", 1336));
    }

    [Fact]
    public void ASpellTheRoomsSpellEndsIn_IsCastHere_AndTheMonstersIsLeftOut()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;

        Feed("You are choking for air!");

        Assert.Equal(new[] { "drowning (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
        Assert.Contains(_log.Snapshot(), e =>
            e.Message.Contains("cast here: 'drowning (test)' (room spell #511)", StringComparison.Ordinal)
            && e.Message.Contains("'the queen's (test)' (Confused)", StringComparison.Ordinal));
    }

    // The room's half still stands in the dark, since the room's spell is the map's;
    // the monsters' half does not, and a monster's spell is then not ruled out.
    [Fact]
    public void ASpellTheRoomsSpellEndsIn_InTheDark_LeavesTheMonstersSpellIn()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;
        _dark = true;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void ASpellTheRoomsSpellEndsIn_WithNoRosterRead_LeavesTheMonstersSpellIn()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;
        _roster = null;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsConfused);
    }

    // The queen is here and the room's spell is another: hers is cast here, and
    // drowning, which a textblock also casts, is never ruled out. Both latch.
    [Fact]
    public void WithTheMonsterHere_InAnotherRoom_BothLatch_DrowningHavingACasterNotInView()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 829;
        Monsters("white queen");

        Feed("You are choking for air!");

        Assert.Equal(
            new[] { "drowning (test)", "the queen's (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsConfused);
    }

    // ----- what is never left out -------------------------------------

    // What stops the character moving, fighting or seeing is latched whoever the
    // roster lists: a monster can have followed in with no line to say so.
    [Theory]
    [InlineData(MessageFlags.MovementPrevented)]
    [InlineData(MessageFlags.AttackPrevented)]
    [InlineData(MessageFlags.Blinded)]
    [InlineData(MessageFlags.Confused | MessageFlags.MovementPrevented)]
    public void AHold_AnAttackStop_ABlindness_IsNeverLeftOut(MessageFlags flags)
    {
        AddAChainedPair(flags);
        _roomSpell = 511;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.Equal(flags, _tracker.ActiveFlags);
    }

    // The lines of the next three tests are made up, so that two spells whose cast
    // lines differ share an applied text; the spells and their casters are the set's.
    private void AddTwoSpraysWithTheirOwnCastLines()
    {
        _messages.Messages.Add(Record("red spray (test)", MessageFlags.Confused,
            "The {source} sprays a red cloud at you!", "You feel dizzy", "Your head clears", 1336));
        _messages.Messages.Add(Record("blue spray (test)", MessageFlags.None,
            "The {source} sprays a blue cloud at you!", "You feel dizzy", "Your head clears", 1363));
    }

    // The red spray is the white queen's alone and she is not listed; the blue is the
    // ice sorceress's, who is. The red one's own cast line was just seen: it is the
    // one applied, whoever is listed.
    [Fact]
    public void ACastLineJustSeen_Decides_AheadOfWhoIsHere()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy!");

        Assert.Equal(new[] { "red spray (test)" }, AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void WithNoCastLineSeen_WhoIsHereDecides()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        Monsters("ice sorceress");

        Feed("You feel dizzy!");

        Assert.Equal(new[] { "blue spray (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // A line holding two applied texts. The red spray's cast line was seen, so the
    // blue one is out and the red one in; the burn is the ice sorceress's, who is
    // here, and without the cast line the red spray would be left out beside it.
    [Fact]
    public void ARecordItsCastLineSinglesOut_IsNotLeftOut_BesideAnotherTextsRecord()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        _messages.Messages.Add(Record("burn (test)", MessageFlags.None,
            "{null}", "your skin burns", "The burning stops", 1363));
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy and your skin burns!");

        Assert.Equal(new[] { "burn (test)", "red spray (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsConfused);
    }

    // The cast line speaks before who is here is asked: the blue spray it rules out
    // is the only record with a caster in the room, and with it gone nobody listed
    // casts any of the rest, so the ache, a spell of the absent queen's, stays.
    [Fact]
    public void WhoIsHere_IsAskedOnlyOfWhatTheCastLineLeavesStanding()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        _messages.Messages.Add(Record("ache (test)", MessageFlags.Poisoned,
            "{null}", "your bones ache", "The ache passes", 1336));
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy and your bones ache!");

        Assert.Equal(new[] { "ache (test)", "red spray (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsPoisoned);
    }

    // One cast line shared by every record says nothing of which was cast, and who is
    // here still decides.
    [Fact]
    public void ACastLineAllOfThemShare_ProtectsNoneOfThem()
    {
        _messages.Messages.Add(Record("red spray (test)", MessageFlags.Confused,
            "The {source} sprays a cloud at you!", "You feel dizzy", "Your head clears", 1336));
        _messages.Messages.Add(Record("blue spray (test)", MessageFlags.None,
            "The {source} sprays a cloud at you!", "You feel dizzy", "Your head clears", 1363));
        Monsters("ice sorceress");

        Feed("The ice sorceress sprays a cloud at you!");
        Feed("You feel dizzy!");

        Assert.Equal(new[] { "blue spray (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // ----- CastReach, asked directly ----------------------------------

    private CastReach.Verdict VerdictOn(int spell, CastReach.Here here) =>
        new CastReach(_casters, () => here).Of(Record("r", MessageFlags.None, "", "x", "y", spell), here, out _);

    [Fact]
    public void AMonstersSpell_IsRuledOutOnlyByARosterThatIsKnown()
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, Nobody, Nobody)));
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, new[] { "ice sorceress" }, Nobody)));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(1336, new CastReach.Here(0, Monsters: null, Nobody)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(1336, new CastReach.Here(0, new[] { "white queen" }, Nobody)));
        // Players have no part in a spell monsters alone cast.
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, Nobody, Strangers: null)));
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, Nobody, new[] { "Bob" })));
    }

    // Confusion #829 is the spell of two rooms and of no monster: the room alone
    // rules it in or out, seen or not.
    [Fact]
    public void ARoomsSpell_IsRuledOutByTheRoom_WhateverTheRoster()
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(829, new CastReach.Here(511, Monsters: null, Strangers: null)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(829, new CastReach.Here(829, Monsters: null, Strangers: null)));
    }

    [Fact]
    public void ASpellWithACasterNotInView_OrNotInTheSet_IsNeverRuledOut()
    {
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(9105, new CastReach.Here(0, Nobody, Nobody)));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(31999, new CastReach.Here(0, Nobody, Nobody)));
    }

    // A learned hostile spell (#9108 has no monster caster; glacial mist #1042
    // has some): out of reach with no player outside the party here, in reach with
    // one, unknown while the players can't be told.
    [Fact]
    public void ALearnedSpellAimedAtFoes_IsReadByThePlayersOutsideTheParty()
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(9108, new CastReach.Here(0, Monsters: null, Nobody)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(9108, new CastReach.Here(0, Monsters: null, new[] { "Bob" })));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(9108, new CastReach.Here(0, Nobody, Strangers: null)));

        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1042, new CastReach.Here(0, Nobody, Nobody)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(1042, new CastReach.Here(0, new[] { "Lorzina" }, Nobody)));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(1042, new CastReach.Here(0, Monsters: null, Nobody)));
    }

    // A hostile spell players learn only from an item's LearnSp slot (#9202) or a
    // textblock's `learnspell` (#9204), with no "Learnable" or "Learned From" to say
    // so, is read as a learnable one, not left open as an item's or textblock's cast.
    [Theory]
    [InlineData(9202)]
    [InlineData(9204)]
    public void ATaughtSpellAimedAtFoes_IsReadByThePlayersOutsideTheParty(int spell)
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(spell, new CastReach.Here(0, Nobody, Nobody)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(spell, new CastReach.Here(0, Nobody, new[] { "Bob" })));
    }

    // The Underwater Passage line again, with mists only an item or a textblock
    // teaches: the dark wizard's ice storm is here, their caster (the ice sorceress)
    // isn't, and no player outside the party is, so neither confuses.
    [Fact]
    public void AnIceStormCasterHere_MistsAnItemOrATextblockTeaches_AreLeftOut()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        _messages.Messages.Add(Record("item-taught mist", MessageFlags.Confused,
            "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 9202));
        _messages.Messages.Add(Record("textblock-taught mist", MessageFlags.Confused,
            "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 9204));
        Monsters("dark wizard");

        Feed("You are freezing");

        Assert.Contains("ice storm", AppliedNames);
        Assert.DoesNotContain("item-taught mist", AppliedNames);
        Assert.DoesNotContain("textblock-taught mist", AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    private static readonly string[] Nobody = Array.Empty<string>();

    // ----- Standing: what the client's own state lets it go by -------

    private static bool Known(string name) => name is "ice sorceress" or "white queen";

    private static CastReach.Here? StandingIn(
        RoomConfidence confidence = RoomConfidence.Confirmed, bool noRoom = false,
        bool dark = false, bool enteredBlind = false, bool blinded = false,
        bool noRoster = false, bool rosterIsWhole = true) =>
        CastReach.Standing(
            confidence, noRoom ? null : RoomWith(511), dark, enteredBlind, blinded,
            noRoster ? null : Roster("ice sorceress"), rosterIsWhole, Known, pvpRealm: true, isPartyMember: _ => false);

    [Fact]
    public void Standing_ConfirmedAndSeen_GivesTheRoomsSpellAndTheMonsters()
    {
        CastReach.Here here = StandingIn()!.Value;

        Assert.Equal(511, here.RoomSpell);
        Assert.Equal(new[] { "ice sorceress" }, here.Monsters);
    }

    [Theory]
    [InlineData(RoomConfidence.Unknown)]
    [InlineData(RoomConfidence.Pending)]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Lost)]
    [InlineData(RoomConfidence.PendingRespawn)]
    public void Standing_IsNowhere_UnlessThePositionIsConfirmed(RoomConfidence confidence)
        => Assert.Null(StandingIn(confidence));

    [Fact]
    public void Standing_IsNowhere_WithNoRoom()
        => Assert.Null(StandingIn(noRoom: true));

    [Fact]
    public void Standing_KeepsTheRoomsSpell_AndGivesNoMonsters_WhenTheRoomCantBeSeen()
    {
        foreach (CastReach.Here? unseen in new[]
                 {
                     StandingIn(dark: true), StandingIn(enteredBlind: true), StandingIn(blinded: true),
                     StandingIn(noRoster: true), StandingIn(rosterIsWhole: false),
                 })
        {
            Assert.Equal(511, unseen!.Value.RoomSpell);
            Assert.Null(unseen.Value.Monsters);
            Assert.Null(unseen.Value.Strangers);
        }
    }

    [Fact]
    public void StrangersSeen_AreTheListedPlayersOutsideTheParty()
    {
        IReadOnlyCollection<string>? seen = CastReach.StrangersSeen(
            new RoomEntitiesObservation("Also here: …",
                new[] { Monster("ice sorceress"), Player("Bob"), Player("Jroc") }, DateTimeOffset.UtcNow),
            cannotSee: false, rosterIsWhole: true, isPartyMember: name => name == "Jroc");

        Assert.Equal(new[] { "Bob" }, seen);
    }

    // ----- what of the roster can be vouched for ----------------------

    [Fact]
    public void MonstersSeen_AreTheRostersMonstersByTheirOwnNames()
    {
        IReadOnlyCollection<string>? seen = CastReach.MonstersSeen(
            new RoomEntitiesObservation("Also here: …",
                new[] { Monster("ice sorceress"), new RoomEntity("Someone", "Someone", EntityKind.Player, null) },
                DateTimeOffset.UtcNow),
            cannotSee: false, rosterIsWhole: true, Known);

        Assert.Equal(new[] { "ice sorceress" }, seen);
    }

    [Fact]
    public void MonstersSeen_AnEmptyRoom_IsKnownToBeEmpty()
        => Assert.Empty(CastReach.MonstersSeen(Roster(), cannotSee: false, rosterIsWhole: true, Known)!);

    [Fact]
    public void MonstersSeen_IsUnknown_WhenTheRoomCantBeSeen()
        => Assert.Null(CastReach.MonstersSeen(Roster("ice sorceress"), cannotSee: true, rosterIsWhole: true, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_WhenSomeoneMayHaveComeInUnlisted()
        => Assert.Null(CastReach.MonstersSeen(Roster("ice sorceress"), cannotSee: false, rosterIsWhole: false, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_BeforeAnyRosterWasRead()
        => Assert.Null(CastReach.MonstersSeen(null, cannotSee: false, rosterIsWhole: true, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_WithANameNobodyCanPlace()
    {
        Assert.Null(CastReach.MonstersSeen(
            new RoomEntitiesObservation("Also here: …",
                new[] { Monster("ice sorceress"), new RoomEntity("a shape", "a shape", EntityKind.Unknown, null) },
                DateTimeOffset.UtcNow),
            cannotSee: false, rosterIsWhole: true, Known));
        Assert.Null(CastReach.MonstersSeen(Roster("snow leopard"), cannotSee: false, rosterIsWhole: true, Known));
    }

    // ----- what the log and the bug report say of it -------------------

    // A record left out is a condition the client does not hold, so it is said at
    // Info, with Debug off: one line for the line read, naming both sides.
    [Fact]
    public void ANarrowedLine_IsLoggedOnceAtInfo_AndKeptForTheBugReport()
    {
        _log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = false };
        AddTheFreezingFamily();
        Monsters("ice sorceress");

        Feed("You are freezing");

        LogEntry said = Assert.Single(_log.Snapshot(), e => e.Message.Contains("shared by several spells", StringComparison.Ordinal));
        Assert.Equal(LogSeverity.Info, said.Severity);
        Assert.Contains("applied line 'You are freezing'", said.Message, StringComparison.Ordinal);
        Assert.Contains("cast here: 'ice storm' (ice sorceress here)", said.Message, StringComparison.Ordinal);
        Assert.Contains("'glacial mist' (Confused)", said.Message, StringComparison.Ordinal);
        Assert.Contains("'galacial mist' (Confused)", said.Message, StringComparison.Ordinal);

        ConditionTracker.Narrowing narrowed = Assert.IsType<ConditionTracker.Narrowing>(_tracker.LastNarrowed);
        Assert.Equal("You are freezing", narrowed.Line);
        Assert.Equal("'ice storm' (ice sorceress here)", narrowed.Kept);
        Assert.Contains("'galacial mist' (Confused)", narrowed.LeftOut, StringComparison.Ordinal);
    }

    // A room spell repeats the same line every tick: the first narrowing is said at
    // Info, the same one again within a minute only at Debug, and once the minute is
    // out it is Info again.
    [Fact]
    public void TheSameNarrowingRepeated_IsInfoOnce_ThenDebugWithinTheWindow()
    {
        _log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        AddTheFreezingFamily();
        Monsters("ice sorceress");
        DateTimeOffset start = DateTimeOffset.UtcNow;

        FeedAt("You are freezing", start);
        FeedAt("You are freezing", start.AddSeconds(10));
        FeedAt("You are freezing", start.AddSeconds(80));

        List<LogSeverity> said = _log.Snapshot()
            .Where(e => e.Message.Contains("shared by several spells", StringComparison.Ordinal))
            .Select(e => e.Severity).ToList();
        Assert.Equal(new[] { LogSeverity.Info, LogSeverity.Debug, LogSeverity.Info }, said);
        Assert.Equal(start.AddSeconds(80), _tracker.LastNarrowed!.At);
    }

    [Fact]
    public void ALineNothingIsLeftOutOf_LogsNoNarrowing()
    {
        AddTheFreezingFamily();
        Monsters();

        Feed("You are freezing");

        Assert.DoesNotContain(_log.Snapshot(), e => e.Message.Contains("shared by several spells", StringComparison.Ordinal));
        Assert.Null(_tracker.LastNarrowed);
    }

    [Fact]
    public void WhyRosterNotWhole_SaysWhatKeepsTheRosterFromCounting()
    {
        RoomEntitiesObservation seen = Roster("ice sorceress");
        Assert.Null(CastReach.WhyRosterNotWhole(seen, false, false, false, true, null, Known));
        Assert.Equal("the room is dark", CastReach.WhyRosterNotWhole(seen, true, false, false, true, null, Known));
        Assert.Equal("the room was entered blind", CastReach.WhyRosterNotWhole(seen, false, true, false, true, null, Known));
        Assert.Equal("the character is blind", CastReach.WhyRosterNotWhole(seen, false, false, true, true, null, Known));
        Assert.Equal("no room list has been read", CastReach.WhyRosterNotWhole(null, false, false, false, true, null, Known));
        Assert.Equal("a summon: 'x'", CastReach.WhyRosterNotWhole(seen, false, false, false, false, "a summon: 'x'", Known));
        Assert.Equal("it lists 'snow leopard', a monster the game data has none of",
            CastReach.WhyRosterNotWhole(Roster("snow leopard"), false, false, false, true, null, Known));
        Assert.Equal("it lists 'a shape', who can't be placed",
            CastReach.WhyRosterNotWhole(
                new RoomEntitiesObservation("Also here: …",
                    new[] { new RoomEntity("a shape", "a shape", EntityKind.Unknown, null) }, DateTimeOffset.UtcNow),
                false, false, false, true, null, Known));
    }

    // After a link drop the classifier has no roster and says why; that reason, not
    // the bare "no room list", is what the report shows.
    [Fact]
    public void WhyRosterNotWhole_WithNoRoster_GivesTheClassifiersReason()
    {
        Assert.Equal("the game was left; no room has been read since",
            CastReach.WhyRosterNotWhole(
                null, false, false, false, false, "the game was left; no room has been read since", Known));
        Assert.Equal("no room list has been read",
            CastReach.WhyRosterNotWhole(null, false, false, false, false, null, Known));
    }
}
