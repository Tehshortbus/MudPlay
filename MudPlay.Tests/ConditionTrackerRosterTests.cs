using System.IO;
using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Game.Conditions;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A monster's spell is ruled out by its caster's absence only while the roster can
// be taken for everyone in the room, and a hold, an attack-stop or a blindness never
// is. Run through the shipped patterns and message records, the room classifier and
// the arrival watcher, over rows copied from the two imported sets (cut down to the
// columns read).
//
// Each case feeds two trackers: one told who is here, and one that is not, which
// latches as the client did before a shared line was read by who could cast it.
public sealed class ConditionTrackerRosterTests
{
    // Spells of the shared lines used below, and every monster their lists name.
    private const string ParadigmSpells = """
        [
          { "Number": 202,  "Name": "banshee's wail", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #113, Monster #765, Monster #809" },
          { "Number": 821,  "Name": "ghastly wail", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #703" },
          { "Number": 932,  "Name": "planewalker wail", "Learnable": 0, "Learned From": "\u0000", "Casted By": "\u0000" },
          { "Number": 1180, "Name": "fear3", "Learnable": 0, "Learned From": "\u0000", "Casted By": "\u0000" },
          { "Number": 327,  "Name": "paralyzed", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #231, Monster #946, Monster #1010, Monster #2220" },
          { "Number": 5110, "Name": "paralyzed", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #1207" },
          { "Number": 249,  "Name": "yellow fungus", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Textblock #196" },
          { "Number": 1024, "Name": "plague", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #850, Monster #2114, Monster #2396, Monster #2781, Monster #215" },
          { "Number": 1113, "Name": "monster dark plague", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #1010" },
          { "Number": 5001, "Name": "dark plague", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #1011, Monster #1075, Monster #1084, Monster #2195" },
          { "Number": 191,  "Name": "shriek", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #201, Monster #2575" },
          { "Number": 5138, "Name": "shriek", "Learnable": 0, "Learned From": "\u0000", "Casted By": "Monster #1234" },
          { "Number": 5400, "Name": "shriek", "Learnable": 0, "Learned From": "\u0000", "Casted By": "\u0000" }
        ]
        """;

    private const string ParadigmMonsters = """
        [
          { "Number": 113,  "Name": "banshee", "MidSpell-0": 202 },
          { "Number": 765,  "Name": "banshee", "MidSpell-0": 202 },
          { "Number": 809,  "Name": "Death Shrieker", "MidSpell-0": 202 },
          { "Number": 703,  "Name": "ancestral guardian", "AttType-1": 2, "AttAcc-1": 821, "Att%-1": 100 },
          { "Number": 231,  "Name": "carrion crawler", "AttType-0": 1, "AttHitSpell-0": 327, "Att%-0": 50 },
          { "Number": 2220, "Name": "carrion crawler", "AttType-0": 1, "AttHitSpell-0": 327, "Att%-0": 50 },
          { "Number": 946,  "Name": "wraith spider", "AttType-2": 1, "AttHitSpell-2": 327, "Att%-2": 100 },
          { "Number": 1010, "Name": "Nahr", "AttType-3": 1, "AttHitSpell-3": 327, "Att%-3": 100, "MidSpell-2": 1113 },
          { "Number": 1011, "Name": "Nahr", "MidSpell-2": 5001 },
          { "Number": 1084, "Name": "Nahr", "MidSpell-2": 5001 },
          { "Number": 1207, "Name": "mammoth stone scorpion", "AttType-2": 1, "AttHitSpell-2": 5110, "Att%-2": 100 },
          { "Number": 850,  "Name": "Dark Mage", "MidSpell-4": 1024 },
          { "Number": 2114, "Name": "Dread Wraith", "MidSpell-3": 1024 },
          { "Number": 2396, "Name": "Dark Revenant", "MidSpell-0": 1024 },
          { "Number": 2781, "Name": "Deathgaze", "MidSpell-3": 1024 },
          { "Number": 215,  "Name": "Zanthus the Lich", "MidSpell-3": 1024 },
          { "Number": 1075, "Name": "black queen", "MidSpell-3": 5001 },
          { "Number": 2195, "Name": "luursa", "AttType-3": 2, "AttAcc-3": 5001, "Att%-3": 0 },
          { "Number": 201,  "Name": "roc", "AttType-2": 2, "AttAcc-2": 191, "Att%-2": 100 },
          { "Number": 2575, "Name": "gryphon", "AttType-3": 2, "AttAcc-3": 191, "Att%-3": 80 },
          { "Number": 1234, "Name": "Mother Ungol", "MidSpell-1": 5138 }
        ]
        """;

    private const string StockSpells = """
        [
          { "Number": 430,  "Name": "terror", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #327, Monster #369, Monster #537, Monster #839, Monster #809" },
          { "Number": 822,  "Name": "terror", "Learnable": 0, "Learned From": "\u0000", "Casted By": "Monster #707" },
          { "Number": 1093, "Name": "web", "Learnable": 0, "Learned From": "\u0000", "Casted By": "Monster #867" },
          { "Number": 1141, "Name": "web", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #622, Monster #929, Monster #898" }
        ]
        """;

    private const string StockMonsters = """
        [
          { "Number": 327, "Name": "terror beast", "AttType-2": 2, "AttAcc-2": 430, "Att%-2": 100 },
          { "Number": 369, "Name": "fallen angel", "MidSpell-0": 430 },
          { "Number": 537, "Name": "diamond guardian", "AttType-1": 2, "AttAcc-1": 430, "Att%-1": 100 },
          { "Number": 809, "Name": "Death Shrieker", "MidSpell-1": 430 },
          { "Number": 839, "Name": "Scarecrow", "MidSpell-1": 430 },
          { "Number": 910, "Name": "Mayor Delanon", "AttType-2": 2, "AttAcc-2": 430, "Att%-2": 90 },
          { "Number": 707, "Name": "monkey spirit", "AttType-2": 2, "AttAcc-2": 822, "Att%-2": 100 },
          { "Number": 867, "Name": "horned spider", "AttType-3": 1, "AttHitSpell-3": 1093, "Att%-3": 100 },
          { "Number": 622, "Name": "massive fanged spider", "MidSpell-0": 1141 },
          { "Number": 898, "Name": "arachnigoth", "MidSpell-0": 1141, "AttType-1": 1, "AttHitSpell-1": 1141, "Att%-1": 100 },
          { "Number": 929, "Name": "crowned spider", "AttType-3": 1, "AttHitSpell-3": 1141, "Att%-3": 100 }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly MessageStore _messages = new();
        private readonly MessageRouter _router = new();
        // The game's output through the router and the extractor both trackers read.
        private readonly ServerFeed _feed;
        private readonly RoomEntryWatcher _watcher;

        public RoomEntityClassifier Classifier { get; }
        // Told who is here; and not told, which is how the client latched before.
        public ConditionTracker Told { get; }
        public ConditionTracker NotTold { get; }
        public List<MessageRecord> ToldApplied { get; } = new();
        public List<MessageRecord> NotToldApplied { get; } = new();

        public Harness(string realm)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-roster-" + Path.GetRandomFileName());
            string set = Path.Combine(_root, "alpha");
            Directory.CreateDirectory(set);
            File.WriteAllText(Path.Combine(set, "Spells.json"), realm == "paradigm" ? ParadigmSpells : StockSpells);
            File.WriteAllText(Path.Combine(set, "Monsters.json"), realm == "paradigm" ? ParadigmMonsters : StockMonsters);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            SpellCasterIndex casters = new(cache, () => Array.Empty<Room>());
            casters.Warm();

            string seeds = Path.Combine(_root, "seeds");
            AppPaths.ExtractEmbeddedSeeds(seeds);
            _messages.Messages.ReplaceAll(JsonStore.Load<List<MessageRecord>>(
                Path.Combine(seeds, $"Messages.{realm}.seed.json"))!);

            DefaultPatterns.Seed(_router);
            Classifier = new RoomEntityClassifier(
                _router, new MonsterMessageStore(), new PlayerDatabase(new ProfileService(), () => null), roomTracker: null, gameData: cache);
            _watcher = new RoomEntryWatcher(_router, Classifier);

            Room room = new()
            {
                Key = new RoomKey(1, 1), Name = "Somewhere", Exits = new Dictionary<Direction, RoomExit>(),
            };
            Told = new ConditionTracker(_messages);
            Told.ConditionApplied += ToldApplied.Add;
            Told.SetCastReach(new CastReach(casters, () => CastReach.Standing(
                RoomConfidence.Confirmed, room, inDarkRoom: false, enteredBlind: false, blinded: false,
                Classifier.Current, Classifier.RosterIsWhole, casters.KnowsMonster,
                pvpRealm: false, isPartyMember: _ => false)));
            Told.CasterNamed += Classifier.NoteCasterNamed;
            NotTold = new ConditionTracker(_messages);
            NotTold.ConditionApplied += NotToldApplied.Add;
            _feed = new ServerFeed(_router);
            Told.AttachLineExtractor(_feed.Lines);
            NotTold.AttachLineExtractor(_feed.Lines);
        }

        public void Feed(params string[] lines)
        {
            _feed.Rows(lines);
        }

        public string[] Names(List<MessageRecord> applied) =>
            applied.Select(r => $"{r.Name} [{r.Flags}]").Order(StringComparer.Ordinal).ToArray();

        // The line fed is read by both trackers alike.
        public void AssertToldLatchesWhatNotToldDoes()
        {
            Assert.Equal(Names(NotToldApplied), Names(ToldApplied));
            Assert.Equal(NotTold.ActiveFlags, Told.ActiveFlags);
        }

        public void Dispose()
        {
            Told.Dispose();
            NotTold.Dispose();
            _watcher.Dispose();
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    // ----- a caster that came in after the roster was read -----------

    // The banshee's follow line is her own (Stock message #244). It names nobody the
    // roster can add, so the ancestral guardian stays the only one listed; her wail
    // is a hold as well as a confusion, the guardian's a confusion alone.
    [Fact]
    public void ABansheeThatFollowedIn_BehindAGuardian_StillHolds()
    {
        using Harness h = new("paradigm");

        h.Feed("Also here: ancestral guardian.",
            "The banshee floats after you!",
            "The banshee shrieks mournfully!",
            "You are deathly afraid!");

        Assert.False(h.Classifier.RosterIsWhole);
        Assert.Equal(MessageFlags.Confused | MessageFlags.MovementPrevented, h.Told.ActiveFlags);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // The crawler's follow line is made up (its own is not in the data at hand); any
    // line of that shape is read the same way.
    [Fact]
    public void ACarrionCrawlerThatFollowedIn_BehindAScorpion_StillParalyzes()
    {
        using Harness h = new("paradigm");

        h.Feed("Also here: mammoth stone scorpion.",
            "The carrion crawler scuttles after you!",
            "Your muscles begin to seize up!",
            "You are paralyzed!");

        Assert.True(h.Told.IsMovementPrevented);
        Assert.True(h.Told.IsAttackPrevented);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // The shrieker's follow line is made up. Its terror's own cast line comes first.
    [Fact]
    public void ADeathShriekerThatFollowedIn_BehindAMonkeySpirit_StillMaddens()
    {
        using Harness h = new("stock");

        h.Feed("Also here: monkey spirit.",
            "The Death Shrieker glides after you!",
            "The Death Shrieker's unearthly shriek strikes terror into your heart!",
            "You have gone mad!");

        Assert.Equal(MessageFlags.AttackPrevented | MessageFlags.Confused, h.Told.ActiveFlags);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // The horned spider's follow line is made up.
    [Fact]
    public void AHornedSpiderThatFollowedIn_BehindACrownedSpider_StillBlinds()
    {
        using Harness h = new("stock");

        h.Feed("Also here: crowned spider.",
            "The horned spider scuttles after you!",
            "The spider quickly wraps you in a sticky cocoon!",
            "You are caught in a cocoon!");

        Assert.Equal(
            MessageFlags.Blinded | MessageFlags.Confused | MessageFlags.MovementPrevented, h.Told.ActiveFlags);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // ----- a cast line that names its caster -------------------------

    // Three Paradigm shriek records share `The {source} rears its head, and shrieks
    // in fury!`: #191 (the rocs' and gryphons', a confusion), #5138 (Mother Ungol's)
    // and #5400 (nobody's), neither of those two a confusion. #191's list is cut to
    // the roc and the gryphon, its other six monsters left out of the rows above.
    private const string Shriek = "rears its head, and shrieks in fury!";

    [Fact]
    public void AShriekByAListedCaster_IsReadByWhoIsListed()
    {
        using Harness h = new("paradigm");

        h.Feed("Also here: Mother Ungol.", $"The Mother Ungol {Shriek}", "You are confused!");

        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Equal(new[] { "shriek [None]" }, h.Names(h.ToldApplied));
        Assert.False(h.Told.IsConfused);
        Assert.True(h.NotTold.IsConfused);
    }

    // The gryphon shrieked and is not listed: the roster is short of it, and nothing
    // is left out by it until the room is read again.
    [Theory]
    [InlineData("gryphon")]        // a monster of the set
    [InlineData("snow leopard")]   // a name the set has no monster for
    public void AShriekByACasterNotListed_LeavesNothingOut(string caster)
    {
        using Harness h = new("paradigm");

        h.Feed("Also here: Mother Ungol.", $"The {caster} {Shriek}", "You are confused!");

        Assert.False(h.Classifier.RosterIsWhole);
        Assert.True(h.Told.IsConfused);
        h.AssertToldLatchesWhatNotToldDoes();

        h.Feed("Also here: Mother Ungol, gryphon.");
        Assert.True(h.Classifier.RosterIsWhole);
    }

    // ----- the cast line, with the roster whole ----------------------

    // No follow line at all: the roster lists the monkey spirit alone. The terror
    // that maddens is the Death Shrieker's, its own cast line was just seen, and that
    // is the one applied. The line names the shrieker, so the roster is short of it.
    [Fact]
    public void ASpellsOwnCastLine_Decides_WhoeverIsListed()
    {
        using Harness h = new("stock");

        h.Feed("Also here: monkey spirit.",
            "The Death Shrieker's unearthly shriek strikes terror into your heart!",
            "You have gone mad!");

        Assert.False(h.Classifier.RosterIsWhole);
        Assert.Equal(new[] { "terror [Confused, AttackPrevented]" }, h.Names(h.ToldApplied));
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // ----- what the roster never rules out ---------------------------

    // The roster is whole and lists only the caster of the milder spell, and no cast
    // line is seen. What holds, stops attacks or blinds is latched all the same.
    [Theory]
    [InlineData("paradigm", "ancestral guardian", "You are deathly afraid!", MessageFlags.MovementPrevented)]
    [InlineData("stock", "monkey spirit", "You have gone mad!", MessageFlags.AttackPrevented)]
    [InlineData("stock", "crowned spider", "You are caught in a cocoon!", MessageFlags.Blinded)]
    public void WithOnlyTheMilderSpellsCasterListed_AHoldAnAttackStopABlindness_StillLatches(
        string realm, string listed, string appliedLine, MessageFlags mustLatch)
    {
        using Harness h = new(realm);

        h.Feed($"Also here: {listed}.", appliedLine);

        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Equal(mustLatch, h.Told.ActiveFlags & mustLatch);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // Paradigm's two paralyzed spells, #327 (the crawlers', the wraith spider's and
    // Nahr's) and #5110 (the scorpion's), carry the same Confusion at +100, which is
    // how the engine paralyzes. Both records are holds and attack-stops, so whichever
    // caster is here, the paralysis holds and nothing reads it as a confusion.
    [Theory]
    [InlineData("carrion crawler")]
    [InlineData("mammoth stone scorpion")]
    public void AParalysis_HoldsAndStopsAttacks_WhicheverCasterIsListed(string listed)
    {
        using Harness h = new("paradigm");

        h.Feed($"Also here: {listed}.", "Your muscles begin to seize up!", "You are paralyzed!");

        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Equal(MessageFlags.MovementPrevented | MessageFlags.AttackPrevented, h.Told.ActiveFlags);
        h.AssertToldLatchesWhatNotToldDoes();
    }

    // Every record that applies `You are paralyzed` is a hold and an attack-stop: the
    // paralyzed spells and yellow fungus (#249), all of them Confusion (71) in the data
    // (the user's ruling of 2026-10-17). A set's own message file stores the user's
    // overrides by Id, so the Ids stay.
    [Theory]
    [InlineData("paradigm", "paralyzed", 327, "65c6d25ebdba89ec")]
    [InlineData("paradigm", "paralyzed", 5110, "f3246e40343077c8")]
    [InlineData("paradigm", "yellow fungus", 249, "ffef3d936076c9b0")]
    [InlineData("stock", "paralyzed", 327, "65c6d25ebdba89ec")]
    [InlineData("stock", "yellow fungus", 249, "96a7f595b8a7e815")]
    public void TheSeed_FlagsEveryParalysisAsAHoldAndAnAttackStop_AndKeepsItsId(
        string realm, string name, int spell, string id)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-para-seed-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;

            MessageRecord r = seed.Single(x =>
                x.Name == name && x.Links is { } links && links.Contains(new GameDataLink("Spells", spell)));
            Assert.Equal(MessageFlags.MovementPrevented | MessageFlags.AttackPrevented, r.Flags);
            Assert.Equal(0x30, r.RawFlagsHex);
            Assert.Equal(id, r.Id);
            Assert.DoesNotContain(seed, x =>
                x.AppliedMessage == "You are paralyzed"
                && x.Flags != (MessageFlags.MovementPrevented | MessageFlags.AttackPrevented));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    // ----- what it does rule out, and when it stops ------------------

    // Three plagues print the same lines: plague (#1024) is a disease, dark plague
    // (#5001, the black queen's) and monster dark plague (#1113) are not. The Dark
    // Mage's follow line is made up.
    [Fact]
    public void APlague_IsReadByWhoIsListed_WhileTheRosterIsWhole_AndNotAfterANamelessArrival()
    {
        using Harness h = new("paradigm");
        string[] plague = { "A hideous plague consumes you!", "A plague is consuming your flesh!" };
        const string over = "The plague has left your ravaged body.";

        // Only the black queen is here: her dark plague, and no disease.
        h.Feed("Also here: black queen.");
        h.Feed(plague);
        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Equal(new[] { "dark plague [None]" }, h.Names(h.ToldApplied));
        Assert.False(h.Told.IsDiseased);
        Assert.True(h.NotTold.IsDiseased);
        h.Feed(over);

        // Someone followed in whom the roster doesn't name: nothing is ruled out.
        h.ToldApplied.Clear();
        h.Feed("The Dark Mage strides after you!");
        Assert.False(h.Classifier.RosterIsWhole);
        h.Feed(plague);
        Assert.Equal(3, h.ToldApplied.Count);
        Assert.True(h.Told.IsDiseased);
        h.Feed(over);

        // The room is read again and lists both: the disease is cast here.
        h.ToldApplied.Clear();
        h.Feed("Also here: black queen, Dark Mage.");
        Assert.True(h.Classifier.RosterIsWhole);
        h.Feed(plague);
        Assert.Equal(new[] { "dark plague [None]", "plague [Diseased]" }, h.Names(h.ToldApplied));
        h.Feed(over);

        // And read again with the mage gone: ruled out once more.
        h.ToldApplied.Clear();
        h.Feed("Also here: black queen.");
        h.Feed(plague);
        Assert.Equal(new[] { "dark plague [None]" }, h.Names(h.ToldApplied));
        Assert.False(h.Told.IsDiseased);
    }

    // An arrival under a name the set has a monster for goes on the roster, which
    // stays whole.
    [Fact]
    public void AnArrivalTheRosterCanName_LeavesItWhole()
    {
        using Harness h = new("paradigm");

        h.Feed("Also here: black queen.",
            "A Dark Mage walks into the room from the north.",
            "A hideous plague consumes you!", "A plague is consuming your flesh!");

        Assert.True(h.Classifier.RosterIsWhole);
        Assert.Equal(new[] { "dark plague [None]", "plague [Diseased]" }, h.Names(h.ToldApplied));
    }

    // ----- the roster's own word on whether it is whole ---------------

    [Fact]
    public void TheRoster_IsWholeFromAReadOfTheRoom_UntilSomeoneMayHaveComeInUnnamed()
    {
        using Harness h = new("paradigm");
        Assert.False(h.Classifier.RosterIsWhole);            // nothing read yet

        h.Feed("Also here: black queen.");
        Assert.True(h.Classifier.RosterIsWhole);

        h.Feed("A shade materializes in the room.");          // a spawn that names nobody the pattern lifts
        Assert.False(h.Classifier.RosterIsWhole);

        h.Feed("Also here: black queen, shade.");
        Assert.True(h.Classifier.RosterIsWhole);

        h.Feed("A snow leopard pads into the room from the north.");   // no monster of that name in the set
        Assert.False(h.Classifier.RosterIsWhole);

        h.Feed("Also here: black queen.");
        Assert.True(h.Classifier.RosterIsWhole);

        h.Classifier.NoteRoomChanged();                        // dropped as stale: not a read
        Assert.False(h.Classifier.RosterIsWhole);

        h.Feed("Also here: black queen.");
        h.Classifier.NoteGameLeft();
        Assert.False(h.Classifier.RosterIsWhole);
    }
}
