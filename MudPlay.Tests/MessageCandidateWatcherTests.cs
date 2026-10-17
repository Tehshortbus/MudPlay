using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MessageCandidateWatcherTests
{
    private sealed class Harness
    {
        public LogService Log { get; } = new();
        public MessageRouter Router { get; } = new();
        public MessageStore Messages { get; } = new();
        public MessageCandidateStore Candidates { get; } = new();
        public MessageCandidateWatcher Watcher { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; }

        // A catalogued line that comes well after the line before it: it releases a
        // line held back as possible death flavour, as the next line of the game does.
        public const string LaterKnownLine = "The rain lets up for a moment.";

        // Mutable so a test can point the watcher at a known room before feeding.
        public RoomKey? Room { get; set; }

        // Stand-in for the active set's Rooms-table name index — a test adds a
        // room name here to prove a room-display title line isn't staged.
        public HashSet<string> RoomNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Default in-game so capture tests exercise the real path; a test that
        // needs the pre-game gate passes inGame:false. seedDefaultPatterns loads the
        // real catalog for tests about shapes DefaultPatterns is supposed to cover.
        public Harness(bool inGame = true, bool seedDefaultPatterns = false,
            Func<LineExtractor.EmittedLine, bool>? isRecognizedLine = null,
            Func<string, bool>? isRoomExitCommand = null)
        {
            if (seedDefaultPatterns) DefaultPatterns.Seed(Router);
            Messages.Messages.Add(MakeRecord(LaterKnownLine));
            Server = new ServerFeed(Router);
            Watcher = new MessageCandidateWatcher(
                Router, Messages, Candidates, currentRoom: () => Room, log: Log,
                isKnownRoomName: RoomNames.Contains,
                isRecognizedByDirectParser: PartyManager.IsRosterRow,
                isRecognizedLine: isRecognizedLine,
                isRoomExitCommand: isRoomExitCommand);
            // Attached as a session starts, so before the first in-game prompt.
            Watcher.AttachLineExtractor(Server.Lines);
            if (inGame) Watcher.NotifyInGame();
        }

        // Real capture holds a vetted line back one line so an experience gain can
        // retire it as monster death flavour, so a test asserting on a single fed
        // line lets the game go on (flush) to release it. Tests about the death rule
        // itself pass flush:false and feed the following line themselves.
        public void Feed(string text, DateTimeOffset? when = null, bool flush = true)
        {
            DateTimeOffset at = when ?? DateTimeOffset.UtcNow;
            Server.Line(text, at);
            if (flush) Server.Line(LaterKnownLine, at.AddMinutes(1));
        }

        // The game goes on a while later, releasing whatever line was held.
        public void GoOn() => Server.Line(LaterKnownLine, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static MessageRecord MakeRecord(string casterMessage) => new(
        Id: MessageRecord.ComputeId("Test", casterMessage, "", "", "", ""),
        Name: "Test",
        Flags: MessageFlags.None,
        RawFlagsHex: 0,
        CasterMessage: casterMessage,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: string.Empty,
        AppliedEndsWith: string.Empty);

    [Fact]
    public void KnownMessageLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeRecord("You feel a surge of power!"));

        h.Feed("You feel a surge of power!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void ConfuseFumbleLine_DoesNotCreateCandidate()
    {
        // A recognized fumble line reaches the app via a predicate, not a router
        // pattern — the watcher must still exclude it (indexed from the record's
        // ConfuseFumbleLine slot), or it'd be falsely staged as unrecognized.
        Harness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: MessageRecord.ComputeId("Convulsions", "", "", "", "", ""),
            Name: "Convulsions",
            Flags: MessageFlags.Confused,
            RawFlagsHex: 0,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: string.Empty,
            AppliedEndsWith: string.Empty,
            Links: null,
            ConfuseFumbleLine: "You look around stupidly."));

        h.Feed("You look around stupidly.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void RouterMatchedLine_DoesNotCreateCandidate()
    {
        Harness h = new();
        h.Router.RegisterPattern(new RegexPattern("test.gossip", @"^\*GOSSIP\* "));

        h.Feed("*GOSSIP* Forged: hello");

        Assert.Empty(h.Candidates.Candidates);
    }

    // Report paradigm-20261008-125639: after `exit` the board's menu was staged as
    // unknown game lines. Its banner is drawn above the row that gives the menu
    // away, so those rows are taken back when that row arrives; an older candidate
    // is left alone, and nothing more is staged until the game is entered again.
    [Fact]
    public void LeavingForTheMenu_TakesBackTheBannerStagedJustBefore_AndStagesNothingMore()
    {
        Harness h = new(seedDefaultPatterns: true);
        h.Candidates.RecordSighting("A strange humming fills the air.", DateTimeOffset.UtcNow.AddMinutes(-5), null, null);
        h.Feed("You will exit after a period of silent meditation.");
        h.Feed("...............");   // the wait, counted out in dots
        h.Feed("P A R A D I G M v26.8.30.1 (Aug 30 2026 10:23:10)");
        h.Feed("{ A New Era of MUD }");
        Assert.Equal(3, h.Candidates.Candidates.Count);   // the wait lines are known patterns

        h.Watcher.NotifyLeftForMenu();
        h.Feed("[H] . Help");
        h.Feed("[X] . Exit Game");

        Assert.Equal(new[] { "A strange humming fills the air." }, h.Candidates.Candidates.Select(c => c.RawText).ToArray());
    }

    [Fact]
    public void GenuinelyNewLine_CreatesCandidate_AndWarnsOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void NewLine_TagsCandidateWithCurrentRoom()
    {
        Harness h = new();
        h.Room = new RoomKey(12, 3456);

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Equal(12, c.Map);
        Assert.Equal(3456, c.Room);
    }

    [Fact]
    public void NewLine_WithoutKnownRoom_LeavesLocationNull()
    {
        Harness h = new();   // Room stays null

        h.Feed("A shimmering aura surrounds you!");

        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        Assert.Null(c.Map);
        Assert.Null(c.Room);
    }

    // Report paradigm-20261002-120516: a fortune card's reading runs over two rows,
    // and the second ('perseverance shall be yours."') staged a candidate on every
    // draw. The row that opens a quote is judged as usual; the rows up to the closing
    // quote go with it.
    [Fact]
    public void RowsInsideAnOpenQuote_AreNotStaged()
    {
        Harness h = new();
        h.Feed("\"When the Priest is played, wisdom and insight are paramount. Faith and");
        h.Feed("perseverance shall be yours.\"");
        h.Feed("A line that follows the closed quote.");

        Assert.Equal(
            new[] { "\"When the Priest is played, wisdom and insight are paramount. Faith and", "A line that follows the closed quote." },
            h.Candidates.Candidates.Select(c => c.RawText));
    }

    // The rows of a drawn card reach the parsers now that positioned rows are
    // emitted; the picture itself is not a message.
    [Fact]
    public void RowsOfPaintedArt_AreNotStaged()
    {
        Harness h = new();
        h.Feed(" \u250C\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510\u2584");
        h.Feed(" \u2502\u2588\u2588The\u2588\u2588\u2588\u2502\u2588");
        h.Feed(" \u2502\u2588Knight\u2588\u2502\u2588");
        h.Feed("A line that is not art.");

        Assert.Equal(
            new[] { "A line that is not art." },
            h.Candidates.Candidates.Select(c => c.RawText));
    }

    [Fact]
    public void AnOpenQuote_EndsAtThePrompt()
    {
        Harness h = new();
        h.Feed("The old man mutters \"never mind");
        h.Server.Prompt("[HP=10/MA=5]:");
        h.Feed("Something unrelated happens here.");

        Assert.Contains(h.Candidates.Candidates, c => c.RawText == "Something unrelated happens here.");
    }

    // A quote that opens and closes on one row leaves the next row alone.
    [Fact]
    public void ABalancedQuote_DoesNotSwallowTheNextRow()
    {
        Harness h = new();
        h.Feed("The gypsy says \"Take this with you.\"");
        h.Feed("Another unknown line arrives.");

        Assert.Equal(2, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void RepeatedLine_BumpsOccurrences_WarnsOnlyOnce()
    {
        Harness h = new();
        int warnCount = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnCount++; };

        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal(3, h.Candidates.Candidates[0].Occurrences);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void SimulateCapture_StagesAFreshCandidateEachCall()
    {
        Harness h = new();

        string first = h.Watcher.SimulateCapture();
        string second = h.Watcher.SimulateCapture();

        Assert.NotEqual(first, second);   // varies per call → distinct candidates
        Assert.Equal(2, h.Candidates.Candidates.Count);
        Assert.Contains(h.Candidates.Candidates, c => c.RawText == first);
    }

    [Fact]
    public void SimulateCapture_RespectsDisabledGate()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Watcher.SimulateCapture();

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DismissedLine_IsIgnoredOnRecurrence()
    {
        Harness h = new();
        h.Feed("A shimmering aura surrounds you!");         // stages candidate (occ 1)
        MessageCandidateRecord c = Assert.Single(h.Candidates.Candidates);
        h.Candidates.Dismiss(c.Id);

        int warnAfter = 0;
        h.Log.EntryAdded += e => { if (e.Severity == LogSeverity.Warn) warnAfter++; };
        h.Feed("A shimmering aura surrounds you!");         // recurrence of a dismissed line

        Assert.Single(h.Candidates.Candidates);             // no duplicate
        Assert.Equal(1, h.Candidates.Candidates[0].Occurrences);  // not bumped
        Assert.Equal(0, warnAfter);                         // no re-alert
    }

    [Fact]
    public void DisabledWatcher_NeverCreatesCandidates()
    {
        Harness h = new();
        h.Watcher.Enabled = false;

        h.Feed("Whatever this is, it should be ignored.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BurstOfDistinctUnrecognizedLines_CapsAtBurstLimit()
    {
        // BurstCap = 6, BurstWindow = 1500ms: 10 distinct never-seen lines
        // arriving within the window should stage only the first 6.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
            h.Feed($"Distinct never-seen line #{i}", t0.AddMilliseconds(i * 50));

        Assert.Equal(6, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void BurstAcrossTwoWindows_BothGroupsStageNormally()
    {
        // Two separate bursts of 5 (under the cap), well apart in time, should
        // each stage in full — the window resets rather than accumulating
        // across the gap.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            h.Feed($"Group A line #{i}", t0.AddMilliseconds(i * 50));

        DateTimeOffset t1 = t0.AddSeconds(2);   // past the 1500ms burst window
        for (int i = 0; i < 5; i++)
            h.Feed($"Group B line #{i}", t1.AddMilliseconds(i * 50));

        Assert.Equal(10, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void PreGameLine_IsNotStaged()
    {
        // Before the first in-game prompt (splash / login menu / connect banner),
        // capture holds off entirely.
        Harness h = new(inGame: false);

        h.Feed("Welcome to the BBS! Press ENTER to continue.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void NotifyInGame_OpensCaptureAfterThePreGameGate()
    {
        Harness h = new(inGame: false);
        h.Feed("Login menu junk that should be ignored.");
        Assert.Empty(h.Candidates.Candidates);

        h.Watcher.NotifyInGame();
        h.Feed("A shimmering aura surrounds you!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void KnownRoomName_IsNotStaged()
    {
        // A room-display title line is read directly by the room-display parser and
        // registers no router pattern, so without the Rooms-table exclusion it would
        // stage as unrecognized. A known room name is dropped...
        Harness h = new();
        h.RoomNames.Add("Intersection of Guild St. & River St.");

        h.Feed("Intersection of Guild St. & River St.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void SpellLineSharingRoomNameColour_IsStillStaged()
    {
        // ...but a genuine unrecognized spell/monster line that is NOT a room name
        // still stages, even though it can share the room-name colour on some
        // palettes — the exclusion keys off the Rooms table, never the colour.
        Harness h = new();
        h.RoomNames.Add("River Street");

        h.Feed("The goblin shaman chants a guttural incantation!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ClientStatusLine_IsNotStaged()
    {
        // The client's own bracketed WriteTerminalStatus notices are never server
        // messages, so a full-line "[ … ]" is dropped.
        Harness h = new();

        h.Feed("[The Cleric's Quest is Now Available]");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void EchoedCommand_IsNotStaged()
    {
        // A command the user just sent bounces back as an echo; ObserveOutbound
        // remembers it so the echo isn't staged as an unknown line.
        Harness h = new();
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("eq engraved warhorn\r\n"));

        h.Feed("eq engraved warhorn");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void EchoedCommand_PastEchoWindow_IsStagedAgain()
    {
        // The echo suppression is time-bounded — a line matching an old command
        // long after the fact is treated as a genuine unrecognized line.
        Harness h = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("wave banner\r\n"));

        h.Feed("wave banner", t0.AddSeconds(30));   // well past the 3s echo window

        Assert.Single(h.Candidates.Candidates);
    }

    // ----- Templated catalogue slots -------------------------------------
    // The catalogue stores most messages as templates, which can never string-equal
    // a real line. Comparing them as text meant every templated message in the game
    // read as unrecognized and known casts were staged for review.

    private static MessageRecord MakeTemplateRecord(
        string name, string caster = "", string witness = "",
        string applied = "", string endsWith = "") => new(
            Id: MessageRecord.ComputeId(name, caster, "", witness, applied, endsWith),
            Name: name,
            Flags: MessageFlags.None,
            RawFlagsHex: 0,
            CasterMessage: caster,
            TargetMessage: string.Empty,
            WitnessMessage: witness,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith);

    [Theory]
    [InlineData("Raijin casts minor healing on Raijin!")]
    [InlineData("Raijin casts bless on Suijin!")]
    public void TemplatedWitnessCast_IsNotStaged(string line)
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Minor Healing", witness: "{source} casts {spellname} on {target}!"));

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void TemplatedCasterLine_IsNotStaged()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Way of the Swan", caster: "You invoke the {spellname}."));

        h.Feed("You invoke the way of the swan.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void LiteralAppliedEndsWith_IsMatchedAsSubstring()
    {
        // The stored wording omits the server's trailing punctuation, so an exact
        // comparison never fired and every buff-expiry line looked unrecognized.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "Way of the Tiger",
            applied: "You feel the power of the tiger.",
            endsWith: "The effects of way of the tiger wear off"));

        h.Feed("The effects of way of the tiger wear off!");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void LiteralAppliedMessage_IsMatchedAsSubstring()
    {
        // A gypsy card's record holds only the opening of its reading, which is what
        // ConditionTracker latches the card on. The wire line runs on past it (and
        // opens with a quote), so an exact comparison staged every draw for review.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "card-knight",
            applied: "The Knight is both protector and aggressor",
            endsWith: "The gaze of luck is no longer upon you"));

        h.Feed("\"The Knight is both protector and aggressor. Strength and vigilance shall");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void PlaceholderOnlyTemplate_DoesNotSuppressUnrelatedLines()
    {
        // A template with no literal text compiles to a pattern matching virtually
        // any line. Indexing one would silently disable the whole feature, so it has
        // to be dropped — the stock catalogue really does contain one.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord("Degenerate", witness: "{source} {target}"));

        h.Feed("The gnarled tree groans ominously.");

        Assert.Single(h.Candidates.Candidates);
    }

    // ----- Shapes the client already knows -------------------------------

    [Theory]
    [InlineData("The room is dimly lit")]
    [InlineData("The room is dimly lit.")]
    [InlineData("The room is barely visible")]
    [InlineData("The room is pitch black")]
    [InlineData("The room is very dark - you can't see anything")]
    public void RoomLightAnnouncement_IsNotStaged(string line)
    {
        // Room light is fully derivable from game data, so none of the bands belong
        // in a review queue. "Dimly lit" alone was the noisiest line captured.
        Harness h = new();

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("  Raijin WuzHere                 (Priest)     [M:100%] [H: 85%]   - Backrank")]
    [InlineData("  Suijin WuzHere                 (Witchunter)          [H:100%]   - Midrank")]
    [InlineData("  Fujin WuzHere                  (Mystic)     [K:100%] [H: 94%]   - Frontrank")]
    [InlineData("  Raijin WuzHere                 (Priest)     [Invited]")]
    public void PartyRosterRow_IsNotStaged(string line)
    {
        // `par` output is consumed by PartyManager's stateful block parser, which
        // registers no router pattern — so the whole roster staged on every poll.
        Harness h = new();

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("The wild dog snaps at Suijin with its teeth!")]
    [InlineData("The dark goblin archer shoots an arrow at Suijin with their shortbow!")]
    [InlineData("The nasty bandit swings at Suijin with their broadsword!")]
    [InlineData("Raijin swipes at dark goblin archer!")]
    public void ThirdPartyPhysicalAttack_IsNotStaged(string line)
    {
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void MonsterCastAtAPartyMember_IsStillStaged()
    {
        // The physical-attack patterns must stay narrow enough to let a monster's
        // SPELL through — unrecognized monster spell messages are the whole point of
        // the capture, so a pattern that swallowed one would defeat the feature.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The dark elf priest hurls a searing bolt at Suijin!");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ThirdPartyCastFizzle_IsNotStaged()
    {
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("Raijin attempted to cast minor healing, but failed.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("Your spell has no effect in this room!")]
    [InlineData("short orc warrior retches uncontrollably!")]
    [InlineData("Raijin fumbles about dazedly!")]
    public void RoomSpellRefusalAndWitnessedFumble_AreNotStaged(string line)
    {
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(line);

        Assert.Empty(h.Candidates.Candidates);
    }

    // ----- Monster death flavour ------------------------------------------

    [Theory]
    [InlineData("The dog yelps loudly, and dies.")]
    [InlineData("The dark goblin archer collapses with a spiteful hiss.")]
    public void DeathFlavourBeforeExperienceLine_IsNotStaged(string deathLine)
    {
        // Realms author death messages per species and don't publish them, so no
        // catalogue can hold one. Position identifies them: the line immediately
        // before the experience gain.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(deathLine, flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DeathFlavour_RetiresARowCapturedEarlier()
    {
        // Rows staged before the positional rule existed heal themselves on the next
        // kill rather than sitting in the queue forever.
        Harness h = new(seedDefaultPatterns: true);
        h.Feed("The dog yelps loudly, and dies.");
        Assert.Single(h.Candidates.Candidates);

        h.Feed("The dog yelps loudly, and dies.", flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DeathFlavour_SurvivesABlankLineBeforeTheExperienceGain()
    {
        // Only a substantive server line releases the held candidate. A blank line or
        // the client's own status notice between the two would otherwise let the death
        // message through.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The dog yelps loudly, and dies.", flush: false);
        h.Feed("", flush: false);
        h.Feed("[Something the client printed]", flush: false);
        h.Feed("You gain 350 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void DeathFlavour_SurvivesAWearOffLineBeforeTheExperienceGain()
    {
        // What the monster had cast lifts as it dies, between its death message and
        // the experience line. A line the catalogues know doesn't release the held one.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The nanati lets out a shriek and fades away!", flush: false);
        h.Feed("Your party has been disbanded.", flush: false);   // any recognized line
        h.Feed("You gain 825 experience.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void HeldLine_IsStagedOnceMoreThanACoupleOfRecognizedLinesPass()
    {
        // The allowance is for the line or two a death prints, not for the rest of a
        // fight: an unknown line followed by ordinary traffic is still staged.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The gnarled tree groans ominously.", flush: false);
        h.Feed("Your party has been disbanded.", flush: false);
        h.Feed("Your party has been disbanded.", flush: false);
        Assert.Empty(h.Candidates.Candidates);
        h.Feed("Your party has been disbanded.", flush: false);

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void LineNotFollowedByExperience_IsStagedNormally()
    {
        // The deferral must not swallow ordinary lines — only the one an experience
        // gain directly follows.
        Harness h = new(seedDefaultPatterns: true);

        h.Feed("The gnarled tree groans ominously.", flush: false);
        h.Feed("A cold wind stirs the branches.", flush: false);

        Assert.Single(h.Candidates.Candidates);
        Assert.Equal("The gnarled tree groans ominously.", h.Candidates.Candidates[0].RawText);
    }

    // ----- Room-spell flavour must survive --------------------------------

    [Theory]
    [InlineData("A cold mist coils slowly around your ankles.")]
    [InlineData("Somewhere far off, a raven caws three times.")]
    [InlineData("The cobblestones glisten under a sudden squall.")]
    public void RoomSpellFlavour_NotYetInCatalogue_IsStillStaged(string line)
    {
        // Scenery-like room-spell triggers the catalogue doesn't have an entry for —
        // surfacing them is exactly what the capture is for, so none of the exclusions
        // may touch a line with no catalogue entry. (The silvermere / darkwood sets are
        // now seeded, so their wordings are recognized instead — see the multi-witness
        // test below; these stand-ins keep the "unknown flavor still surfaces" invariant.)
        Harness h = new(seedDefaultPatterns: true);

        h.Feed(line);

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ColourAwareRecognizedLine_IsNotStaged()
    {
        // The colour-aware exclusion (BBS action / emote, told apart by all-green colour
        // plus a roster check in real life) drops the line before it's staged. Here the
        // delegate stands in for that recognizer.
        Harness h = new(isRecognizedLine: line => line.Text == "Fujin waves happily.");

        h.Feed("Fujin waves happily.");

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BroadcastChannelList_HeaderAndMembers_NotStaged()
    {
        // `br` prints a header then the channel members (one per line or comma-separated).
        // Header + member rows are all suppressed.
        Harness h = new();

        h.Feed("The following users are on channel 34433:", flush: false);
        h.Feed("Fujin", flush: false);
        h.Feed("Raijin, Suijin", flush: false);

        h.GoOn();

        Assert.Empty(h.Candidates.Candidates);
    }

    [Fact]
    public void BareName_WithoutChannelHeader_IsStillStaged()
    {
        // A bare capitalized name is suppressed ONLY right after the channel header — on
        // its own it's a genuine unknown that must surface.
        Harness h = new();

        h.Feed("Grimlock");

        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void BroadcastChannelList_GateClearsAfterANonMemberLine()
    {
        // The member gate is one-shot per header: a line that isn't a member row clears
        // it (and is itself staged normally), so a later bare name isn't wrongly dropped.
        Harness h = new();

        h.Feed("The following users are on channel 34433:", flush: false);   // gate on
        h.Feed("Fujin", flush: false);                                        // member — suppressed
        h.Feed("The gnarled tree groans ominously.", flush: false);           // non-member → gate off, staged
        h.Feed("Grimlock", flush: false);                                     // bare name, gate cleared → staged

        h.GoOn();

        Assert.Equal(2, h.Candidates.Candidates.Count);
    }

    [Fact]
    public void MultiLineWitness_EveryWording_IsRecognized()
    {
        // A room spell fires SEVERAL ambient flavor lines from ONE record; WitnessMessage
        // holds them newline-separated. Every wording must be recognized (not staged),
        // while an unrelated line still surfaces — this is what lets the seeded silvermere
        // / darkwood sets drop out of the report.
        Harness h = new();
        h.Messages.Messages.Add(MakeTemplateRecord(
            "darkwood forest spell",
            witness: "A dry twig snaps loudly behind you.\n"
                   + "An ominous wind blows through the trees.\n"
                   + "The forest becomes strangely silent."));

        h.Feed("A dry twig snaps loudly behind you.");
        h.Feed("An ominous wind blows through the trees.");
        h.Feed("The forest becomes strangely silent.");
        Assert.Empty(h.Candidates.Candidates);

        h.Feed("A wholly different, uncatalogued line appears here.");
        Assert.Single(h.Candidates.Candidates);
    }

    // ----- listings, wrapped room rows, own-action replies ------------------

    [Fact]
    public void ShopListing_SkippedFromHeaderToPrompt()
    {
        Harness h = new();
        h.Feed("Item                          Quantity    Price", flush: false);
        h.Feed("------------------------------------------------------", flush: false);
        h.Feed("crystal club                  10        1375 gold crowns", flush: false);
        h.Feed("a row no shape could describe", flush: false);
        h.GoOn();
        Assert.Empty(h.Candidates.Candidates);

        h.Server.Prompt("[HP=91/MA=42]:");
        h.Feed("A shimmering rune flickers.", flush: false);
        h.GoOn();
        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void RoomDisplay_DescriptionSkippedUpToExitsLine()
    {
        Harness h = new();
        h.RoomNames.Add("Crumbling Tunnel");
        h.Feed("Crumbling Tunnel", flush: false);
        h.Feed("Mud and trodden grass form the packed surface of this winding path. It", flush: false);
        h.Feed("Obvious exits: northwest, southeast", flush: false);
        h.GoOn();
        Assert.Empty(h.Candidates.Candidates);

        h.Feed("A shimmering rune flickers.", flush: false);
        h.GoOn();
        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void WrappedAlsoHere_ContinuationRowsSkipped()
    {
        Harness h = new();
        h.Feed("Also here: fat half-orc sentry, thin half-orc", flush: false);
        h.Feed("warrior, large", flush: false);
        h.Feed("guardsman.", flush: false);
        h.GoOn();
        Assert.Empty(h.Candidates.Candidates);

        h.Feed("A shimmering rune flickers.", flush: false);
        h.GoOn();
        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void ListingBlock_EndsOnAPauseWhenNoPromptIsRead()
    {
        Harness h = new();
        DateTimeOffset t = DateTimeOffset.UtcNow;
        h.Feed("Top Heroes of the Realm By Experience", t, flush: false);
        h.Feed("some row", t.AddMilliseconds(50), flush: false);
        h.Feed("A shimmering rune flickers.", t.AddSeconds(5), flush: false);
        h.GoOn();
        Assert.Single(h.Candidates.Candidates);
    }

    [Fact]
    public void RoomCommandReply_LeadingWithTheVerbJustSent_Skipped()
    {
        Harness h = new();
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("pull lever\r\n"));
        h.Feed("You pull the large iron lever.");
        Assert.Empty(h.Candidates.Candidates);

        // No such command sent: the same shape is a candidate.
        h.Feed("You push the button.");
        Assert.Single(h.Candidates.Candidates);
    }

    // A named exit's passage line shares no word with its command; the room's own
    // exit commands tie the two together.
    [Fact]
    public void NamedExitFlavour_AfterTheRoomsExitCommand_Skipped()
    {
        Harness h = new(isRoomExitCommand: c => c is "go manhole" or "go man" or "enter manhole");
        h.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("go manhole\r\n"));
        h.Feed("You pull open the manhole cover, and slip inside the hole.");
        Assert.Empty(h.Candidates.Candidates);

        // Any other command leaves a "You …" line a candidate.
        Harness other = new(isRoomExitCommand: c => c is "go manhole");
        other.Watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("fbal orc\r\n"));
        other.Feed("You hurl a ball of flame at the orc!");
        Assert.Single(other.Candidates.Candidates);
    }

    [Fact]
    public void LeftGame_HoldsCaptureUntilTheNextInGamePrompt()
    {
        Harness h = new();
        h.Watcher.NotifyLeftGame();
        h.Feed("Welcome to the official Paradigm server!");
        Assert.Empty(h.Candidates.Candidates);

        h.Watcher.NotifyInGame();
        h.Feed("A shimmering rune flickers.");
        Assert.Single(h.Candidates.Candidates);
    }

    [Theory]
    [InlineData("Player ID:           228")]
    [InlineData("Broadcast Channel:   6969")]
    [InlineData("HP Regen:   3/9        AC vs Evil:  31           Cold Resist:     0")]
    [InlineData("vs Good:     31           Dodge:          16")]
    [InlineData("Attacks:")]
    [InlineData("Type        Swings   Accy   Min   Max   QnD(Total)   Avg/Rnd(+xtra)")]
    [InlineData("Attack       2.924     80    10    21     0(11)           60")]
    [InlineData("mmis             2     15    15    24                     38")]
    [InlineData("crystal sword                 10        1375 gold crowns (You can't use)")]
    [InlineData("1. Rough                 Paladin    Prestige Worldwide  13195000000")]
    [InlineData("Slee Stak                     27 Kang Cleric            - Online [Leader]")]
    [InlineData("Porksoda just joined your channel (6969)")]
    [InlineData("Your balance at Bank of Godfrey is:")]
    [InlineData("On deposit: 33107 copper farthings [331.07 gold crowns]")]
    [InlineData("You deposit 58271 copper farthings.")]
    [InlineData("You withdrew 132 copper farthings.")]
    [InlineData("You are now resting.")]
    [InlineData("Welcome to level 10!")]
    [InlineData("You gain 10 CPs")]
    [InlineData("But, due to a miracle, you have been saved.")]
    [InlineData("You have recovered the corpse of Raijin.")]
    [InlineData("tall orc shaman attempts to cast harm on you, but fails.")]
    [InlineData("big orc shaman attempts to cast harm on you, but you resist!")]
    [InlineData("A new day has come!")]
    [InlineData("TarlChain(210)             0")]
    [InlineData("-=-=-=-=-=-=-=-=-=-=-=-")]
    [InlineData("[HP=91/91 MA=11/42] :w")]
    public void StandardEngineOutput_IsBenign(string line)
    {
        Assert.True(BenignChatterMatcher.IsBenign(line));
    }

    [Theory]
    [InlineData("A shimmering rune flickers and fades.")]
    [InlineData("You feel a surge of power!")]
    [InlineData("The fanatic screams \"Death to those who oppose the Blood God!\"")]
    [InlineData("Raijin casts minor healing on Raijin!")]
    public void SpellShapedLines_AreNotBenign(string line)
    {
        Assert.False(BenignChatterMatcher.IsBenign(line));
    }

    [Fact]
    public void PruneIfRecognized_DropsNowKnownLines_KeepsTheRest()
    {
        Harness h = new(seedDefaultPatterns: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        h.Candidates.RecordSighting("You deposit 20420 copper farthings.", now, null, null);
        h.Candidates.RecordSighting("A shade materializes in the room.", now, null, null);
        h.Candidates.RecordSighting("A shimmering rune flickers and fades.", now, null, null);

        int pruned = h.Candidates.Candidates.ToList().Count(h.Watcher.PruneIfRecognized);

        Assert.Equal(2, pruned);
        Assert.Equal("A shimmering rune flickers and fades.", Assert.Single(h.Candidates.Candidates).RawText);
    }
}
