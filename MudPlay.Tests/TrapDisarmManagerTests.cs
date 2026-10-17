using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// State-machine + queue coverage for TrapDisarmManager. The handler-
// side authorisation + channel-aware denial live in TrapHandlerTests;
// these tests drive the manager directly via Enqueue + simulated
// inbound game messages.
public sealed class TrapDisarmManagerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public TrapDisarmManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-trapdisarm-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup — leaves nothing but a temp dir if it fails */ }
    }

    // Build a cache over an isolated set. Seed rows via classesJson / racesJson
    // (null → that table absent). Passing both null leaves no active set, so every
    // FindRowByName reads null — what the state-machine tests want, where the
    // class/race inference must contribute nothing.
    private GameDataCache Cache(string? classesJson = null, string? racesJson = null)
    {
        GameDataCache cache = new(_root);
        if (classesJson is null && racesJson is null) return cache;
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        if (classesJson is not null) File.WriteAllText(Path.Combine(dir, "Classes.json"), classesJson);
        if (racesJson is not null) File.WriteAllText(Path.Combine(dir, "Races.json"), racesJson);
        cache.SwitchSet("set");
        return cache;
    }

    private (TrapDisarmManager mgr, MessageRouter router, PlayerStats stats, List<byte[]> wire) Setup(
        int traps = 50, GameDataCache? cache = null, string race = "", string @class = "")
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerStats stats = new() { Traps = traps, Race = race, Class = @class };
        TrapDisarmManager mgr = new(router, stats, cache ?? Cache());
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        return (mgr, router, stats, wire);
    }

    private static void Dispatch(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(text, new CellAttributes[text.Length], Now, IsPromptLine: false));

    // ===== Disarm skill + odds (GAME_MECHANICS "Exit traps — search and disarm") =====

    [Theory]
    [InlineData(71,  71, 10, 19)]
    [InlineData(0,    0, 10, 90)]
    [InlineData(95,  95,  5,  0)]    // above 90 the trap never springs
    [InlineData(120, 100, 0,  0)]
    public void DisarmOdds_SplitOneRollIntoDisarmSafeMissAndSprings(int skill, int disarm, int safe, int springs)
    {
        TrapDisarmOdds odds = TrapDisarmOdds.For(skill);
        Assert.Equal((disarm, safe, springs), (odds.Disarm, odds.SafeMiss, odds.Springs));
    }

    [Fact]
    public void DisarmSkill_IsTheShownTrapsLessFindGearPlusDisarmGear()
    {
        var (mgr, _, _, _) = Setup(traps: 71);
        Assert.Equal(71, mgr.DisarmSkill);                   // no gear wired: the Traps shown

        mgr.SetWornTrapBonuses(() => (5, 0));               // a thief's kit: +5 find only
        Assert.Equal(66, mgr.DisarmSkill);
        Assert.Equal("disarm ~66%, failure (no dmg) 10%, failure (dmg) 24%", mgr.DisarmOdds!.Value.Summary);

        mgr.SetWornTrapBonuses(() => (0, 8));               // +8 DisarmTraps
        Assert.Equal(79, mgr.DisarmSkill);
    }

    [Fact]
    public void DisarmSkill_IsNull_WithoutAReadTrapsSkill()
    {
        var (mgr, _, _, _) = Setup(traps: 0);
        Assert.Null(mgr.DisarmSkill);
        Assert.Null(mgr.DisarmOdds);
    }

    // ===== Direction normalisation =====

    [Theory]
    [InlineData("n",         "n")]
    [InlineData("north",     "n")]
    [InlineData("NORTH",     "n")]
    [InlineData("s",         "s")]
    [InlineData("south",     "s")]
    [InlineData("e",         "e")]
    [InlineData("east",      "e")]
    [InlineData("w",         "w")]
    [InlineData("west",      "w")]
    [InlineData("ne",        "ne")]
    [InlineData("northeast", "ne")]
    [InlineData("nw",        "nw")]
    [InlineData("northwest", "nw")]
    [InlineData("se",        "se")]
    [InlineData("southeast", "se")]
    [InlineData("sw",        "sw")]
    [InlineData("southwest", "sw")]
    [InlineData("u",         "u")]
    [InlineData("up",        "u")]
    [InlineData("d",         "d")]
    [InlineData("down",      "d")]
    public void NormaliseDirection_RoundTripsAllForms(string input, string expected)
    {
        Assert.Equal(expected, TrapDisarmManager.NormaliseDirection(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("middle")]
    [InlineData("xyzzy")]
    public void NormaliseDirection_RejectsUnknown(string input)
    {
        Assert.Null(TrapDisarmManager.NormaliseDirection(input));
    }

    // ===== Skill gate =====

    [Fact]
    public void CanDisarm_False_WhenTrapsSkillZero()
    {
        var (mgr, _, _, _) = Setup(traps: 0);
        Assert.False(mgr.CanDisarm);
    }

    [Fact]
    public void CanDisarm_True_WhenTrapsSkillPositive()
    {
        var (mgr, _, _, _) = Setup(traps: 50);
        Assert.True(mgr.CanDisarm);
    }

    [Fact]
    public void CanDisarm_True_WhenClassGrantsTraps_EvenWithZeroStat()
    {
        // The Traps value was never captured (a freshly loaded profile, or a
        // brand-new character that hasn't run `stat`), but the selected class
        // grants the Traps skill in game data (Abil-0=40 FindTraps). Inference
        // recognises capability so the walker self-disarms instead of walking
        // through — the reported bug.
        GameDataCache cache = Cache(
            classesJson: "[{\"Name\":\"Ninja\",\"Abil-0\":40},{\"Name\":\"Mage\",\"Abil-0\":5}]");
        var (mgr, _, _, _) = Setup(traps: 0, cache: cache, @class: "Ninja");
        Assert.True(mgr.CanDisarm);
        Assert.True(mgr.SkillInferredFromClassOrRace);
    }

    [Fact]
    public void CanDisarm_True_WhenRaceGrantsTraps_EvenWithZeroStat()
    {
        // Class doesn't grant it, but the race does (Abil-0=1002 GrantTraps).
        GameDataCache cache = Cache(
            classesJson: "[{\"Name\":\"Mage\",\"Abil-0\":5}]",
            racesJson:   "[{\"Name\":\"Gnome\",\"Abil-0\":1002},{\"Name\":\"Human\",\"Abil-0\":0}]");
        var (mgr, _, _, _) = Setup(traps: 0, cache: cache, race: "Gnome", @class: "Mage");
        Assert.True(mgr.CanDisarm);
    }

    [Fact]
    public void CanDisarm_False_WhenClassAndRaceLackTraps_AndStatZero()
    {
        GameDataCache cache = Cache(
            classesJson: "[{\"Name\":\"Mage\",\"Abil-0\":5}]",
            racesJson:   "[{\"Name\":\"Human\",\"Abil-0\":0}]");
        var (mgr, _, _, _) = Setup(traps: 0, cache: cache, race: "Human", @class: "Mage");
        Assert.False(mgr.CanDisarm);
        Assert.False(mgr.SkillInferredFromClassOrRace);
    }

    [Fact]
    public void SkillInferredFromClassOrRace_False_WhenStatPositive()
    {
        // A parsed positive Traps value is the primary signal, so the inference
        // diagnostic reads false even when the class would also grant the skill.
        GameDataCache cache = Cache(
            classesJson: "[{\"Name\":\"Ninja\",\"Abil-0\":40}]");
        var (mgr, _, _, _) = Setup(traps: 50, cache: cache, @class: "Ninja");
        Assert.True(mgr.CanDisarm);
        Assert.False(mgr.SkillInferredFromClassOrRace);
    }

    // ===== Single-request happy path =====
    // Every request disarms directly: `disarm trap <dir>` fires against the trap in
    // that direction without searching for it first (user, 2026-09-27).

    [Fact]
    public void Enqueue_DisarmsImmediately_WhenIdle()
    {
        var (mgr, _, _, wire) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", text => reply = text);

        Assert.Equal("disarm trap n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("n", mgr.CurrentDirection);
        Assert.Null(reply);   // no terminal state yet
    }

    [Fact]
    public void Enqueue_NeverSearches()
    {
        var (mgr, _, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        Assert.DoesNotContain(wire, b => Encoding.Latin1.GetString(b).StartsWith("sea "));
    }

    [Fact]
    public void DisarmSuccess_RepliesAndReturnsToIdle()
    {
        var (mgr, router, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", text => reply = text);

        Dispatch(router, "You successfully disarmed the trap to the north.");

        Assert.Equal("Trap to the n disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
        Assert.Null(mgr.CurrentDirection);
    }

    [Fact]
    public void DisarmSuccess_IgnoresWrongDirection()
    {
        // Defensive: we're disarming north, the server printed a line for east
        // (another player's disarm, leftover output). Don't complete.
        var (mgr, router, _, _) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });

        Dispatch(router, "You successfully disarmed the trap to the east.");

        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    [Fact]
    public void LongFormDirection_DisarmsAndMatchesTheSuccessLine()
    {
        // The walker enqueues the LONG-form direction word ("southeast"), not the
        // short form the @trap handler parses; the game replies long-form too.
        // Matching normalises both sides (report 132150).
        var (mgr, router, _, wire) = Setup();
        string? reply = null;
        mgr.Enqueue("southeast", "walker", t => reply = t);
        Assert.Equal("disarm trap southeast\r", Encoding.Latin1.GetString(Assert.Single(wire)));

        Dispatch(router, "You successfully disarmed the trap to the southeast.");
        Assert.Equal("Trap to the southeast disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // Paradigm capture (2026-09-27): a failed disarm prints
    // "You try to disarm the trap, but instead trigger it!" (no direction), and
    // `disarm trap <dir>` with no trap that way prints "Your command had no effect."

    [Fact]
    public void DisarmTriggered_TriesAgain()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("w", "Raijin", _ => { });
        wire.Clear();

        Dispatch(router, "You try to disarm the trap, but instead trigger it!");

        Assert.Equal("disarm trap w\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    // Every trap has its own failure wording; any of them retries the pending disarm.
    [Fact]
    public void DisarmTriggered_ByAnotherTrapsWording_TriesAgain()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("n", "walker", _ => { });
        wire.Clear();

        Dispatch(router, "You fail to disarm the trap, and blades sweep out and slice you!");

        Assert.Equal("disarm trap n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    // A trap whose failure wording we don't know gets no recognised reply: the
    // watchdog handles it like one that went off, and keeps the lines for the log.
    [Fact]
    public void UnansweredDisarm_RetriesAndKeepsTheLines()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        List<Action> timers = new();
        TrapDisarmManager mgr = new(router, new PlayerStats { Traps = 50 }, Cache(),
            scheduleDelay: (_, cb) => { timers.Add(cb); return new NoopHandle(); });
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        mgr.Enqueue("n", "walker", _ => { });
        wire.Clear();

        Dispatch(router, "disarm trap n");
        Dispatch(router, "A gout of green flame bursts from the wall!");
        timers[^1]();

        Assert.Equal("disarm trap n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal("A gout of green flame bursts from the wall!", mgr.LastUnansweredReply);
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    private sealed class NoopHandle : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void DisarmTriggered_AtTheCap_GivesUpAndReports()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.MaxDisarmAttempts = 2;
        string? reply = null;
        mgr.Enqueue("w", "Raijin", t => reply = t);

        Dispatch(router, "You try to disarm the trap, but instead trigger it!");   // attempt 1 failed → 2nd
        Dispatch(router, "You try to disarm the trap, but instead trigger it!");   // attempt 2 failed → stop

        Assert.Equal(2, wire.Count);
        Assert.Equal("Couldn't disarm the trap to the w (2 attempts).", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void NoEffect_MeansNoTrap_AndTheExitIsClear()
    {
        var (mgr, router, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("e", "walker", t => reply = t);

        Dispatch(router, "Your command had no effect.");

        Assert.Equal("No trap to the e to disarm.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // Stock: "You failed to disarm any trap to the <dir>." is both a fumble and the
    // reply for a direction with no trap. Retry, then take the exit as clear.
    [Fact]
    public void StockFailedAny_RetriesThenTakesTheExitAsClear()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.MaxDisarmAttempts = 2;
        string? reply = null;
        mgr.Enqueue("e", "walker", t => reply = t);

        Dispatch(router, "You failed to disarm any trap to the east.");
        Assert.Equal(2, wire.Count);                  // retried once
        Assert.Null(reply);

        Dispatch(router, "You failed to disarm any trap to the east.");
        Assert.StartsWith("No trap to the e", reply);  // the walker's "clear" reply
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // Every attempt reports its outcome; Stock's ambiguous "failed to disarm any"
    // only counts once the same exit proves to hold a trap.
    [Fact]
    public void DisarmAttempted_CountsFumblesOnlyOnceATrapIsProven()
    {
        var (mgr, router, _, _) = Setup();
        List<bool> outcomes = new();
        mgr.DisarmAttempted += outcomes.Add;

        mgr.Enqueue("e", "walker", _ => { });
        Dispatch(router, "You failed to disarm any trap to the east.");
        Assert.Empty(outcomes);                          // could be an empty exit
        Dispatch(router, "You try to disarm the trap, but instead trigger it!");
        Dispatch(router, "You successfully disarmed the trap to the east.");
        Assert.Equal(new[] { false, false, true }, outcomes);

        outcomes.Clear();
        mgr.MaxDisarmAttempts = 2;
        mgr.Enqueue("n", "walker", _ => { });
        Dispatch(router, "You failed to disarm any trap to the north.");
        Dispatch(router, "You failed to disarm any trap to the north.");
        Assert.Empty(outcomes);                          // taken as no trap: not attempts

        mgr.Enqueue("s", "walker", _ => { });
        Dispatch(router, "Your command had no effect.");
        Assert.Empty(outcomes);
    }

    // Paradigm report paradigm-20261002-191037: the line went unrecognised, so the
    // watchdog took it for a trap that went off and retried.
    [Fact]
    public void AlreadyDisarmed_MeansTheExitIsClear_AndIsNotAnAttempt()
    {
        var (mgr, router, _, wire) = Setup();
        List<bool> outcomes = new();
        mgr.DisarmAttempted += outcomes.Add;
        string? reply = null;
        mgr.Enqueue("w", "loop", t => reply = t);

        Dispatch(router, "The trap is already disarmed.");

        Assert.Single(wire);
        Assert.Equal("Trap to the w already disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
        Assert.Empty(outcomes);
    }

    // An exit we disarmed stays down until the realm's re-arm time: skipped before
    // it, disarmed again at or after it. Someone else's "already disarmed" never
    // starts the clock.
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 2)]
    public void OurDisarm_SkipsTheExitUntilItReArms(bool paradigm, int rearmMinutes)
    {
        DateTimeOffset now = Now;
        GameDataCache cache = new(_root);
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), paradigm ? "[{\"Legit\":2}]" : "[{\"Legit\":0}]");
        cache.SwitchSet("set");
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        TrapDisarmManager mgr = new(router, new PlayerStats { Traps = 50 }, cache, clock: () => now);
        List<byte[]> wire = new();
        mgr.SetWireSender(wire.Add);
        RoomKey room = new(9, 448);
        mgr.SetCurrentRoom(() => room);

        mgr.Enqueue("w", "loop", _ => { });
        Dispatch(router, "You successfully disarmed the trap to the west.");
        Assert.Single(wire);

        now += TimeSpan.FromMinutes(rearmMinutes) - TimeSpan.FromSeconds(1);
        string? reply = null;
        mgr.Enqueue("west", "loop", t => reply = t);
        Assert.Single(wire);                                   // skipped
        Assert.Contains("disarmed", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);

        room = new(9, 449);                                    // same direction, other room
        mgr.Enqueue("w", "loop", _ => { });
        Assert.Equal(2, wire.Count);
        Dispatch(router, "The trap is already disarmed.");     // not ours: no clock
        mgr.Enqueue("w", "loop", _ => { });
        Assert.Equal(3, wire.Count);
        Dispatch(router, "The trap is already disarmed.");

        room = new(9, 448);
        now += TimeSpan.FromSeconds(1);                        // on the timer: disarm again
        mgr.Enqueue("w", "loop", _ => { });
        Assert.Equal(4, wire.Count);
    }

    [Fact]
    public void StockFailedAny_OtherDirection_Ignored()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("e", "walker", _ => { });
        Dispatch(router, "You failed to disarm any trap to the west.");
        Assert.Single(wire);
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    [Fact]
    public void NoEffect_WhileIdle_IsSomeoneElsesRefusal()
    {
        var (mgr, router, _, wire) = Setup();
        Dispatch(router, "Your command had no effect.");
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
        Assert.Empty(wire);
    }

    // ===== Queue =====

    [Fact]
    public void Enqueue_DuringInFlight_QueuesRequest()
    {
        var (mgr, _, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        // Second request — should queue, not interrupt.
        mgr.Enqueue("e", "Helper", _ => { });

        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("n", mgr.CurrentDirection);
        Assert.Equal(1, mgr.QueueDepth);
        // Only the first request's disarm landed.
        Assert.Single(wire);
    }

    [Fact]
    public void Queue_NextRequestStartsAfterCurrentCompletes()
    {
        var (mgr, router, _, wire) = Setup();
        string? firstReply = null, secondReply = null;
        mgr.Enqueue("n", "Raijin", t => firstReply = t);
        mgr.Enqueue("e", "Helper", t => secondReply = t);
        wire.Clear();

        Dispatch(router, "You successfully disarmed the trap to the north.");

        Assert.NotNull(firstReply);
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("e", mgr.CurrentDirection);
        Assert.Equal("disarm trap e\r", Encoding.Latin1.GetString(wire[^1]));

        Dispatch(router, "You successfully disarmed the trap to the east.");

        Assert.Equal("Trap to the e disarmed.", secondReply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void Enqueue_SameDirectionAsInFlight_Ignored()
    {
        var (mgr, _, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        wire.Clear();

        // Second @trap n while we're already on it.
        mgr.Enqueue("n", "Helper", _ => { });

        Assert.Empty(wire);
        Assert.Equal(0, mgr.QueueDepth);
    }

    [Fact]
    public void Enqueue_SameDirectionAsQueued_Ignored()
    {
        var (mgr, _, _, _) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        mgr.Enqueue("e", "Helper", _ => { });
        Assert.Equal(1, mgr.QueueDepth);

        // Second @trap e — already queued.
        mgr.Enqueue("e", "Buddy", _ => { });

        Assert.Equal(1, mgr.QueueDepth);
    }

    // ===== Stop =====

    [Fact]
    public void StopAll_AbortsInFlight_AndTelepathsStopReply()
    {
        var (mgr, _, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", t => reply = t);

        mgr.StopAll();

        Assert.Equal("Trap flow stopped.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void StopAll_DrainsQueue_AndTelepathsEachSender()
    {
        var (mgr, _, _, _) = Setup();
        string? r1 = null, r2 = null, r3 = null;
        mgr.Enqueue("n", "Raijin", t => r1 = t);
        mgr.Enqueue("e", "Helper", t => r2 = t);
        mgr.Enqueue("s", "Buddy",  t => r3 = t);

        mgr.StopAll();

        Assert.Equal("Trap flow stopped.", r1);
        Assert.Equal("Trap flow stopped.", r2);
        Assert.Equal("Trap flow stopped.", r3);
        Assert.Equal(0, mgr.QueueDepth);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void StopAll_WhenIdle_NoOps()
    {
        var (mgr, _, _, _) = Setup();
        mgr.StopAll();
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void StopAll_AllowsNextEnqueueToStartCleanly()
    {
        var (mgr, _, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        mgr.StopAll();
        wire.Clear();

        mgr.Enqueue("e", "Helper", _ => { });

        Assert.Equal("disarm trap e\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    // ===== Dispose =====

    [Fact]
    public void Dispose_UnsubscribesPatterns()
    {
        var (mgr, router, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", t => reply = t);

        mgr.Dispose();
        Dispatch(router, "You successfully disarmed the trap to the north.");

        Assert.Null(reply);
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    // Report paradigm-20261003-111800: two fires took HP under the rest trigger and
    // the third try went out as the rest began. The retry now waits the rest out.
    [Fact]
    public void RestHold_AfterTheTrapFires_HoldsTheRetry_UntilTheRestIsOver()
    {
        var (mgr, router, _, wire) = Setup();
        bool resting = false;
        mgr.SetRestHold(() => resting);
        mgr.Enqueue("e", "loop", _ => { });
        wire.Clear();

        resting = true;                                  // the fire dropped HP; resting
        Dispatch(router, "An arrow shoots out of the wall and strikes you!");
        Assert.Empty(wire);
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);

        mgr.NotifyRestHoldChanged();                     // still resting
        Assert.Empty(wire);

        resting = false;
        mgr.NotifyRestHoldChanged();
        Assert.Equal("disarm trap e\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }
}
