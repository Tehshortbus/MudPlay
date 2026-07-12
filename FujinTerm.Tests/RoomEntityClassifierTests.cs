using System.IO;
using System.Text;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0b sub-B — <see cref="RoomEntityClassifier"/> Also-Here parsing,
/// prefix-strip resolution against MonsterMessageStore, player lookup
/// against PlayerDatabase, and the Unknown-entity Warn path with
/// raw-line Context payload.
/// </summary>
public sealed class RoomEntityClassifierTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public List<LogEntry> LogEntries { get; } = new();
        public List<RoomEntitiesObservation> Observations { get; } = new();

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Log.EntryAdded += LogEntries.Add;
            Classifier.EntitiesObserved += Observations.Add;
        }

        public void AddMonster(int number, string name, bool allowNoPrefix, params string[] flavorPrefixes)
        {
            // Minimal MonsterMessageRecord for classifier purposes — the
            // line patterns (HitYou, MissYou, etc.) aren't read by the
            // classifier; empty lists keep the fixture small.
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: Array.Empty<string>(),
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: flavorPrefixes,
                AllowNoPrefix: allowNoPrefix,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void AddPlayer(string givenName, string familyName = "")
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: givenName,
                FamilyName: familyName,
                Class: "Warrior",
                Race: "Human",
                Alignment: "Neutral",
                Title: null,
                Gang: null,
                Role: null,
                FirstSeenUtc: DateTime.UtcNow,
                LastSeenUtc: DateTime.UtcNow));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose() => Classifier.Dispose();
    }

    // ----- basic shape -----------------------------------------------

    [Fact]
    public void Empty_BeforeAnyLine_CurrentIsNull()
    {
        using Harness h = new();
        Assert.Null(h.Classifier.Current);
    }

    [Fact]
    public void NonMatchingLine_NoEmit()
    {
        using Harness h = new();
        h.Feed("Some random line not matching anything.");
        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    // ----- direct monster match (AllowNoPrefix) ----------------------

    [Fact]
    public void DirectMonsterMatch_AllowNoPrefix()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);

        h.Feed("Also here: giant rat.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("giant rat", ent.RawName);
        Assert.Equal("giant rat", ent.ResolvedName);
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal(1, ent.MonsterNumber);
    }

    [Fact]
    public void DirectMonsterMatch_RequiresAllowNoPrefix()
    {
        // Same name but AllowNoPrefix=false — direct shouldn't match.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: false, "nasty");

        h.Feed("Also here: giant rat.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
    }

    [Fact]
    public void DirectMonsterMatch_EmptyFlavorPrefixes_ImpliesNoPrefix()
    {
        // Live repro: 535 of 1100 v1.11p seed records (cave bear,
        // shade, Colin, Lady Sentara, …) carry AllowNoPrefix=false
        // AND FlavorPrefixes=[]. Without the empty-prefix fallback
        // they're unreachable: direct match rejects (AllowNoPrefix
        // false) and prefix-stripped match skips empty lists. So the
        // entire record sits in the catalogue and the classifier still
        // emits "unknown entity 'cave bear'". The fallback treats
        // "no prefixes defined" as "bare name is the only form" and
        // matches directly.
        using Harness h = new();
        h.AddMonster(80, "cave bear", allowNoPrefix: false);   // no flavor prefixes

        h.Feed("Also here: cave bear.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal("cave bear", ent.ResolvedName);
        Assert.Equal(80, ent.MonsterNumber);
    }

    // ----- prefix-stripped monster match -----------------------------

    [Fact]
    public void PrefixStrippedMonsterMatch()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true,
            "nasty", "angry", "fat");

        h.Feed("Also here: nasty giant rat.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("nasty giant rat", ent.RawName);
        Assert.Equal("giant rat", ent.ResolvedName);   // canonical base
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal(1, ent.MonsterNumber);
    }

    [Fact]
    public void PrefixMatch_CaseInsensitive()
    {
        using Harness h = new();
        h.AddMonster(1, "Giant Rat", allowNoPrefix: false, "Nasty");

        h.Feed("Also here: NASTY giant rat.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
    }

    [Fact]
    public void UnknownPrefix_FallsToUnknown()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true, "nasty");

        h.Feed("Also here: stinking giant rat.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
    }

    // ----- player lookup ---------------------------------------------

    [Fact]
    public void PlayerMatch_ByGivenName()
    {
        using Harness h = new();
        h.AddPlayer("Bob");

        h.Feed("Also here: Bob.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("Bob", ent.RawName);
        Assert.Equal("Bob", ent.ResolvedName);
        Assert.Equal(EntityKind.Player, ent.Kind);
        Assert.Null(ent.MonsterNumber);
    }

    [Fact]
    public void PlayerMatch_StripsParentheticalSuffix()
    {
        // "Raijin (sneaking)" → first-token "Raijin" against Players.
        using Harness h = new();
        h.AddPlayer("Raijin");

        h.Feed("Also here: Raijin (sneaking).");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("Raijin", ent.RawName);
        Assert.Equal(EntityKind.Player, ent.Kind);
    }

    [Fact]
    public void PlayerMatch_FamilyNameInDisplay_ResolvesByGivenOnly()
    {
        // "Forged Paradigm" — Also-Here shows family name; we still look
        // up by the first whitespace token.
        using Harness h = new();
        h.AddPlayer("Forged", familyName: "Paradigm");

        h.Feed("Also here: Forged Paradigm.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Player, h.Observations[0].Entities[0].Kind);
    }

    [Fact]
    public void PlayerMatch_CaseInsensitiveOnGivenName()
    {
        using Harness h = new();
        h.AddPlayer("Bob");

        h.Feed("Also here: BOB.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Player, h.Observations[0].Entities[0].Kind);
    }

    // ----- Unknown fallback + Warn-with-Context emit -----------------

    [Fact]
    public void Unknown_EmitsWarnRowWithRawLineContext()
    {
        using Harness h = new();

        h.Feed("Also here: foozle.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);

        LogEntry warn = h.LogEntries.Find(e =>
            e.Source == RoomEntityClassifier.LogCategory &&
            e.Severity == LogSeverity.Warn);
        Assert.NotEqual(default, warn);
        Assert.Contains("foozle", warn.Message);
        Assert.Equal("Also here: foozle.", warn.Context);
    }

    [Fact]
    public void Unknown_ClassifierWithoutLog_DoesNotThrow()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MonsterMessageStore monsters = new();
        PlayerDatabase players = new();

        using RoomEntityClassifier classifier = new(router, monsters, players, log: null);

        LineExtractor.EmittedLine emitted = new(
            "Also here: foozle.", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false);
        router.Dispatch(emitted);

        // Without a log service the Warn just goes nowhere — but the
        // observation still fires + classifies as Unknown.
        Assert.NotNull(classifier.Current);
        Assert.Equal(EntityKind.Unknown, classifier.Current!.Value.Entities[0].Kind);
    }

    // ----- list-shape forms (commas, Oxford-and, mixed) --------------

    [Fact]
    public void TwoEntries_Comma()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddPlayer("Bob");

        h.Feed("Also here: giant rat, Bob.");

        Assert.Single(h.Observations);
        Assert.Equal(2, h.Observations[0].Entities.Count);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
        Assert.Equal(EntityKind.Player,  h.Observations[0].Entities[1].Kind);
    }

    [Fact]
    public void ThreeEntries_OxfordAnd()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddMonster(2, "goblin",    allowNoPrefix: true);
        h.AddPlayer("Bob");

        h.Feed("Also here: giant rat, goblin and Bob.");

        Assert.Single(h.Observations);
        Assert.Equal(3, h.Observations[0].Entities.Count);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[1].Kind);
        Assert.Equal(EntityKind.Player,  h.Observations[0].Entities[2].Kind);
    }

    [Fact]
    public void MixedKnownAndUnknown_ProducesPartialResults()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);

        h.Feed("Also here: giant rat, foozle.");

        Assert.Single(h.Observations);
        Assert.Equal(2, h.Observations[0].Entities.Count);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[1].Kind);

        // One warn row carrying the same raw line.
        LogEntry warn = h.LogEntries.Find(e =>
            e.Source == RoomEntityClassifier.LogCategory &&
            e.Severity == LogSeverity.Warn);
        Assert.Equal("Also here: giant rat, foozle.", warn.Context);
    }

    // ----- precedence: monster before player -------------------------

    [Fact]
    public void MonsterBeforePlayer_WhenBothNamesCollide()
    {
        // Edge case: a player and a monster happen to share a name.
        // The classifier checks Monsters first (the design assumes
        // shopkeepers / quest-givers can collide with real names, and
        // the Phase 9 plan resolves monster first).
        using Harness h = new();
        h.AddMonster(99, "Bob", allowNoPrefix: true);
        h.AddPlayer("Bob");

        h.Feed("Also here: Bob.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
    }

    // ----- raw-line preservation -------------------------------------

    [Fact]
    public void RawAlsoHereLine_PreservedInObservation()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);

        h.Feed("Also here: giant rat.");

        Assert.Equal("Also here: giant rat.", h.Observations[0].RawAlsoHereLine);
    }

    // ----- RemoveDeadEntity --------------------------------------------

    [Fact]
    public void RemoveDeadEntity_DropsMatchingMonster_FiresObservation()
    {
        // The user's "kobold thief arrived but no attack" bug: after a
        // mob dies, its entry must be removed from Current so
        // CombatManager doesn't sit thinking it's still alive when an
        // arrival event appends a new entity.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddMonster(2, "kobold thief", allowNoPrefix: true);

        h.Feed("Also here: giant rat, kobold thief.");
        Assert.Equal(2, h.Observations[0].Entities.Count);

        bool removed = h.Classifier.RemoveDeadEntity("giant rat");

        Assert.True(removed);
        Assert.Equal(2, h.Observations.Count);
        Assert.Single(h.Observations[1].Entities);
        Assert.Equal("kobold thief", h.Observations[1].Entities[0].ResolvedName);
    }

    [Fact]
    public void RemoveDeadEntity_RemovesOnlyOne_OfMultipleSameName()
    {
        // Three giant rats — one dies. We remove ONE entry; the next
        // room re-display corrects if our local count diverges.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);

        h.Feed("Also here: giant rat, giant rat, giant rat.");
        Assert.Equal(3, h.Observations[0].Entities.Count);

        bool removed = h.Classifier.RemoveDeadEntity("giant rat");

        Assert.True(removed);
        Assert.Equal(2, h.Observations[1].Entities.Count);
    }

    [Fact]
    public void RemoveDeadEntity_NoMatch_ReturnsFalse_NoEmit()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.Feed("Also here: giant rat.");
        int observationsBefore = h.Observations.Count;

        bool removed = h.Classifier.RemoveDeadEntity("goblin");

        Assert.False(removed);
        Assert.Equal(observationsBefore, h.Observations.Count);
    }

    [Fact]
    public void RemoveDeadEntity_NullCurrent_ReturnsFalse()
    {
        using Harness h = new();
        bool removed = h.Classifier.RemoveDeadEntity("giant rat");
        Assert.False(removed);
    }

    // ----- RemoveDepartedEntity ----------------------------------------

    [Fact]
    public void RemoveDepartedEntity_DropsMatchingMonster_FiresDepartureObservation()
    {
        // The 180449 bug: a fleeing player drags the engaged mob out of the room.
        // The departure line has no room re-display and no Also-Here, so the combat
        // gate the mob held stays asserted forever unless we drop it here and re-fire
        // the observation (tagged Departure) to re-evaluate the gate.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddMonster(2, "orc rogue", allowNoPrefix: true);

        h.Feed("Also here: giant rat, orc rogue.");
        Assert.Equal(2, h.Observations[0].Entities.Count);

        bool removed = h.Classifier.RemoveDepartedEntity("orc rogue");

        Assert.True(removed);
        Assert.Equal(2, h.Observations.Count);
        Assert.Single(h.Observations[1].Entities);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void RemoveDepartedEntity_LastMonster_LeavesEmptyList()
    {
        // The gate-clearing case: the departed mob is the last hostile, so the
        // re-fired observation carries an empty list — the combat gate re-evaluates
        // to zero actionable hostiles and drops.
        using Harness h = new();
        h.AddMonster(2, "orc rogue", allowNoPrefix: true);

        h.Feed("Also here: orc rogue.");
        Assert.Single(h.Observations[0].Entities);

        bool removed = h.Classifier.RemoveDepartedEntity("orc rogue");

        Assert.True(removed);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(RoomObservationSource.Departure, h.Observations[1].Source);
    }

    [Fact]
    public void RemoveDepartedEntity_NoMatch_ReturnsFalse_NoEmit()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.Feed("Also here: giant rat.");
        int observationsBefore = h.Observations.Count;

        bool removed = h.Classifier.RemoveDepartedEntity("goblin");

        Assert.False(removed);
        Assert.Equal(observationsBefore, h.Observations.Count);
    }

    [Fact]
    public void RemoveDepartedEntity_NullCurrent_ReturnsFalse()
    {
        using Harness h = new();
        bool removed = h.Classifier.RemoveDepartedEntity("orc rogue");
        Assert.False(removed);
    }

    [Fact]
    public void RemoveDepartedEntity_LeavesPlayer_Untouched()
    {
        // A departing player holds no combat gate, so a player's entry in Current is
        // harmless — RemoveDepartedEntity is monster-kind only and must not drop the
        // player, which would spuriously re-fire the observation.
        using Harness h = new();
        h.AddPlayer("Bob");
        h.Feed("Also here: Bob.");
        int observationsBefore = h.Observations.Count;

        bool removed = h.Classifier.RemoveDepartedEntity("Bob");

        Assert.False(removed);
        Assert.Equal(observationsBefore, h.Observations.Count);
    }

    [Fact]
    public void NoteRoomChanged_WipesCurrent_EmitsEmptyObservation()
    {
        // User's "wasted combat round on move" scenario: we have a
        // target in the previous room; when the walker moves, the
        // classifier wipes its observation so CombatManager clears
        // its target before the next round fires.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Observations[0].Entities);
        Assert.NotNull(h.Classifier.Current);

        h.Classifier.NoteRoomChanged();

        // Now two observations: the original Also-Here parse + the
        // wipe. Wipe carries an empty entity list.
        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(string.Empty, h.Observations[1].RawAlsoHereLine);

        // Current reflects the wipe.
        Assert.NotNull(h.Classifier.Current);
        Assert.Empty(h.Classifier.Current!.Value.Entities);
    }

    [Fact]
    public void NoteRoomChanged_ThenAlsoHere_RebuildsForNewRoom()
    {
        // After a wipe, the new room's Also-Here parse drives a fresh
        // emit with the new entities.
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddMonster(2, "goblin",    allowNoPrefix: true);

        h.Feed("Also here: giant rat.");
        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: goblin.");

        Assert.Equal(3, h.Observations.Count);
        Assert.Equal("giant rat", h.Observations[0].Entities[0].ResolvedName);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal("goblin",    h.Observations[2].Entities[0].ResolvedName);
    }

    [Fact]
    public void Current_TracksLatestObservation()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", allowNoPrefix: true);
        h.AddPlayer("Bob");

        h.Feed("Also here: giant rat.");
        h.Feed("Also here: Bob.");

        Assert.NotNull(h.Classifier.Current);
        Assert.Equal("Also here: Bob.", h.Classifier.Current!.Value.RawAlsoHereLine);
        Assert.Equal(EntityKind.Player, h.Classifier.Current.Value.Entities[0].Kind);
    }

    // ----- Multi-line wrap (80-col server boundary) ------------------

    /// <summary>
    /// MajorMUD wraps occupant lists at the 80-col boundary mid-token,
    /// so an Also Here line can span N rows until a period closes it.
    /// The classifier buffers continuation rows via the LineExtractor
    /// subscription and parses the joined text on close.
    /// </summary>
    [Fact]
    public void Wrap_AcrossTwoLines_ParsesAllOccupants()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",     allowNoPrefix: true, "angry");
        h.AddMonster(2, "carrion beast", allowNoPrefix: true, "nasty");
        h.AddMonster(3, "giant rat",     allowNoPrefix: true);
        h.AddMonster(4, "acid slime",    allowNoPrefix: true);
        h.AddMonster(5, "kobold thief",  allowNoPrefix: true, "short");

        // Attach the extractor so the wrap-buffer path is live.
        Terminal.LineExtractor lines = new(new FujinTerm.Terminal.TerminalEmulator(80, 24));
        h.Classifier.AttachLineExtractor(lines);

        // Feed wrapped rows in the order the server produces them.
        FeedLine(lines, "Also here: angry giant rat, nasty carrion beast, giant rat, acid slime, short");
        FeedLine(lines, "kobold thief.");

        Assert.Single(h.Observations);
        RoomEntitiesObservation obs = h.Observations[0];
        Assert.Equal(5, obs.Entities.Count);
        Assert.Equal("giant rat",     obs.Entities[0].ResolvedName);
        Assert.Equal("carrion beast", obs.Entities[1].ResolvedName);
        Assert.Equal("giant rat",     obs.Entities[2].ResolvedName);
        Assert.Equal("acid slime",    obs.Entities[3].ResolvedName);
        Assert.Equal("kobold thief",  obs.Entities[4].ResolvedName);
    }

    [Fact]
    public void Wrap_AcrossThreeLines_ParsesAllOccupants()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",    allowNoPrefix: true);
        h.AddMonster(2, "acid slime",   allowNoPrefix: true);
        h.AddMonster(3, "kobold thief", allowNoPrefix: true);

        Terminal.LineExtractor lines = new(new FujinTerm.Terminal.TerminalEmulator(80, 24));
        h.Classifier.AttachLineExtractor(lines);

        FeedLine(lines, "Also here: giant rat,");
        FeedLine(lines, "acid slime,");
        FeedLine(lines, "kobold thief.");

        Assert.Single(h.Observations);
        Assert.Equal(3, h.Observations[0].Entities.Count);
    }

    // ----- RoomTracker-coupled move-confirm freshness guard ----------
    //
    // Mid-combat re-attack regression. Wire order in a room display is
    // name → "Also here:" → "Obvious exits:", and RoomTracker only
    // CONFIRMS a move on the exits line. So the new room's occupants are
    // already parsed into Current by the time the move-confirming
    // StateChanged fires. The classifier must NOT wipe a post-move
    // observation — wiping nulls CombatManager's just-picked target and
    // burns a second attack. It must STILL wipe a genuinely stale
    // pre-move observation (monster from the prior room that didn't
    // follow us in).

    private sealed class TrackerHarness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public RoomTracker Tracker { get; }
        public RoomEntityClassifier Classifier { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();

        public TrackerHarness()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "fujinterm-classifier-tracker-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);

            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Tracker);
            Classifier.EntitiesObserved += Observations.Add;
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name,
                HitYou: Array.Empty<string>(), HitOther: Array.Empty<string>(),
                DeathLine: Array.Empty<string>(),
                ArmorBlockYou: Array.Empty<string>(), ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(), DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(), MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(), AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void FeedAlsoHere(string line)
            => Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose()
        {
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }

        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "North Square",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
    }

    [Fact]
    public void MoveConfirm_FreshNewRoomOccupants_NotWiped()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        // Located at Town Gates (exits N).
        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        // Move sent (in the past), then the NEW room's occupants parse
        // BEFORE the exits line confirms the move.
        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        // Exits line lands → tracker confirms Town Gates → North Square.
        h.Tracker.NoteRoomObserved(new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        // No wipe: the fresh post-move occupant survives.
        Assert.Single(h.Observations);
        Assert.NotNull(h.Classifier.Current);
        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("giant rat", h.Classifier.Current.Value.Entities[0].ResolvedName);
    }

    [Fact]
    public void MoveConfirm_StalePreMoveOccupants_Wiped()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        // A monster observed in the PREVIOUS room, before we moved.
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        // Move sent AFTER that observation → the observation is stale.
        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);
        h.Tracker.NoteRoomObserved(new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        // Wipe fires: stale occupant cleared so combat drops the target.
        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(RoomObservationSource.RoomChange, h.Observations[1].Source);
    }

    [Fact]
    public void DarkRoomAdvance_DoesNotWipe_KeepsInjectedCombatState()
    {
        // Dark-room regression: a dark room prints no name / exits / "Also
        // here:" line, so an empty entity list there means "can't see", not
        // "room is empty". Advancing through the dark must NOT fire the empty
        // RoomChange wipe — that empty observation is read as "room cleared" and
        // drops the Combat gate mid-fight, letting the walker step on through a
        // dark room while a mob is engaging us. Same setup as the stale-wipe
        // test (pre-move occupant that the freshness guard would normally wipe);
        // the ONLY difference is the advance is a dark-room advance, so it's
        // kept.
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        // Move N sent, then the move resolves as a DARK-room advance (1/1 → 1/3).
        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);
        h.Tracker.NoteDarkRoomEntered();

        Assert.True(h.Tracker.IsInDarkRoom);
        // No wipe: Current still holds the injected mob and no empty
        // RoomChange observation was emitted.
        Assert.Single(h.Observations);
        Assert.NotNull(h.Classifier.Current);
        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("giant rat", h.Classifier.Current.Value.Entities[0].ResolvedName);
    }

    [Fact]
    public void MoveConfirm_PostMoveArrival_Wiped_BelongsToOldRoom()
    {
        // Live repro (stuck "fighting" chip): the player sends a move, and
        // WHILE it is in flight a monster walks into the room being left.
        // Its Arrival timestamp is post-move, so the old freshness guard —
        // which kept any post-move observation — stranded that walk-in in
        // Current after the player stepped into a fresh, EMPTY room (no
        // "Also here:" line to overwrite it). The Combat gate stayed
        // asserted, the client kept firing the attack command, and the game
        // answered "Your command had no effect." An Arrival is always
        // relative to the room the player currently occupies, so a post-move
        // walk-in belongs to the OLD room and must be wiped on the room
        // change.
        using TrackerHarness h = new();
        h.AddMonster(1, "bandit");

        h.Tracker.NoteRoomObserved(
            new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);

        // Monster walks into the room being left — post-move timestamp, but
        // stale the instant we confirm the new room.
        RoomEntity bandit = new("bandit", "bandit", EntityKind.Monster, 1);
        h.Classifier.AppendArrivalEntity(bandit, "A bandit walks in from the south.");
        Assert.Single(h.Observations);
        Assert.Equal(RoomObservationSource.Arrival, h.Observations[0].Source);

        // New room confirms empty (no occupants displayed).
        h.Tracker.NoteRoomObserved(
            new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(RoomObservationSource.RoomChange, h.Observations[1].Source);
    }

    [Fact]
    public void MoveConfirm_PursuerArrivalAfterNewRoomAlsoHere_Kept_StopAndFight()
    {
        // Live repro (stock-20260708-000606): a monster that PURSUES us into the
        // new room announces itself with the same "<mob> walks in from <dir>."
        // arrival line. When that line lands in the window between the new room's
        // "Also here:" parse and its exits line (which confirms the move), it flips
        // Current.Source AlsoHere → Arrival right as the move-confirm decides
        // keep-vs-wipe. The old rule wiped it — the gate oscillated and the walker
        // grabbed a step, dragging us + the pursuer onward. Because a NEW-room
        // Also-here parsed before the arrival (_lastAlsoHereAt >= moveAt), this is a
        // genuine new-room pursuer and must be KEPT so combat holds (stop & fight).
        using TrackerHarness h = new();
        h.AddMonster(1, "forest spider");

        h.Tracker.NoteRoomObserved(
            new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);

        // New room's occupants parse (spider present), THEN the pursue-arrival
        // line lands before the exits line confirms.
        h.FeedAlsoHere("Also here: forest spider.");
        RoomEntity spider = new("forest spider", "forest spider", EntityKind.Monster, 1);
        h.Classifier.AppendArrivalEntity(spider, "A forest spider walks in from the south.");
        Assert.Equal(RoomObservationSource.Arrival, h.Classifier.Current!.Value.Source);

        int before = h.Observations.Count;
        h.Tracker.NoteRoomObserved(
            new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        // No wipe: the pursuer survives, combat stays engaged.
        Assert.Equal(before, h.Observations.Count);
        Assert.Equal(RoomObservationSource.Arrival, h.Classifier.Current!.Value.Source);
        Assert.NotEmpty(h.Classifier.Current.Value.Entities);
    }

    [Fact]
    public void MoveConfirm_PursuerArrival_WhileFleeing_Wiped_KeepRunning()
    {
        // Same shape as the stop-and-fight case, but we're actively fleeing. A
        // pursuer caught mid-flee must NOT re-arm the gate — we keep running
        // instead of turning to fight the thing we're fleeing. The flee probe
        // returning true suppresses the pursuer-keep, so the wipe stands.
        using TrackerHarness h = new();
        h.AddMonster(1, "forest spider");
        h.Classifier.FleeProbe = () => true;

        h.Tracker.NoteRoomObserved(
            new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);

        h.FeedAlsoHere("Also here: forest spider.");
        RoomEntity spider = new("forest spider", "forest spider", EntityKind.Monster, 1);
        h.Classifier.AppendArrivalEntity(spider, "A forest spider walks in from the south.");

        int before = h.Observations.Count;
        h.Tracker.NoteRoomObserved(
            new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        // Wipe fires: pursuer dropped so we don't re-engage mid-flee.
        Assert.Equal(before + 1, h.Observations.Count);
        Assert.Empty(h.Observations[^1].Entities);
        Assert.Equal(RoomObservationSource.RoomChange, h.Observations[^1].Source);
    }

    // ----- look-direction peek suppression ---------------------------
    //
    // A `look <dir>` peek renders a FULL room display (name, "You notice…",
    // "Also here:", "Obvious exits:") for the ADJACENT room. RoomTracker arms a
    // suppression window on the outbound look and consumes it on the exits line;
    // the "Also here:" line arrives first, so the classifier still sees the armed
    // flag and must NOT emit — otherwise it asserts the combat gate against a
    // room the player never entered, and pollutes Current so the real walk-in
    // can't re-engage.

    [Fact]
    public void Peek_AlsoHere_Dropped_NoObservation()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        h.Tracker.NoteLookSent();                    // look <dir> peek armed
        h.FeedAlsoHere("Also here: giant rat.");

        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    [Fact]
    public void Peek_ThenRealWalkIn_ReFiresObservation()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        // Peek east: the adjacent room's display is dropped.
        h.Tracker.NoteLookSent();
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Empty(h.Observations);

        // The peek's exits line consumes the suppression flag.
        h.Tracker.NoteRoomObserved(
            new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        // Now the player actually walks in — the same room display re-fires and
        // this time the classifier emits, so combat can engage.
        h.FeedAlsoHere("Also here: giant rat.");

        Assert.Single(h.Observations);
        Assert.NotNull(h.Classifier.Current);
        Assert.Equal("giant rat", h.Classifier.Current!.Value.Entities[0].ResolvedName);
    }

    private static void FeedLine(Terminal.LineExtractor lines, string text)
    {
        // The classifier's LineEmitted subscription is what we want
        // to exercise; reflect into the private event invoke since
        // LineExtractor.LineEmitted isn't a public test hook.
        System.Reflection.FieldInfo? field = typeof(Terminal.LineExtractor)
            .GetField("LineEmitted",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<Terminal.LineExtractor.EmittedLine> handler)
        {
            handler(new Terminal.LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }
}
