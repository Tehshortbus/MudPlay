using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// State-machine + queue coverage for TrapDisarmManager. The handler-
/// side authorisation + channel-aware denial live in TrapHandlerTests;
/// these tests drive the manager directly via Enqueue + simulated
/// inbound game messages.
/// </summary>
public sealed class TrapDisarmManagerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public TrapDisarmManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-trapdisarm-tests-" + Path.GetRandomFileName());
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

    [Fact]
    public void Enqueue_StartsSearchImmediately_WhenIdle()
    {
        var (mgr, _, _, wire) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", text => reply = text);

        Assert.Single(wire);
        Assert.Equal("sea n\r", Encoding.Latin1.GetString(wire[0]));
        Assert.Equal(TrapDisarmManager.State.Searching, mgr.CurrentState);
        Assert.Equal("n", mgr.CurrentDirection);
        Assert.Null(reply);   // no terminal state yet
    }

    [Fact]
    public void Enqueue_TrapKnown_SkipsSearchAndDisarmsImmediately()
    {
        // The walker enqueues with trapKnown=true because it only reaches the
        // trap path on a RoomExitHint.Trap — the trap is already known, so the
        // confirming `sea <dir>` is a wasted round and we go straight to disarm.
        var (mgr, _, _, wire) = Setup();
        mgr.Enqueue("se", "walker", _ => { }, trapKnown: true);

        Assert.Equal("disarm trap se\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("se", mgr.CurrentDirection);
    }

    [Fact]
    public void Enqueue_TrapKnown_DisarmSuccess_RepliesAndReturnsToIdle()
    {
        var (mgr, router, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("se", "walker", t => reply = t, trapKnown: true);

        Dispatch(router, "You successfully disarmed the trap to the southeast.");

        Assert.Equal("Trap to the se disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void SearchSuccess_TransitionsToDisarmAndSendsDisarmCommand()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        wire.Clear();

        Dispatch(router, "You found a trap to the north!");

        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("disarm trap n\r", Encoding.Latin1.GetString(Assert.Single(wire)));
    }

    [Fact]
    public void DisarmSuccess_RepliesAndReturnsToIdle()
    {
        var (mgr, router, _, _) = Setup();
        string? reply = null;
        mgr.Enqueue("n", "Raijin", text => reply = text);
        Dispatch(router, "You found a trap to the north!");

        Dispatch(router, "You successfully disarmed the trap to the north.");

        Assert.Equal("Trap to the n disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
        Assert.Null(mgr.CurrentDirection);
    }

    [Fact]
    public void SearchFailure_RetriesUntilSuccess()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        wire.Clear();

        // Two failures, then a success.
        Dispatch(router, "You notice nothing different to the north.");
        Dispatch(router, "You notice nothing different to the north.");
        Dispatch(router, "You found a trap to the north!");

        Assert.Equal(3, wire.Count);
        Assert.Equal("sea n\r",          Encoding.Latin1.GetString(wire[0]));
        Assert.Equal("sea n\r",          Encoding.Latin1.GetString(wire[1]));
        Assert.Equal("disarm trap n\r",  Encoding.Latin1.GetString(wire[2]));
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
    }

    [Fact]
    public void SearchFailure_HitsMaxAttempts_ReplyAndIdle()
    {
        var (mgr, router, _, _) = Setup();
        mgr.MaxSearchAttempts = 3;
        string? reply = null;
        mgr.Enqueue("n", "Raijin", text => reply = text);

        // Three nothing-different lines should hit the cap (first
        // attempt was the immediate-send from Enqueue, then two retries).
        Dispatch(router, "You notice nothing different to the north.");
        Dispatch(router, "You notice nothing different to the north.");
        Dispatch(router, "You notice nothing different to the north.");

        Assert.NotNull(reply);
        Assert.Contains("Couldn't find trap to the n", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    [Fact]
    public void SearchMatch_IgnoresWrongDirection()
    {
        // Defensive: we asked for north, server happened to print a line
        // for east (different request, leftover output, etc.). Don't
        // transition.
        var (mgr, router, _, _) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });

        Dispatch(router, "You found a trap to the east!");

        // Still searching for north — east match was ignored.
        Assert.Equal(TrapDisarmManager.State.Searching, mgr.CurrentState);
    }

    [Fact]
    public void LongFormDirection_MatchesSearchThenDisarmThenSuccess()
    {
        // Regression: the walker enqueues the LONG-form direction word
        // ("southeast"), not the short form the @trap handler parses. The
        // game replies in the long form too. Direction matching must
        // normalise BOTH sides — otherwise a successful search never
        // advances past Searching and the disarm stalls (the reported bug).
        var (mgr, router, _, wire) = Setup();
        string? reply = null;
        mgr.Enqueue("southeast", "walker", t => reply = t);
        Assert.Equal("sea southeast\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        wire.Clear();

        Dispatch(router, "You found a trap to the southeast!");
        Assert.Equal(TrapDisarmManager.State.DisarmPending, mgr.CurrentState);
        Assert.Equal("disarm trap southeast\r", Encoding.Latin1.GetString(Assert.Single(wire)));

        Dispatch(router, "You successfully disarmed the trap to the southeast.");
        Assert.Equal("Trap to the southeast disarmed.", reply);
        Assert.Equal(TrapDisarmManager.State.Idle, mgr.CurrentState);
    }

    // ===== Queue =====

    [Fact]
    public void Enqueue_DuringInFlight_QueuesRequest()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        // Second request — should queue, not interrupt.
        mgr.Enqueue("e", "Helper", _ => { });

        Assert.Equal(TrapDisarmManager.State.Searching, mgr.CurrentState);
        Assert.Equal("n", mgr.CurrentDirection);
        Assert.Equal(1, mgr.QueueDepth);
        // Only the first request's search command landed.
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

        // Complete the first one.
        Dispatch(router, "You found a trap to the north!");
        Dispatch(router, "You successfully disarmed the trap to the north.");

        Assert.NotNull(firstReply);
        Assert.Equal(TrapDisarmManager.State.Searching, mgr.CurrentState);
        Assert.Equal("e", mgr.CurrentDirection);
        // Second request's search command was just dispatched.
        Assert.Equal("sea e\r", Encoding.Latin1.GetString(wire[^1]));

        // Finish the second one too.
        Dispatch(router, "You found a trap to the east!");
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

        Assert.Equal("sea e\r", Encoding.Latin1.GetString(Assert.Single(wire)));
        Assert.Equal(TrapDisarmManager.State.Searching, mgr.CurrentState);
    }

    // ===== Dispose =====

    [Fact]
    public void Dispose_UnsubscribesPatterns()
    {
        var (mgr, router, _, wire) = Setup();
        mgr.Enqueue("n", "Raijin", _ => { });
        wire.Clear();

        mgr.Dispose();
        // After dispose, dispatching wouldn't transition state — verify
        // by ensuring no new wire-send fires.
        Dispatch(router, "You found a trap to the north!");

        Assert.Empty(wire);
    }
}
