using System.IO;
using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// RoomEntityClassifier Also-Here parsing,
// prefix-strip resolution against MonsterMessageStore, player lookup
// against PlayerDatabase, and the Unknown-entity Warn path with
// raw-line Context payload.
public sealed class RoomEntityClassifierTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public FlavorPrefixStore Prefixes { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public List<LogEntry> LogEntries { get; } = new();
        public List<RoomEntitiesObservation> Observations { get; } = new();

        public Harness(GameDataCache? gameData = null)
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(
                Router, Monsters, Players, roomTracker: null, Log, gameData, Prefixes);
            Log.EntryAdded += LogEntries.Add;
            Classifier.EntitiesObserved += Observations.Add;
        }

        // Register a monster by canonical name. Flavor adjectives aren't per-monster
        // anymore — a prefixed display name resolves via the shared FlavorPrefixStore
        // (Prefixes), which starts from the built-in vocabulary; a test adds a custom
        // word with Prefixes.Add.
        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
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

    // ----- ResolveBaseName (flavor-prefix strip for room-aware resolution) -----

    [Fact]
    public void ResolveBaseName_StripsFlavorPrefixToBaseName()
    {
        using Harness h = new();
        h.AddMonster(101, "orc lieutenant");
        Assert.Equal("orc lieutenant", h.Classifier.ResolveBaseName("short orc lieutenant"));
        Assert.Equal("orc lieutenant", h.Classifier.ResolveBaseName("fierce orc lieutenant"));
        Assert.Equal("orc lieutenant", h.Classifier.ResolveBaseName("orc lieutenant"));
    }

    [Fact]
    public void ResolveBaseName_UnknownMonster_ReturnsNull()
    {
        using Harness h = new();
        h.AddMonster(101, "orc lieutenant");
        Assert.Null(h.Classifier.ResolveBaseName("giant spider"));
    }

    [Fact]
    public void NonMatchingLine_NoEmit()
    {
        using Harness h = new();
        h.Feed("Some random line not matching anything.");
        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    // ----- direct monster match (bare name) --------------------------

    [Fact]
    public void DirectMonsterMatch_BareName()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("giant rat", ent.RawName);
        Assert.Equal("giant rat", ent.ResolvedName);
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal(1, ent.MonsterNumber);
    }

    [Fact]
    public void DirectMonsterMatch_AnyCatalogName_MatchesBare()
    {
        // There's no AllowNoPrefix gate anymore: every catalog record matches its
        // bare name directly. A flavor adjective (if present) is stripped generically
        // one level up via the shared FlavorPrefixStore, so no per-monster prefix data
        // is involved. cave bear (one of 535 stock records that only ever appear bare)
        // resolves the same as any other.
        using Harness h = new();
        h.AddMonster(80, "cave bear");

        h.Feed("Also here: cave bear.");

        Assert.Single(h.Observations);
        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal("cave bear", ent.ResolvedName);
        Assert.Equal(80, ent.MonsterNumber);
    }

    // ----- global flavor-prefix vocabulary ---------------------------

    [Fact]
    public void GlobalPrefixVocab_ResolvesPrefix_NotInMonstersOwnList()
    {
        // The monster records NO FlavorPrefixes of its own, yet a standard adjective
        // from the shared vocabulary still resolves it — so a custom game needs no
        // per-monster prefix data for the common adjectives.
        using Harness h = new();
        h.AddMonster(1, "giant rat");   // no flavor prefixes

        h.Feed("Also here: large giant rat.");

        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal("giant rat", ent.ResolvedName);
        Assert.Equal(1, ent.MonsterNumber);
    }

    [Fact]
    public void GlobalPrefixVocab_CanonicalNameStartingWithPrefix_MatchesItself()
    {
        // "huge basilisk" is a canonical name (22 such in the seed) — it must resolve
        // to ITSELF, not be reduced to "basilisk" by the global strip. The full-name
        // match runs first, so the strip never sees it.
        using Harness h = new();
        h.AddMonster(5, "huge basilisk");
        h.AddMonster(6, "basilisk");   // the tail — must NOT win

        h.Feed("Also here: huge basilisk.");

        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal("huge basilisk", ent.ResolvedName);
        Assert.Equal(5, ent.MonsterNumber);
    }

    // ----- prefix-stripped monster match -----------------------------

    [Fact]
    public void PrefixStrippedMonsterMatch()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");   // "nasty" comes from the shared vocabulary

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
        h.AddMonster(1, "Giant Rat");

        h.Feed("Also here: NASTY giant rat.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
    }

    [Fact]
    public void UnknownPrefix_FallsToUnknown()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: stinking giant rat.");

        Assert.Single(h.Observations);
        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
    }

    [Fact]
    public void CustomVocab_AddedWord_ResolvesPrefix()
    {
        // A custom realm's adjective the built-in list doesn't have: once added to
        // the per-set FlavorPrefixStore, the prefixed name resolves generically.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Prefixes.Add("stinking");

        h.Feed("Also here: stinking giant rat.");

        RoomEntity ent = h.Observations[0].Entities[0];
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal("giant rat", ent.ResolvedName);
        Assert.Equal(1, ent.MonsterNumber);
    }

    [Fact]
    public void CustomVocab_RemovedWord_NoLongerStrips()
    {
        // Removing a default adjective takes it out of the vocabulary — the prefixed
        // name no longer resolves (no GameData here, so Pass 4 can't rescue it).
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Prefixes.Remove("large");

        h.Feed("Also here: large giant rat.");

        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
    }

    // ----- ReemitCurrent (auto-combat-off gate re-evaluation, report 121114) -----

    [Fact]
    public void ReemitCurrent_ReplaysTheCurrentObservation()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.Single(h.Observations);

        h.Classifier.ReemitCurrent();

        // A second, identical observation fires so every EntitiesObserved
        // subscriber (combat gate, deferred-cash / get-items / search acquisition
        // holds) re-evaluates without waiting for a fresh room render.
        Assert.Equal(2, h.Observations.Count);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
    }

    [Fact]
    public void ReemitCurrent_BeforeAnyObservation_IsNoOp()
    {
        using Harness h = new();
        h.Classifier.ReemitCurrent();
        Assert.Empty(h.Observations);
    }

    // The roster does not outlive the link: it is dropped without an observation
    // (nothing can act on one offline), a re-emit has nothing to replay, and the
    // first arrival after the reconnect starts a roster of its own.
    [Fact]
    public void NoteGameLeft_DropsTheRoster_WithoutAnObservation()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.Classifier.NoteGameLeft();
        h.Classifier.ReemitCurrent();

        Assert.Null(h.Classifier.Current);
        Assert.Single(h.Observations);

        h.Classifier.AppendArrivalEntity(
            new RoomEntity("orc", "orc", EntityKind.Monster, 2), "An orc walks into the room from north.");
        Assert.Equal("orc", Assert.Single(h.Observations[1].Entities).ResolvedName);
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
        PlayerDatabase players = new(new ProfileService(), () => null);

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

    // ----- first-word-strip Monsters-table fallback ------------------

    // Build a GameDataCache backed by a temp set carrying just a Monsters
    // table. Caller deletes the returned directory.
    private static (GameDataCache cache, string dir) MonstersTable(string monstersJson)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "mudplay-classifier-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "alpha"));
        File.WriteAllText(Path.Combine(dir, "alpha", "Monsters.json"), monstersJson);
        GameDataCache cache = new(dir);
        cache.SwitchSet("alpha");
        return (cache, dir);
    }

    // A monster whose Monsters-table row exists but whose leading flavor word
    // isn't in any MonsterMessageRecord resolves by stripping the first word.
    [Fact]
    public void UnknownFlavorWord_ResolvesToMonstersTableRow()
    {
        (GameDataCache cache, string dir) = MonstersTable(
            """[ { "Number": 82, "Name": "kobold" } ]""");
        try
        {
            using Harness h = new(cache);

            h.Feed("Also here: vicious kobold.");

            Assert.Single(h.Observations);
            RoomEntity ent = h.Observations[0].Entities[0];
            Assert.Equal(EntityKind.Monster, ent.Kind);
            Assert.Equal(82, ent.MonsterNumber);
            Assert.Equal("vicious kobold", ent.RawName);
            Assert.Equal("kobold", ent.ResolvedName);

            // The row is flagged for a vocabulary add, with the leading word carried
            // in Context so the double-click adds it to the FlavorPrefixStore.
            LogEntry warn = h.LogEntries.Find(e =>
                e.Source == RoomEntityClassifier.MissingFlavorCategory);
            Assert.NotEqual(default, warn);
            Assert.Contains("vicious kobold", warn.Message);
            Assert.Equal("vicious", warn.Context);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // The remainder after the first word must match a Monsters-table row —
    // otherwise the entry stays Unknown.
    [Fact]
    public void UnknownFlavorWord_RemainderNotAMonster_StaysUnknown()
    {
        (GameDataCache cache, string dir) = MonstersTable(
            """[ { "Number": 82, "Name": "kobold" } ]""");
        try
        {
            using Harness h = new(cache);

            h.Feed("Also here: vicious wombat.");

            Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
            Assert.DoesNotContain(h.LogEntries, e =>
                e.Source == RoomEntityClassifier.MissingFlavorCategory);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // Only the FIRST word is stripped: a two-word flavor prefix won't reduce
    // to the base name, so the fallback doesn't fire.
    [Fact]
    public void UnknownFlavorWord_OnlyStripsFirstWord()
    {
        (GameDataCache cache, string dir) = MonstersTable(
            """[ { "Number": 82, "Name": "kobold" } ]""");
        try
        {
            using Harness h = new(cache);

            h.Feed("Also here: large vicious kobold.");

            // Remainder "vicious kobold" isn't a Monsters-table name.
            Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // Without a GameDataCache the fallback is inert — the entry stays Unknown.
    [Fact]
    public void UnknownFlavorWord_NoGameData_StaysUnknown()
    {
        using Harness h = new();

        h.Feed("Also here: vicious kobold.");

        Assert.Equal(EntityKind.Unknown, h.Observations[0].Entities[0].Kind);
    }

    // A greet-only NPC ("old man") whose MonsterMessageRecord carries no Monsters link still
    // resolves to its Monsters-table row by name — so downstream (the combat gate) can read its
    // relationship / KillOnSight overlay instead of getting a numberless Monster and defaulting
    // it to a fightable enemy. Regression: the neutral-NPC on-sight-attack bug.
    [Fact]
    public void MessageRecordWithoutLink_ResolvesToMonstersTableRowByName()
    {
        (GameDataCache cache, string dir) = MonstersTable(
            """[ { "Number": 499, "Name": "old man" } ]""");
        try
        {
            using Harness h = new(cache);
            h.Monsters.Messages.Add(new MonsterMessageRecord(Id: "OM", Name: "old man", Links: null));

            h.Feed("Also here: old man.");

            RoomEntity ent = h.Observations[0].Entities[0];
            Assert.Equal(EntityKind.Monster, ent.Kind);
            Assert.Equal(499, ent.MonsterNumber);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // ----- full-name Monsters-table match ----------------------------

    // A monster whose Monsters-table row exists under its full multi-word name
    // but which has no MonsterMessageRecord (a lair boss like "brigand chief")
    // resolves by matching the WHOLE entry — not by stripping the first word,
    // which would reduce it to "chief" and find nothing. Classified as the
    // canonical monster with no missing-flavor warning.
    [Fact]
    public void FullNameMultiWordMonster_ResolvesToMonstersTableRow()
    {
        (GameDataCache cache, string dir) = MonstersTable(
            """[ { "Number": 500, "Name": "brigand chief" } ]""");
        try
        {
            using Harness h = new(cache);

            h.Feed("Also here: brigand chief.");

            Assert.Single(h.Observations);
            RoomEntity ent = h.Observations[0].Entities[0];
            Assert.Equal(EntityKind.Monster, ent.Kind);
            Assert.Equal(500, ent.MonsterNumber);
            Assert.Equal("brigand chief", ent.RawName);
            Assert.Equal("brigand chief", ent.ResolvedName);

            // Canonical name — no flavor prefix was stripped, so no warning.
            Assert.DoesNotContain(h.LogEntries, e =>
                e.Source == RoomEntityClassifier.MissingFlavorCategory);
            Assert.DoesNotContain(h.LogEntries, e =>
                e.Source == RoomEntityClassifier.LogCategory);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // ----- list-shape forms (commas, Oxford-and, mixed) --------------

    [Fact]
    public void TwoEntries_Comma()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
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
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "goblin");
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
        h.AddMonster(1, "giant rat");

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
        // The classifier checks Monsters first: shopkeepers / quest-givers
        // can collide with real names, and monster wins.
        using Harness h = new();
        h.AddMonster(99, "Bob");
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
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "kobold thief");

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
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "giant rat");
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
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc rogue");

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
        h.AddMonster(2, "orc rogue");

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
        h.AddMonster(1, "giant rat");
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
        // RemoveDepartedEntity is monster-kind only: a departed player is taken out
        // by RemoveDepartedPlayer, so this call leaves Bob and fires nothing.
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
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "goblin");

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
        h.AddMonster(1, "giant rat");
        h.AddPlayer("Bob");

        h.Feed("Also here: giant rat.");
        h.Feed("Also here: Bob.");

        Assert.NotNull(h.Classifier.Current);
        Assert.Equal("Also here: Bob.", h.Classifier.Current!.Value.RawAlsoHereLine);
        Assert.Equal(EntityKind.Player, h.Classifier.Current.Value.Entities[0].Kind);
    }

    // ----- Multi-line wrap (80-col server boundary) ------------------

    // MajorMUD wraps occupant lists at the 80-col boundary mid-token,
    // so an Also Here line can span N rows until a period closes it.
    // The classifier buffers continuation rows via the LineExtractor
    // subscription and parses the joined text on close.
    [Fact]
    public void Wrap_AcrossTwoLines_ParsesAllOccupants()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "carrion beast");
        h.AddMonster(3, "giant rat");
        h.AddMonster(4, "acid slime");
        h.AddMonster(5, "kobold thief");

        // Attach the extractor so the wrap-buffer path is live.
        ServerFeed feed = new();
        h.Classifier.AttachLineExtractor(feed.Lines);

        // Feed wrapped rows in the order the server produces them.
        feed.Line("Also here: angry giant rat, nasty carrion beast, giant rat, acid slime, short");
        feed.Line("kobold thief.");

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
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "acid slime");
        h.AddMonster(3, "kobold thief");

        ServerFeed feed = new();
        h.Classifier.AttachLineExtractor(feed.Lines);

        feed.Line("Also here: giant rat,");
        feed.Line("acid slime,");
        feed.Line("kobold thief.");

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
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public RoomTracker Tracker { get; }
        public RoomEntityClassifier Classifier { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();

        public TrackerHarness()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "mudplay-classifier-tracker-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), MonstersJson);
            File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);

            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            // gameData wired so Pass 0 (room-aware) + Pass 3 (Monsters-table) can resolve.
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Tracker, log: null, gameData: cache);
            // Wire room-aware Pass 0 (placed NPC + lair + Summoned-By spawns + their summons) —
            // the same resolver production uses.
            var roomAware = new RoomAwareMonsterResolver(
                cache,
                () => Tracker.State.CurrentRoom,
                Classifier.ResolveBaseName,
                new MonsterSpawnIndex(cache),
                new MonsterSummonTargetsIndex(cache));
            Classifier.SetRoomAwareResolver(roomAware.ResolveInCurrentRoom);
            Classifier.EntitiesObserved += Observations.Add;
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name,
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
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 5, "Name": "Asylum",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5, "NPC": 499,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/3", "D": "0" },
              { "Map Number": 1, "Room Number": 7, "Name": "Rat Den",
                "Light": 0, "Shop": 0, "Lair": "(Max 2): 7,[6-0-5-1]", "Delay": 5,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/3" },
              { "Map Number": 1, "Room Number": 9, "Name": "Crypt",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5, "NPC": 500,
                "N": "0", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/3", "D": "0" }
            ]
            """;

        // #1 giant rat is the "global" variant (also a message-catalog record via AddMonster);
        // #7 is the Rat Den's lair variant (same name, different Number). #499 is the placed
        // "old man" NPC in the Asylum. #404 zombie is listed BEFORE #303 so a global name match
        // picks #404, but necromancer #500 (placed in the Crypt) summons #303 on death — room-
        // aware should widen to that summoned minion.
        private const string MonstersJson = """
            [
              { "Number": 1,   "Name": "giant rat" },
              { "Number": 7,   "Name": "giant rat" },
              { "Number": 404, "Name": "zombie" },
              { "Number": 303, "Name": "zombie" },
              { "Number": 499, "Name": "old man" },
              { "Number": 500, "Name": "necromancer", "DeathSpell": 900 }
            ]
            """;

        private const string SpellsJson = """
            [ { "Number": 900, "Name": "raise dead", "Abil-0": 12, "AbilVal-0": 303 } ]
            """;
    }

    // ----- whether the roster is the whole of who is here --------------

    // A room walked into whose display lists nobody: the roster is empty, and that
    // is the room as read.
    [Fact]
    public void AMoveIntoARoomThatListsNobody_LeavesAnEmptyRosterKnownWhole()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");
        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.True(h.Classifier.RosterIsWhole);

        h.Tracker.NoteMoveSent(Direction.N, DateTimeOffset.UtcNow.AddMilliseconds(50));
        h.Tracker.NoteRoomObserved(new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        Assert.Empty(h.Classifier.Current!.Value.Entities);
        Assert.True(h.Classifier.RosterIsWhole);
    }

    // The new room listed two, one died ahead of the exits line, and the roster is
    // emptied as stale. The other is still here: empty is not the room as read.
    [Fact]
    public void ARosterReadSinceTheMove_AndThenEmptied_IsNotKnownWhole()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(404, "zombie");
        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));

        h.Tracker.NoteMoveSent(Direction.N, DateTimeOffset.UtcNow.AddSeconds(-1));
        h.FeedAlsoHere("Also here: giant rat, zombie.");
        Assert.True(h.Classifier.RemoveDeadEntity("giant rat"));
        h.Tracker.NoteRoomObserved(new RoomObservation("North Square", new HashSet<Direction> { Direction.S }));

        Assert.Empty(h.Classifier.Current!.Value.Entities);
        Assert.False(h.Classifier.RosterIsWhole);
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

        // No wipe: the fresh post-move occupant survives. AND the confirmation re-emits
        // the kept roster — those occupants were parsed against the STALE current room
        // (still Town Gates), so room-dependent gates (the loop's "only attack in lair
        // rooms" suppression) must re-decide now that CurrentRoom points at the room we
        // actually entered (report paradigm-20260915-130624). The re-emit carries the
        // same roster, unchanged.
        Assert.Equal(2, h.Observations.Count);
        Assert.Equal(RoomObservationSource.AlsoHere, h.Observations[^1].Source);
        Assert.Equal("giant rat", h.Observations[^1].Entities.Single().ResolvedName);
        Assert.NotNull(h.Classifier.Current);
        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("giant rat", h.Classifier.Current.Value.Entities[0].ResolvedName);
    }

    // Login with a stale saved room: the first display shows a monster, then the
    // `rm` resync corrects the room. No move was sent, so the monster is still here.
    [Fact]
    public void PositionCorrection_NoMoveSentThisSession_KeepsRoster()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        h.Tracker.SetLocated(new RoomKey(1, 3));

        Assert.Equal(2, h.Observations.Count);
        Assert.Equal("giant rat", h.Observations[^1].Entities.Single().ResolvedName);
        Assert.Single(h.Classifier.Current!.Value.Entities);
    }

    // ----- room-aware resolution (Pass 0) ----------------------------

    [Fact]
    public void RoomAware_ResolvesPlacedNpcByName()
    {
        using TrackerHarness h = new();
        // Standing in the Asylum, whose room record places the "old man" NPC (#499).
        h.Tracker.NoteRoomObserved(new RoomObservation("Asylum", new HashSet<Direction> { Direction.U }));

        h.FeedAlsoHere("Also here: old man.");

        RoomEntity ent = h.Classifier.Current!.Value.Entities[0];
        Assert.Equal(EntityKind.Monster, ent.Kind);
        Assert.Equal(499, ent.MonsterNumber);   // pinned to the room's placed NPC
    }

    [Fact]
    public void RoomAware_DisambiguatesHomonymToRoomLairVariant()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");   // the "global" giant rat (#1) via the message catalog
        // Standing in the Rat Den, whose lair is the #7 giant-rat variant.
        h.Tracker.NoteRoomObserved(new RoomObservation("Rat Den", new HashSet<Direction> { Direction.D }));

        h.FeedAlsoHere("Also here: giant rat.");

        // Same name, two Numbers — room-aware pins it to the lair's #7, not the message-catalog
        // #1, so per-monster overrides land on the variant actually in this room.
        Assert.Equal(7, h.Classifier.Current!.Value.Entities[0].MonsterNumber);
    }

    [Fact]
    public void RoomAware_ResolvesMonsterSummonedByPlacedMonster()
    {
        using TrackerHarness h = new();
        // The Crypt places necromancer #500, which summons the zombie #303 on death. A global
        // name match would pick #404 (listed first); room-aware widens to the summoner's minion.
        h.Tracker.NoteRoomObserved(new RoomObservation("Crypt", new HashSet<Direction> { Direction.U }));

        h.FeedAlsoHere("Also here: zombie.");

        Assert.Equal(303, h.Classifier.Current!.Value.Entities[0].MonsterNumber);
    }

    [Fact]
    public void RoomAware_FallsThroughToNameMatch_WhenNotRoomListed()
    {
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");
        // In the Asylum (places #499 old man) — a giant rat here isn't in the room record.
        h.Tracker.NoteRoomObserved(new RoomObservation("Asylum", new HashSet<Direction> { Direction.U }));

        h.FeedAlsoHere("Also here: giant rat.");

        // Room-aware finds no match → falls through to the name-based passes (message catalog #1).
        Assert.Equal(1, h.Classifier.Current!.Value.Entities[0].MonsterNumber);
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
    public void DarkRoomAdvance_KeepsActiveCombatTarget_ShedsRest()
    {
        // Dark-room regression: a dark room prints no name / exits / "Also
        // here:" line, so we can't re-derive the new room's roster. The mob we're
        // actively fighting must survive the advance (dropping it fires an empty
        // "room cleared" observation, drops the Combat gate mid-fight, and lets
        // the walker step on through while the mob is still engaging us). With the
        // active-target probe pointing at "giant rat", the advance carries it
        // across.
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");
        h.Classifier.ActiveCombatTargetProbe = () => "giant rat";

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        // Move N sent, then the move resolves as a DARK-room advance (1/1 → 1/3).
        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);
        h.Tracker.NoteDarkRoomEntered();

        Assert.True(h.Tracker.IsInDarkRoom);
        // The active target is carried across the reset.
        Assert.NotNull(h.Classifier.Current);
        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("giant rat", h.Classifier.Current.Value.Entities[0].ResolvedName);
    }

    [Fact]
    public void DarkRoomAdvance_NoActiveTarget_ResetsRoster()
    {
        // With no mob we're fighting, a dark advance resets the accumulated roster
        // to empty so a pursuer/phantom from the room we just left doesn't linger.
        // This is the half that kills the all-night accumulation: nothing to keep,
        // so the list clears and the next room starts fresh.
        using TrackerHarness h = new();
        h.AddMonster(1, "giant rat");
        // No ActiveCombatTargetProbe set → keep nothing.

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        h.FeedAlsoHere("Also here: giant rat.");
        Assert.Single(h.Observations);

        DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddSeconds(1);
        h.Tracker.NoteMoveSent(Direction.N, moveAt);
        h.Tracker.NoteDarkRoomEntered();

        Assert.True(h.Tracker.IsInDarkRoom);
        // Reset fired: an empty RoomChange observation, Current now empty.
        Assert.Equal(2, h.Observations.Count);
        Assert.Empty(h.Observations[1].Entities);
        Assert.Equal(RoomObservationSource.RoomChange, h.Observations[1].Source);
        Assert.NotNull(h.Classifier.Current);
        Assert.Empty(h.Classifier.Current!.Value.Entities);
    }

    [Fact]
    public void ConsecutiveDarkAdvances_ArrivalsDoNotAccumulate()
    {
        // The reported bug: over a corridor hunt, a pursuit arrival appends each
        // room and the dark roster never resets, growing to hundreds of phantom
        // mobs that trip the max-monsters gate (we never engage) while the Combat
        // gate stalls for 30s a room. Each dark advance must reset the list so an
        // arrival that lands afterward is the ONLY occupant — the count stays
        // bounded, not cumulative.
        using TrackerHarness h = new();
        h.AddMonster(1, "dark-elf sentry");
        // No active target — the mob tanks a party member, we never engage it.

        h.Tracker.NoteRoomObserved(new RoomObservation("Town Gates", new HashSet<Direction> { Direction.N }));
        RoomEntity pursuer = new("dark-elf sentry", "dark-elf sentry", EntityKind.Monster, 1);

        for (int room = 0; room < 5; room++)
        {
            // Alternate N/S so each advance is a real room change (1/1 ↔ 1/3).
            Direction dir = room % 2 == 0 ? Direction.N : Direction.S;
            DateTimeOffset moveAt = DateTimeOffset.UtcNow.AddMilliseconds(room * 10);
            h.Tracker.NoteMoveSent(dir, moveAt);
            h.Tracker.NoteDarkRoomEntered();
            // A pursuer walks in after the advance resolves (as RoomEntryWatcher
            // would append it live).
            h.Classifier.AppendArrivalEntity(pursuer, rawWireLine: "arrival");

            // The roster never carries more than this room's single arrival —
            // the reset shed the prior rooms' pursuers instead of stacking them.
            Assert.NotNull(h.Classifier.Current);
            Assert.Single(h.Classifier.Current!.Value.Entities);
        }
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

}
