using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// RoomEntryWatcher + the regex behind
// KnownPatterns.RoomEntryArrival. Covers article
// stripping, monster vs player lookup, colour-hint fallback for
// unknown names, direction capture, and the
// RoomEntityClassifier.AppendArrivalEntity roundtrip.
public sealed class RoomEntryWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Watcher { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();
        public List<RoomEntryArrivalEvent> Arrivals { get; } = new();
        public List<LogEntry> LogEntries { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Watcher = new RoomEntryWatcher(Router, Classifier, Log);
            Classifier.EntitiesObserved += Observations.Add;
            Watcher.ArrivalObserved += Arrivals.Add;
            Log.EntryAdded += LogEntries.Add;
        }

        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void AddPlayer(string givenName)
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: givenName,
                FamilyName: string.Empty,
                Class: "Warrior",
                Race: "Human",
                Alignment: "Neutral",
                Title: null,
                Gang: null,
                Role: null,
                FirstSeenUtc: DateTime.UtcNow,
                LastSeenUtc: DateTime.UtcNow));
        }

        public void Feed(string line, CellAttributes[]? attributes = null)
        {
            LineExtractor.EmittedLine emitted = new(
                line,
                attributes ?? Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow,
                IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // Build a CellAttributes[] whose first non-space cell carries
        // the given foreground index. Used to inject the yellow /
        // red colour hint into the arrival line.
        public CellAttributes[] AttrsWithFg(string text, int index)
        {
            CellAttributes[] arr = new CellAttributes[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                arr[i] = CellAttributes.Default
                    .WithForeground(TerminalColor.Indexed(index));
            }
            return arr;
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- basic parse + classification --------------------------------

    [Fact]
    public void KnownMonster_AddedAsMonster()
    {
        using Harness h = new();
        h.AddMonster(1, "lashworm");   // "fierce" strips through the built-in prefix list

        h.Feed("A fierce lashworm crawls into the room from nowhere.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("fierce lashworm", h.Arrivals[0].Name);
        Assert.Equal("nowhere", h.Arrivals[0].Direction);
    }

    [Fact]
    public void KnownMonster_StripsLeadingTheArticle()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("The giant rat walks into the room from north.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("giant rat", h.Arrivals[0].Name);
        Assert.Equal("north", h.Arrivals[0].Direction);
    }

    [Fact]
    public void KnownMonster_StripsLeadingAnArticle()
    {
        using Harness h = new();
        h.AddMonster(1, "ogre");

        h.Feed("An ogre stomps into the room from southeast.");

        Assert.Single(h.Arrivals);
        Assert.Equal("ogre", h.Arrivals[0].Name);
        Assert.Equal("southeast", h.Arrivals[0].Direction);
    }

    [Fact]
    public void KnownMonster_LocativePreposition_In_AlsoParses()
    {
        // Verified live: "A carrion beast creeps in the room from
        // nowhere." Slow-creep verbs ("creeps", "slithers", etc.)
        // use locative `in` rather than directional `into`.
        using Harness h = new();
        h.AddMonster(1, "carrion beast");

        h.Feed("A carrion beast creeps in the room from nowhere.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("carrion beast", h.Arrivals[0].Name);
        Assert.Equal("nowhere", h.Arrivals[0].Direction);
    }

    [Fact]
    public void KnownMonster_GreetTextArrival_NoRoomBangTerminated_Parses()
    {
        // Per-monster greet text (GreetTXT) drops " the room", adds a
        // "the" before the direction, and ends on a bang:
        // "A cave bear lumbers in from the south!". Verified live — this
        // form previously slipped past the arrival regex, so the cave bear
        // never entered the combat gate and was never attacked.
        using Harness h = new();
        h.AddMonster(1, "cave bear");

        h.Feed("A cave bear lumbers in from the south!");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("cave bear", h.Arrivals[0].Name);
        Assert.Equal("south", h.Arrivals[0].Direction);
    }

    [Theory]
    [InlineData("A kobold thief sneaks into the room from the south.", "kobold thief", "south")]
    [InlineData("A carrion beast creeps in the room from the north.", "carrion beast", "north")]
    public void KnownMonster_CanonicalRoomForm_WithTheBeforeDirection_Parses(
        string line, string name, string direction)
    {
        // The canonical "into/in the room from" form can also carry a
        // "the" before the direction ("from the south"), not just the
        // bare cardinals ("from south"). Both forms reach the gate.
        using Harness h = new();
        h.AddMonster(1, name);

        h.Feed(line);

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal(name, h.Arrivals[0].Name);
        Assert.Equal(direction, h.Arrivals[0].Direction);
    }

    [Fact]
    public void KnownPlayer_AddedAsPlayer()
    {
        using Harness h = new();
        h.AddPlayer("Bob");

        h.Feed("Bob walks into the room from south.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Player, h.Arrivals[0].Kind);
        Assert.Equal("Bob", h.Arrivals[0].Name);
    }

    // ----- sneak-arrival notice ----------------------------------------

    [Fact]
    public void SneakNotice_ClassifiedAsPlayer_EvenWithMonsterHue()
    {
        // "You notice <name> sneaking in from the <dir>." is always a player
        // who failed a sneak into our room. The wire paints the line the
        // monster hue (yellow), which previously mis-tagged it a null-numbered
        // Monster that held the Combat gate open forever. It must classify
        // Player regardless of the colour.
        using Harness h = new();
        string line = "You notice Gronx sneaking in from the north.";
        h.Feed(line, h.AttrsWithFg(line, index: 11));     // bright yellow (monster hue)

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Player, h.Arrivals[0].Kind);
        Assert.Equal("Gronx", h.Arrivals[0].Name);
        Assert.Equal("north", h.Arrivals[0].Direction);

        // The bare player name is captured — not the polluted "You notice Gronx"
        // the greedy RoomEntryArrival pattern used to grab — and it carries no
        // monster number, so nothing engageable holds the gate.
        RoomEntity appended = h.Observations[0].Entities[0];
        Assert.Equal("Gronx", appended.ResolvedName);
        Assert.Equal(EntityKind.Player, appended.Kind);
        Assert.Null(appended.MonsterNumber);
    }

    [Fact]
    public void SneakNotice_DoesNotDoubleFireAsMonsterArrival()
    {
        // RoomEntryArrival's (?!You notice ) guard must keep it from also
        // matching the sneak line — MessageRouter is fan-out, so without the
        // guard both patterns would fire and re-create the Monster phantom.
        using Harness h = new();
        h.Feed("You notice Gronx sneaking in from the north.");

        Assert.Single(h.Arrivals);
        Assert.DoesNotContain(h.Arrivals, a => a.Kind == EntityKind.Monster);
    }

    // The Stock engine's wording of the same notice: "sneak in from", not "sneaking".
    [Fact]
    public void SneakNotice_StockWording_ClassifiedAsPlayer()
    {
        using Harness h = new();
        string line = "You notice Gronx sneak in from the north.";
        h.Feed(line, h.AttrsWithFg(line, index: 11));

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Player, h.Arrivals[0].Kind);
        Assert.Equal("Gronx", h.Arrivals[0].Name);
        Assert.Equal("north", h.Arrivals[0].Direction);
        Assert.Null(h.Observations[0].Entities[0].MonsterNumber);
    }

    // ----- direction tolerance -----------------------------------------

    [Theory]
    [InlineData("north")]
    [InlineData("south")]
    [InlineData("east")]
    [InlineData("west")]
    [InlineData("northeast")]
    [InlineData("northwest")]
    [InlineData("southeast")]
    [InlineData("southwest")]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("nowhere")]
    public void DirectionVariants_AllParseSuccessfully(string direction)
    {
        using Harness h = new();
        h.AddMonster(1, "rat");

        h.Feed($"A rat crawls into the room from {direction}.");

        Assert.Single(h.Arrivals);
        Assert.Equal(direction, h.Arrivals[0].Direction);
    }

    [Fact]
    public void DifferentVerbs_AllMatch()
    {
        using Harness h = new();
        h.AddMonster(1, "lashworm");
        h.AddMonster(2, "phantom");
        h.AddMonster(3, "guard");

        h.Feed("A lashworm crawls into the room from nowhere.");
        h.Feed("A phantom teleports into the room from nowhere.");
        h.Feed("A guard walks into the room from north.");

        Assert.Equal(3, h.Arrivals.Count);
    }

    // ----- classifier append-to-current --------------------------------

    [Fact]
    public void AppendsToExistingObservation()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "goblin");

        // Initial Also-Here: just the rat.
        h.Feed("Also here: giant rat.");
        Assert.Single(h.Observations);
        Assert.Single(h.Observations[0].Entities);

        // Arrival of a goblin → second observation with both.
        h.Feed("A goblin walks into the room from east.");

        Assert.Equal(2, h.Observations.Count);
        Assert.Equal(2, h.Observations[1].Entities.Count);
        Assert.Equal("giant rat", h.Observations[1].Entities[0].ResolvedName);
        Assert.Equal("goblin",    h.Observations[1].Entities[1].ResolvedName);
    }

    [Fact]
    public void NoPriorAlsoHere_StartsFreshObservation()
    {
        // Mob spawns before any Also-Here was parsed (e.g. user just
        // connected mid-action). Observation list is empty → arrival
        // synthesises one with just the new entity.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("A giant rat crawls into the room from nowhere.");

        Assert.Single(h.Observations);
        Assert.Single(h.Observations[0].Entities);
        Assert.Equal(EntityKind.Monster, h.Observations[0].Entities[0].Kind);
    }

    // ----- colour-hint fallback ----------------------------------------

    [Fact]
    public void UnknownName_YellowHint_TreatedAsMonster()
    {
        using Harness h = new();
        // No monster + no player registered. Colour says yellow → monster.
        string line = "A fuzzy thingamajig crawls into the room from nowhere.";
        h.Feed(line, h.AttrsWithFg(line, index: 11));     // bright yellow

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("fuzzy thingamajig", h.Arrivals[0].Name);
    }

    [Fact]
    public void UnknownName_RedHint_TreatedAsPlayer()
    {
        using Harness h = new();
        string line = "Whatsisname walks into the room from south.";
        h.Feed(line, h.AttrsWithFg(line, index: 9));     // bright red

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Player, h.Arrivals[0].Kind);
    }

    [Fact]
    public void UnknownName_NoColour_FallsBackToMonster()
    {
        // No colour attributes at all → safer default is monster (the
        // combat-gating consumer would rather pause for a fake mob
        // than walk into a real one we didn't recognise).
        using Harness h = new();
        h.Feed("A weirdo crawls into the room from nowhere.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);

        // Warn log for the unknown.
        LogEntry warn = h.LogEntries.Find(e =>
            e.Source == RoomEntryWatcher.LogCategory &&
            e.Severity == LogSeverity.Warn);
        Assert.NotEqual(default, warn);
    }

    [Fact]
    public void KnownMonster_ColourSaysPlayer_TrustsLookup_LogsWarn()
    {
        // Edge case: data table says monster, colour says player.
        // Lookup wins (gives us the priority info) but we warn so
        // the user knows the data might be stale.
        using Harness h = new();
        h.AddMonster(1, "guard");
        string line = "A guard walks into the room from north.";
        h.Feed(line, h.AttrsWithFg(line, index: 1));     // red

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);

        LogEntry warn = h.LogEntries.Find(e =>
            e.Source == RoomEntryWatcher.LogCategory &&
            e.Severity == LogSeverity.Warn);
        Assert.NotEqual(default, warn);
        Assert.Contains("colour", warn.Message);
    }

    // ----- non-matching lines ------------------------------------------

    [Fact]
    public void NonArrivalLine_NoEmit()
    {
        using Harness h = new();
        h.Feed("Also here: giant rat.");                 // matches RoomAlsoHere, not arrival
        h.Feed("You hit a goblin for 5 damage!");
        h.Feed("Some random other line.");

        Assert.Empty(h.Arrivals);
    }

    // ----- prefix-stripping integration --------------------------------

    [Fact]
    public void FlavoredArrival_ResolvesToBaseMonster()
    {
        // "A nasty giant rat crawls in" → article strip → "nasty giant
        // rat" → the built-in prefix strip → "giant rat" matches monster.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("A nasty giant rat crawls into the room from nowhere.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("nasty giant rat", h.Arrivals[0].Name);

        // The classifier's appended RoomEntity carries the resolved
        // base — exactly what CombatManager sorts + picks against.
        RoomEntity appended = h.Observations[0].Entities[0];
        Assert.Equal("nasty giant rat", appended.RawName);
        Assert.Equal("giant rat",       appended.ResolvedName);
        Assert.Equal(1,                 appended.MonsterNumber);
    }

    // ----- article-as-name fallback ------------------------------------

    [Fact]
    public void MonsterNameBeginningWithThe_ResolvesViaArticleFallback()
    {
        // Stock has exactly one monster whose name legitimately begins
        // with "The " (number 251 — never a valid combat target). Its
        // arrival line "The {name} … into the room from …" would strip
        // to a bare miss; the with-article retry recovers the record so
        // the monster number survives for a do-not-attack rule.
        using Harness h = new();
        h.AddMonster(251, "The Eternal");

        h.Feed("The Eternal floats into the room from nowhere.");

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("The Eternal", h.Arrivals[0].Name);

        // Critical: the monster number is preserved (not dropped to the
        // colour-hint fallback which would null it out).
        RoomEntity appended = h.Observations[0].Entities[0];
        Assert.Equal("The Eternal", appended.ResolvedName);
        Assert.Equal(251,           appended.MonsterNumber);
    }

    // ----- colour-scan fallback: custom, unstructured spawn lines -------

    // Build attributes where only the cells spanning `name` carry the yellow
    // index (3); every other cell is default-colour.
    private static CellAttributes[] YellowOnName(string text, string name)
    {
        CellAttributes[] arr = new CellAttributes[text.Length];
        for (int i = 0; i < text.Length; i++) arr[i] = CellAttributes.Default;
        int idx = text.IndexOf(name, StringComparison.Ordinal);
        for (int i = idx; i >= 0 && i < idx + name.Length; i++)
            arr[i] = CellAttributes.Default.WithForeground(TerminalColor.Indexed(3));
        return arr;
    }

    // A custom spawn line — no "into the room" / "from <dir>", so the structured
    // patterns miss it — with the whole line painted yellow. The colour scan finds
    // the yellow monster name and treats it as an arrival.
    [Fact]
    public void CustomSpawnLine_WholeLineYellow_TreatedAsArrival()
    {
        using Harness h = new();
        h.AddMonster(1, "muckworm");
        string line = "A muckworm darts out of the mud!";
        h.Feed(line, h.AttrsWithFg(line, index: 3));

        Assert.Single(h.Arrivals);
        Assert.Equal(EntityKind.Monster, h.Arrivals[0].Kind);
        Assert.Equal("muckworm", h.Arrivals[0].Name);
    }

    // Same line but only the NAME is yellow (name-yellow constructor); the rest is
    // default-colour. Still recognised.
    [Fact]
    public void CustomSpawnLine_OnlyNameYellow_TreatedAsArrival()
    {
        using Harness h = new();
        h.AddMonster(1, "muckworm");
        string line = "A muckworm darts out of the mud!";
        h.Feed(line, YellowOnName(line, "muckworm"));

        Assert.Single(h.Arrivals);
        Assert.Equal("muckworm", h.Arrivals[0].Name);
    }

    // A two-word name inside an all-yellow custom line — the 1–4-word window finds it.
    [Fact]
    public void CustomSpawnLine_MultiWordName_Yellow_TreatedAsArrival()
    {
        using Harness h = new();
        h.AddMonster(1, "acid slime");
        string line = "An acid slime bubbles out of the swamp!";
        h.Feed(line, h.AttrsWithFg(line, index: 3));

        Assert.Single(h.Arrivals);
        Assert.Equal("acid slime", h.Arrivals[0].Name);
    }

    // The client's own yellow notices can name a monster ("[Round 1 dealt: You 59,
    // small green slime 0]"). Reading one as an arrival re-engaged a monster that had
    // just died (report paradigm-20260928-230226).
    [Fact]
    public void ClientNotice_YellowMonsterName_NotAnArrival()
    {
        using Harness h = new();
        h.AddMonster(1, "small green slime");
        string line = "[Round 1 dealt: You 59, small green slime 0]";
        h.Feed(line, h.AttrsWithFg(line, index: 3));

        Assert.Empty(h.Arrivals);
    }

    // A plain room description names a monster but in default colour (all white) —
    // never an arrival, regardless of the room-descriptions-on setting.
    [Fact]
    public void RoomDescription_WhiteMonsterName_NotAnArrival()
    {
        using Harness h = new();
        h.AddMonster(1, "skeleton");
        string line = "A skeleton lies crumpled in the corner.";
        CellAttributes[] white = new CellAttributes[line.Length];
        for (int i = 0; i < line.Length; i++) white[i] = CellAttributes.Default;
        h.Feed(line, white);

        Assert.Empty(h.Arrivals);
    }

    // A monster already in the room, re-mentioned in yellow, isn't a fresh arrival.
    [Fact]
    public void CustomSpawnLine_AlreadyPresent_NotReAdded()
    {
        using Harness h = new();
        h.AddMonster(1, "muckworm");
        string line = "A muckworm darts out of the mud!";
        h.Feed(line, h.AttrsWithFg(line, index: 3));   // first sighting → arrival
        h.Feed(line, h.AttrsWithFg(line, index: 3));   // same mob again → no new arrival

        Assert.Single(h.Arrivals);
    }

    // A structured arrival whose name is ALSO yellow is handled by the directional
    // pattern; the colour scan must not double-count it (AnyPatternMatches gate).
    [Fact]
    public void StructuredArrival_NotDoubleHandledByColourScan()
    {
        using Harness h = new();
        h.AddMonster(1, "muckworm");
        string line = "A muckworm oozes into the room from nowhere.";
        h.Feed(line, h.AttrsWithFg(line, index: 3));

        Assert.Single(h.Arrivals);
    }
}
