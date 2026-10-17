using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class RoomDisplayParserTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _root;

    public RoomDisplayParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomdisplay-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 297, "Name": "Bank of Godfrey",
            "Light": 0, "Shop": 8, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "1/1", "W": "1/41 (Door [1000 picklocks/strength])",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 5, "Name": "Cellar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "2/4", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, RoomDisplayParser Parser) NewParser(string json = GraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        RoomDisplayParser parser = new(_feed.Lines, tracker);
        return (tracker, parser);
    }

    // Same wiring, but hands back the emulator so a test can replay raw wire
    // bytes through the whole terminal -> line -> parser path.
    private (RoomTracker Tracker, RoomDisplayParser Parser, TerminalEmulator Terminal) NewParserWithTerminal(
        string json = GraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        TerminalEmulator term = new(80, 25);
        LineExtractor lines = new(term);
        RoomDisplayParser parser = new(lines, tracker);
        return (tracker, parser, term);
    }

    // ----- happy paths ----------------------------------------------

    [Fact]
    public void CompactRoom_NameThenExits_RegistersObservation()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Town Gates",
            "Obvious exits: north."
        });

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void VerboseRoom_NameThenDescriptionThenExits_RegistersObservation()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Town Gates",
            "You are standing at the south gate of the town. The road",
            "continues north into the heart of town.",
            "Obvious exits: north."
        });

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void MultipleExits_ParsedAsSet()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();
        List<RoomObservation> parsed = new();
        parser.RoomParsed += parsed.Add;

        _feed.Rows(new[]
        {
            "Town Gates",
            "Obvious exits: north, south, east and west."
        });

        RoomObservation obs = Assert.Single(parsed);
        Assert.True(obs.Exits.SetEquals(new[] { Direction.N, Direction.S, Direction.E, Direction.W }));
        // Town Gates has only N in the graph, so no candidate matches the exits.
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    [Fact]
    public void ObviousExitsNone_EmitsEmptyExitSet()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Town Gates",
            "Obvious exits: none."
        });

        // The parser must turn "Obvious exits: none." into an empty exit set —
        // the mirrored ObservedExitDirections is the direct proof.
        Assert.NotNull(tracker.State.ObservedExitDirections);
        Assert.Empty(tracker.State.ObservedExitDirections!);

        // Town Gates has N in the graph, so the exact (Name, ExitMask) search
        // misses on exits={}. But the name is unique in the graph and {} is a
        // subset of every room's exits, so the door-tolerant re-anchor latches
        // the name-unique room at 1/1 rather than freezing at Lost.
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void UpDownExits_RegisterAsVerticalDirections()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // Cellar (2/5) has only U.
        _feed.Rows(new[]
        {
            "Cellar",
            "Obvious exits: up."
        });

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(2, 5), tracker.State.CurrentRoom!.Key);
    }

    // ----- block boundary semantics ---------------------------------

    [Fact]
    public void MovementTransition_BetweenRooms_ResetsNameSearch()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // The first room's lines, then a movement transition, then a
        // new room display. The transition line acts as a block
        // boundary so the second room's name is correctly picked.
        _feed.Rows(new[]
        {
            "Town Gates",
            "Obvious exits: north."
        });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        _feed.Rows(new[]
        {
            "You walk north.",
            "North Square",
            "Obvious exits: south."
        });
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void BlankLine_BeforeRoomName_IsHandledAsBoundary()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // Combat ending + blank + new room display.
        _feed.Rows(new[]
        {
            "You strike the goblin for 12 damage.",
            "The goblin dies.",
            "",
            "Town Gates",
            "Obvious exits: north."
        });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void NoRoomNameRecoverable_DropsObservation()
    {
        // Only a movement-transition line before the exits anchor —
        // can't recover a name. No observation should be made.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "You walk north.",
            "Obvious exits: south."
        });
        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
    }

    // ----- direction parsing edge cases -----------------------------

    [Theory]
    [InlineData("n, e, s, w",                   new[] { Direction.N, Direction.E, Direction.S, Direction.W })]
    [InlineData("ne, nw, se, sw",               new[] { Direction.NE, Direction.NW, Direction.SE, Direction.SW })]
    [InlineData("north, south",                 new[] { Direction.N, Direction.S })]
    [InlineData("up and down",                  new[] { Direction.U, Direction.D })]
    [InlineData("North, South, East and West",  new[] { Direction.N, Direction.S, Direction.E, Direction.W })]
    public void ExitListParses_VariousFormats(string list, Direction[] expected)
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();
        List<RoomObservation> parsed = new();
        parser.RoomParsed += parsed.Add;
        _feed.Rows(new[]
        {
            "Some Room Name",
            $"Obvious exits: {list}."
        });

        Assert.True(Assert.Single(parsed).Exits.SetEquals(expected));
        // The room isn't in the graph → Lost.
        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
    }

    // ----- command-echo filter (the original bug) ------------------

    [Fact]
    public void CommandEcho_SingleLetterDirection_DoesNotBecomeRoomName()
    {
        // The bug: player types "e", terminal echoes "e", next room
        // arrives. Buffer is ["e", "Newhaven, Narrow Road"]. The old
        // text heuristic picked "e" as the name. The new echo filter
        // must skip "e" and pick "Newhaven, Narrow Road".
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Town Gates",
            "Obvious exits: north."
        });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        _feed.Rows(new[]
        {
            "n",
            "North Square",
            "Obvious exits: south."
        });
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Theory]
    [InlineData("look n")]
    [InlineData("l e")]
    [InlineData("sea sword")]
    [InlineData("exa torch")]
    public void CommandEcho_VerbWithArg_DoesNotBecomeRoomName(string echo)
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            echo,
            "Town Gates",
            "Obvious exits: north."
        });

        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void CommandEcho_BareDirectionWord_DoesNotBecomeRoomName()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "north",
            "Town Gates",
            "Obvious exits: north."
        });

        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- party-list (`par`) output must never become a room name -----

    [Fact]
    public void ParOutput_BeforeDraggedRoom_DoesNotBecomeRoomName()
    {
        // The follower's party tracking polls `par` constantly. Its output —
        // "You are following <leader>." + roster — lands in the buffer right
        // before the leader-drag line and the room the follower is pulled into.
        // Without the party-chatter boundary the name search reaches back and
        // grabs "You are following MudPlay." as the room name, knocking the
        // tracker to Suspect. The boundary must isolate the real room title.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[] { "Town Gates", "Obvious exits: north." });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        _feed.Rows(new[]
        {
            "You are following MudPlay.",
            "The following people are in your travel party:",
            "  MudPlay WuzHere                  (Mystic)     [K: 60%] [H:100%]   - Frontrank",
            "  Raijin WuzHere                 (Priest)     [M: 94%] [H:100%]   - Backrank",
            " -- Following your Party leader north --",
            "North Square",
            "Obvious exits: south.",
        });

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void FollowLeaderDragLine_ActsAsBlockBoundary()
    {
        // Even with no `par` output, the leader-drag line alone must isolate
        // the dragged room's title from whatever combat/chatter preceded it.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "The dark goblin archer collapses with a spiteful hiss.",
            "You gain 40 experience.",
            "MudPlay just left to the north.",
            " -- Following your Party leader north --",
            "North Square",
            "Obvious exits: south.",
        });

        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void PromptLikeLine_AsBoundary_IsolatesNextRoom()
    {
        // A stray "[HP=..." that slipped past LineExtractor's split
        // should still act as a block boundary so the next room's name
        // is picked correctly.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "You strike the goblin for 12 damage.",
            "[HP=80/MA=20]: ",
            "Town Gates",
            "Obvious exits: north."
        });

        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void PromptLine_ClearsBuffer_MonsterLookHeaderNotGrabbedAsRoomName()
    {
        // 205936: `lo ar` examines a monster; its first line is the monster's
        // name ("dark goblin archer"). The real prompt that ends the look
        // output has IsPromptLine set, so LineExtractor never routes it to the
        // buffer — but it must still clear the buffer, otherwise the monster
        // header survives into the next block and gets picked as the room name.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // Establish a confirmed starting room first.
        _feed.Rows(new[] { "Town Gates", "Obvious exits: north." });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        _feed.Rows(new[]
        {
            "dark goblin archer",
            "It appears to be a vile creature of the dark.",
            "[HP=72/KAI=5]: ",
            "North Square",
            "Obvious exits: south.",
        });

        // The prompt cleared the monster-look header out of the buffer, so the
        // room display resolves to the real room, not the archer's name.
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // ----- colour-anchored detection --------------------------------

    // ----- bank lobby: a blank line INSIDE the room display ----------

    // Captured off a live 1.11p board (Bank of Godfrey, 1/297, 2026-08-20):
    // the bank lobby prints its currency-conversion table as part of the room
    // display, separated from the description by a BLANK line, and the table
    // is dim cyan (SGR 0;36) rather than the bright cyan of a room name. The
    // blank line is a block boundary for name recovery, so scanning forward
    // from it finds no bright-cyan line and the text fallback returns "The
    // currency conversion rates are:" — a name no room has. The tracker can't
    // match it, keeps its previous room (map goes stale), and a walker mid-step
    // never sees the arrival: no auto-deposit, no resumed loop.
    [Fact]
    public void BankLobby_TableAfterBlankLineInDisplay_StillRecoversRoomName()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Bank of Godfrey", BrightCyanSgr1Then36),
            ColoredLine("    This is the town bank. It is lightly crowded with customers come to", DefaultAttr),
            ColoredLine("withdraw or deposit their hard-earned cash. There is a row of bank tellers", DefaultAttr),
            ColoredLine("along the western wall, and you can see a large iron gate with triple locks", DefaultAttr),
            ColoredLine("leading into the vault. ", DefaultAttr),
            ColoredLine("", DimCyan),
            ColoredLine("The currency conversion rates are:", DimCyan),
            ColoredLine("100 platinum pieces == 1 runic coins", DimCyan),
            ColoredLine("100 gold crowns == 1 platinum piece", DimCyan),
            ColoredLine("10 silver nobles == 1 gold crown", DimCyan),
            ColoredLine("10 copper farthings == 1 silver noble", DimCyan),
            ColoredLine("Also here: nasty elite guardsman.", DefaultAttr),
            ColoredLine("Obvious exits: north, east, closed gate west", DefaultAttr),
        }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 297), tracker.State.CurrentRoom!.Key);
    }

    // The same lobby with one more line in the display — a second occupant, an
    // item on the floor, or an "Also here:" list long enough to wrap. The
    // capture above already fills the rolling line buffer exactly, so ONE extra
    // line evicts the room-name line before "Obvious exits:" arrives and no
    // colour pass can find what is no longer buffered. The buffer has to be
    // deep enough to hold the longest real room display, not just this one.
    [Fact]
    public void BankLobby_ExtraOccupantLine_StillRecoversRoomName()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Bank of Godfrey", BrightCyanSgr1Then36),
            ColoredLine("    This is the town bank. It is lightly crowded with customers come to", DefaultAttr),
            ColoredLine("withdraw or deposit their hard-earned cash. There is a row of bank tellers", DefaultAttr),
            ColoredLine("along the western wall, and you can see a large iron gate with triple locks", DefaultAttr),
            ColoredLine("leading into the vault. ", DefaultAttr),
            ColoredLine("", DimCyan),
            ColoredLine("The currency conversion rates are:", DimCyan),
            ColoredLine("100 platinum pieces == 1 runic coins", DimCyan),
            ColoredLine("100 gold crowns == 1 platinum piece", DimCyan),
            ColoredLine("10 silver nobles == 1 gold crown", DimCyan),
            ColoredLine("10 copper farthings == 1 silver noble", DimCyan),
            ColoredLine("You notice a torch here.", DefaultAttr),
            ColoredLine("Also here: nasty elite guardsman, Salad.", DefaultAttr),
            ColoredLine("Obvious exits: north, east, closed gate west", DefaultAttr),
        }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 297), tracker.State.CurrentRoom!.Key);
    }

    // End-to-end replay of the real thing: the byte-for-byte wire capture of a
    // `w` into the Bank of Godfrey (1/297) taken off a live MajorMUD v1.11p-WG
    // board on 2026-08-20, fed through the actual TerminalEmulator and
    // LineExtractor rather than hand-built lines. This is what proves the fix on
    // the path the client really runs: the SGR parsing that marks the name
    // bright cyan (ESC[1;36m) and the table dim cyan (ESC[0;36m), the blank line
    // the lobby emits before its currency table, the backspace-overstruck exit
    // hotkeys ("nL\borth"), and the "closed gate west" barrier phrasing.
    [Fact]
    public void BankLobby_RawWireCapture_LandsInTheBank()
    {
        (RoomTracker tracker, RoomDisplayParser parser, TerminalEmulator term) = NewParserWithTerminal();
        RoomObservation? observed = null;
        parser.RoomParsed += o => observed = o;

        term.Feed(Convert.FromHexString(BankArrivalCaptureHex));

        Assert.NotNull(observed);
        Assert.Equal("Bank of Godfrey", observed!.Value.Name);
        Assert.Equal(
            new[] { Direction.N, Direction.E, Direction.W }.OrderBy(d => d),
            observed.Value.Exits.OrderBy(d => d));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 297), tracker.State.CurrentRoom!.Key);
    }

    // Guard on the across-blank reach-back: it must stop at the nearest HARD
    // boundary (a movement transition here) and never reach past it for a stale
    // cyan name from the PRIOR room. Buffer:
    //   [Cellar cyan] [You walk north.] [Town Gates cyan] [blank] [dim table]
    // The near block (after the blank) has no bright cyan, so the reach-back
    // fires — but it may only walk back to just after "You walk north.", landing
    // on "Town Gates". If it ignored the hard boundary it would grab "Cellar"
    // (a real room, but the wrong one), whose exits don't match {N} → the tracker
    // would miss 1/1. So this fails loudly if the reach-back ever becomes
    // unbounded.
    [Fact]
    public void AcrossBlankReachBack_StopsAtHardBoundary_IgnoresStalePriorName()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Cellar", BrightCyanSgr1Then36),          // stale prior room name (before the boundary)
            ColoredLine("You walk north.", DefaultAttr),          // HARD boundary — the real block start
            ColoredLine("Town Gates", BrightCyanSgr1Then36),      // current room name, before the in-display blank
            ColoredLine("", DimCyan),                             // blank inside the display
            ColoredLine("The currency conversion rates are:", DimCyan),  // near block: no bright cyan
            ColoredLine("Obvious exits: north.", DefaultAttr),
        }));

        // Recovered "Town Gates" (after the boundary), not "Cellar" (before it).
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    private const string BankArrivalCaptureHex =
        "770d0a1b5b303b33373b34306d1b5b3739441b5b4b1b5b313b33366d42616e6b206f6620476f64667265790d0a1b5b37"
        + "39441b5b4b1b5b303b33373b34306d20202020546869732069732074686520746f776e2062616e6b2e20497420697320"
        + "6c696768746c792063726f77646564207769746820637573746f6d65727320636f6d6520746f0d0a7769746864726177"
        + "206f72206465706f73697420746865697220686172642d6561726e656420636173682e20546865726520697320612072"
        + "6f77206f662062616e6b2074656c6c6572730d0a616c6f6e6720746865207765737465726e2077616c6c2c20616e6420"
        + "796f752063616e207365652061206c617267652069726f6e2067617465207769746820747269706c65206c6f636b730d"
        + "0a6c656164696e6720696e746f20746865207661756c742e200d0a1b5b3739441b5b4b1b5b303b33366d0d0a54686520"
        + "63757272656e637920636f6e76657273696f6e207261746573206172653a0d0a31303020706c6174696e756d20706965"
        + "636573203d3d20312072756e696320636f696e730d0a31303020676f6c642063726f776e73203d3d203120706c617469"
        + "6e756d2070696563650d0a31302073696c766572206e6f626c6573203d3d203120676f6c642063726f776e0d0a313020"
        + "636f70706572206661727468696e6773203d3d20312073696c766572206e6f626c650d0a1b5b303b33356d416c736f20"
        + "686572653a201b5b303b33376d6e6173747920656c697465206775617264736d616e1b5b306d1b5b303b33356d2e0d0a"
        + "1b5b306d1b5b303b33326d4f6276696f75732065786974733a206e4c086f7274682c206552086173742c20636c6f7365"
        + "64206761746520776573740d0a1b5b3739441b5b4b1b5b303b33376d5b48503d33331b5b303b33376d5d3a01";

    // One row as the game sends it in one colour: the SGR escape, the text, a reset.
    private static string ColoredLine(string text, string sgr) => $"\u001b[{sgr}m{text}\u001b[0m\r\n";

    private const string BrightCyanSgr96 = "96";

    private const string BrightCyanSgr1Then36 = "1;36";

    // SGR 0;36 — the bank's currency table. Index 6 WITHOUT bold, so it is
    // deliberately NOT bright cyan and must not be mistaken for a room name.
    private const string DimCyan = "0;36";

    private const string DefaultAttr = "0";

    [Fact]
    public void ColorAnchor_BrightCyan_PicksRoomNameOverPriorLines()
    {
        // Two non-blank lines with no boundary between them. Text
        // fallback would pick the first ("Some narrative."). The
        // colour-anchored pass should prefer the second because it's
        // bright cyan.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Some narrative line that isn't the room name.", DefaultAttr),
            ColoredLine("Town Gates", BrightCyanSgr96),
            ColoredLine("Obvious exits: north.", DefaultAttr),
        }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void ColorAnchor_BrightCyanAbilityBeforeTitle_PicksNearestRoomTitle()
    {
        // Regression (paradigm-20260829-154032): another player entered while
        // our move was landing, then emitted a bright-cyan ability line before
        // the server printed the real bright-cyan room title. Retaining the
        // FIRST cyan line produced the impossible observation
        // "Astro invokes the way of the monkey!" + the room's exits, knocked
        // the tracker to Suspect, and caused the paused loop to retry its
        // already-completed move into a wall. The title nearest the exits wins.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Astro walks into the room from the southeast.", DefaultAttr),
            ColoredLine("Astro invokes the way of the monkey!", BrightCyanSgr1Then36),
            ColoredLine("A leafling walks into the area.", DefaultAttr),
            ColoredLine("Town Gates", BrightCyanSgr1Then36),
            ColoredLine("Also here: leafling.", DefaultAttr),
            ColoredLine("Obvious exits: north.", DefaultAttr),
        }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void ColorAnchor_CrowdedRoom_TitleSurvivesRollingBufferEviction()
    {
        // Live GH rooms can list hundreds of wrapped floor items. The title is
        // long gone from the 12-line rolling buffer when the exits line arrives.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();
        var lines = new List<string>
        {
            ColoredLine("Town Gates", BrightCyanSgr96),
        };
        for (int i = 0; i < 30; i++)
            lines.Add(ColoredLine($"wrapped floor item row {i}", DefaultAttr));
        lines.Add(ColoredLine("Obvious exits: north.", DefaultAttr));

        _feed.Raw(string.Concat(lines));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // ----- open-door modifier capture (commit 8 fix) -----------------

    [Fact]
    public void OpenDoorModifier_OnSouth_CapturesOpenDoorDirections()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Silvermere Residence",
            "Obvious exits: open door south, up."
        });

        // The exit set still includes S (the direction is real); the
        // OpenDoorDirections set carries it separately so the walker
        // can skip the door FSM.
        Assert.NotNull(tracker.State.OpenDoorDirections);
        Assert.Contains(Direction.S, tracker.State.OpenDoorDirections!);
        Assert.DoesNotContain(Direction.U, tracker.State.OpenDoorDirections!);
    }

    [Fact]
    public void ClosedDoorModifier_OnNorth_ParsesDirectionButNotAsOpen()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Crown Street",
            "Obvious exits: closed door north, east, west."
        });

        // Direction parsed; closed door doesn't make the open set.
        Assert.Null(tracker.State.OpenDoorDirections);
    }

    // The inner-gate portcullis on some realms renders as "gate" rather than
    // "door" — exact wording captured live at 1/1331 on Paradigm. A gate is a
    // door-type barrier, so its open/closed state must feed OpenDoorDirections
    // just like a door's, or the walker never learns the gate is already open.
    [Fact]
    public void OpenGateModifier_OnNorth_CapturesOpenDoorDirections()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Inner Gate",
            "Obvious exits: open gate north, south, east, west."
        });

        Assert.NotNull(tracker.State.OpenDoorDirections);
        Assert.Contains(Direction.N, tracker.State.OpenDoorDirections!);
        Assert.DoesNotContain(Direction.S, tracker.State.OpenDoorDirections!);
    }

    [Fact]
    public void ClosedGateModifier_OnNorth_ParsesDirectionButNotAsOpen()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Rows(new[]
        {
            "Inner Gate",
            "Obvious exits: closed gate north, south, east, west."
        });

        // Closed gate: N is still a real exit but not in the open set.
        Assert.Null(tracker.State.OpenDoorDirections);
    }

    // A mixed line — "closed door north, open gate south, open door west" — splits
    // the barriers by state: the closed one feeds ClosedDoorDirections (so the
    // asylum solver knows to open it before peeking through), the open ones feed
    // OpenDoorDirections. Both door- and gate-nouns count the same.
    [Fact]
    public void ClosedBarrier_CapturedInClosedDoorDirections_AlongsideOpenOnes()
    {
        (_, RoomDisplayParser parser) = NewParser();
        RoomObservation? captured = null;
        parser.RoomParsed += o => captured = o;

        _feed.Rows(new[]
        {
            "Abandoned Asylum",
            "Obvious exits: closed door north, open gate south, open door west."
        });

        Assert.NotNull(captured);
        RoomObservation obs = captured!.Value;
        Assert.Contains(Direction.N, obs.Exits);   // still a real exit
        Assert.NotNull(obs.ClosedDoorDirections);
        Assert.Contains(Direction.N, obs.ClosedDoorDirections!);
        Assert.DoesNotContain(Direction.S, obs.ClosedDoorDirections!);
        Assert.NotNull(obs.OpenDoorDirections);
        Assert.Contains(Direction.S, obs.OpenDoorDirections!);
        Assert.Contains(Direction.W, obs.OpenDoorDirections!);
    }

    [Fact]
    public void ColorAnchor_BoldCyan_AlsoQualifiesAsBrightCyan()
    {
        // SGR 1;36 → palette index 6 + Bold flag. Same visual as SGR 96.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        _feed.Raw(string.Concat(new[]
        {
            ColoredLine("Pre-room narrative.", DefaultAttr),
            ColoredLine("Town Gates", BrightCyanSgr1Then36),
            ColoredLine("Obvious exits: north.", DefaultAttr),
        }));

        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }
}
