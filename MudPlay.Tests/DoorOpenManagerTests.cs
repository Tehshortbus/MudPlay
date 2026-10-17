using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// FSM coverage for DoorOpenManager. Drives the manager
// through every transition by injecting fabricated server lines via
// MessageRouter.Dispatch and inspecting the wire bytes
// + terminal callback result.
public sealed class DoorOpenManagerTests
{
    // ----- harness ---------------------------------------------------

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public PlayerStats Stats { get; }
        public DoorOpenManager Mgr { get; }
        public List<byte[]> Sent { get; } = new();
        public Dictionary<int, string> Items { get; } = new();
        public int MaxPick { get; set; } = 10;
        public bool PicklocksOverBash { get; set; }
        // Bash rest gates (see DoorOpenManager): BashRestNeeded=true simulates HP at
        // the rest trigger (pause bashing); BashRestRecovered=true simulates HP back
        // at rest-max (resume). Defaults never pause — bash proceeds uncapped.
        public bool BashRestNeeded { get; set; }
        public bool BashRestRecovered { get; set; } = true;
        // Whether a stat screen has been read; false leaves Strength and Picklocks at 0.
        public bool StatsRead { get; set; } = true;

        public Harness(Func<int, bool>? holdsKeyItem = null)
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Stats = new PlayerStats();
            Stats.Strength = 50;
            Stats.Picklocks = 50;
            Items[172] = "black star key";
            Items[1980] = "ancient brass key";
            Mgr = new DoorOpenManager(Router, Stats,
                () => MaxPick, () => PicklocksOverBash,
                itemNameLookup: id => Items.TryGetValue(id, out string? name) ? name : null,
                holdsKeyItem: holdsKeyItem,
                bashRestNeeded: () => BashRestNeeded,
                bashRestRecovered: () => BashRestRecovered,
                scheduleDelay: Schedule,
                statsRead: () => StatsRead);
            Mgr.SetWireSender(Sent.Add);
        }

        // Simulate a live-HP change notification (AppServices subscribes PlayerState.Hp).
        public void NotifyHealth() => Mgr.NotifyHealthChanged();

        // Controllable stand-in for the UI-thread one-shot. Captures the latest
        // armed watchdog callback; FireTimeout runs it, simulating a door command
        // that drew no response. Never auto-fires, so tests that feed result lines
        // (which disarm the watchdog) are unaffected.
        private Action? _pendingTimeout;
        private IDisposable Schedule(TimeSpan _, Action callback)
        {
            _pendingTimeout = callback;
            return new FakeHandle(() =>
            {
                if (ReferenceEquals(_pendingTimeout, callback)) _pendingTimeout = null;
            });
        }
        public bool HasWatchdogArmed => _pendingTimeout is not null;
        public void FireTimeout() => _pendingTimeout?.Invoke();

        private sealed class FakeHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }

        // All bytes sent so far, decoded and trailing-CR trimmed — for asserting
        // an ordered command sequence (e.g. get-before-use).
        public IReadOnlyList<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Line(string text)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, [], DateTimeOffset.UtcNow, false);
            Router.Dispatch(emitted);
        }

        // A command as the server echoes it: the prompt row, then its trailing text
        // as a second line carrying the same timestamp.
        public void Echoed(string command)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine("[HP=185/KAI=10]:", [], at, true));
            Router.Dispatch(new LineExtractor.EmittedLine(command, [], at, false));
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose() => Mgr.Dispose();
    }

    private static readonly RoomKey DoorRoom = new(7, 1247);
    private static readonly RoomKey NextRoom = new(7, 1246);

    // ----- happy paths ----------------------------------------------

    [Fact]
    public void Bash_FirstAttempt_Succeeds_RepliesOpened()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("bash e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);

        h.Line("With a roar, you bashed the door open!");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    // The server refuses the bash outright (no weapon / no bash ability): re-bashing
    // can't help, and with no pattern it used to re-bash forever on the watchdog.
    // It falls back to pick instead (Stock DLL _cmd_bash).
    [Theory]
    [InlineData("You need a weapon to bash with!")]
    [InlineData("You don't know the first thing about bashing!")]
    public void Bash_Refused_FallsBackToPick(string refusal)
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("bash e", h.LastSent);

        h.Line(refusal);

        Assert.Equal("pick e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingPick, h.Mgr.CurrentState);
        Assert.Null(result);
    }

    // "The <key> doesn't seem to fit that lock." — the wrong key: fail now rather
    // than wait out the watchdog.
    [Fact]
    public void UseKey_WrongKey_FailsAtOnce()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: false, keyItemId: 172, "walker", r => result = r);
        Assert.Equal("use black star key w", h.LastSent);

        h.Line("The black star key doesn't seem to fit that lock.");

        Assert.IsType<DoorOpenResult.Failed>(result);
    }

    // ----- response watchdog (no-hang) ------------------------------

    [Fact]
    public void Watchdog_BashNoResponse_ReBashesUncapped_NeverFallsBack()
    {
        // A bash that draws NO recognised response is re-sent on each watchdog miss.
        // Bashing is uncapped (a bashable door opens eventually), so it never falls
        // back to pick and never fails — it just keeps swinging.
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("bash e", h.LastSent);              // attempt 1
        Assert.True(h.HasWatchdogArmed);

        h.FireTimeout();                                 // silence → re-bash
        h.FireTimeout();                                 // silence → re-bash
        Assert.Equal(new[] { "bash e", "bash e", "bash e" }, h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);
        Assert.Null(result);                             // still trying, no failure
        Assert.True(h.HasWatchdogArmed);
    }

    [Fact]
    public void Watchdog_RealResultDisarms_NoSpuriousTimeout()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.True(h.HasWatchdogArmed);

        h.Line("With a roar, you bashed the door open!");   // real success disarms it
        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.False(h.HasWatchdogArmed);

        h.FireTimeout();                                    // stale fire is a no-op
        Assert.Single(h.AllSent);                           // only the one "bash e"
    }

    [Fact]
    public void Pick_FirstAttempt_Succeeds_ThenOpen_ThenOpened()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("pick w", h.LastSent);

        h.Line("You successfully unlock the door.");
        Assert.Equal("open w", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingOpen, h.Mgr.CurrentState);

        h.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Pick_LiveStockWording_UnlockedThenYouOpen_Opens()
    {
        // Exact wording captured from the stock server (report
        // stock-20260730-182812): picklock success is PAST tense "unlocked" and
        // the open reply is "You open the door." — neither matched the original
        // present-tense / "is now open" patterns, so the FSM stalled in
        // WaitingPick and never sent `open`.
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("pick n", h.LastSent);

        h.Line("You successfully unlocked the door.");
        Assert.Equal("open n", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingOpen, h.Mgr.CurrentState);

        h.Line("You open the door.");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    [Fact]
    public void Pick_DoorNotLocked_SkipsToOpenImmediately()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        h.Line("The door was not locked.");
        Assert.Equal("open n", h.LastSent);
        h.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Bash_RetriesOnFailure_Uncapped_NeverFallsBackToPick()
    {
        // A bashable door is bashed until it opens — each failure re-bashes, never
        // falling back to pick (no attempt cap), then a success ends it.
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("bash n", h.LastSent);
        for (int i = 0; i < 5; i++) h.Line("Your attempts to bash through fail.");
        Assert.Equal("bash n", h.LastSent);              // still bashing, not "pick n"
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);
        Assert.Null(result);

        h.Line("With a roar, you bashed the door open!");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Bash_HpBelowRestTrigger_PausesForRest_ResumesAtRestMax()
    {
        // Bashing drains HP: once HP hits the rest trigger the bash pauses (no wire
        // send) so HealthManager can rest to rest-max; the HP-change notification
        // then resumes the swing.
        using Harness h = new() { BashRestNeeded = true, BashRestRecovered = false };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        // Arrived at the door already below the rest trigger — paused, nothing sent.
        Assert.Empty(h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBashRest, h.Mgr.CurrentState);

        // A HP tick while still below rest-max doesn't resume.
        h.NotifyHealth();
        Assert.Empty(h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBashRest, h.Mgr.CurrentState);

        // Recovered to rest-max → resume bashing.
        h.BashRestNeeded = false;
        h.BashRestRecovered = true;
        h.NotifyHealth();
        Assert.Equal("bash e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);

        h.Line("With a roar, you bashed the door open!");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Bash_FailureBelowRestTrigger_PausesInsteadOfReBashing()
    {
        // A bash fails and HP is now at the rest trigger — the retry pauses for rest
        // rather than immediately re-bashing.
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("bash n", h.LastSent);

        h.BashRestNeeded = true;                         // HP dropped over that swing
        h.Line("Your attempts to bash through fail.");   // retry → sees low HP → pause
        Assert.Single(h.AllSent);                        // no second bash yet
        Assert.Equal(DoorOpenManager.DoorState.WaitingBashRest, h.Mgr.CurrentState);

        h.BashRestNeeded = false;                         // rested up
        h.NotifyHealth();
        Assert.Equal(new[] { "bash n", "bash n" }, h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);
    }

    // ----- viability gates ------------------------------------------

    [Fact]
    public void UnbashablePickOnly_BashSettingPreferred_StillPicks()
    {
        // canBash=false + low picklocks meets the (0) requirement.
        // Manager should pick, not bash, regardless of preference.
        using Harness h = new() { PicklocksOverBash = false };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.S, 0, canBash: false, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("pick s", h.LastSent);
    }

    [Fact]
    public void NeitherVerbViable_RepliesFailedImmediately()
    {
        // Stat req 100, player has 0 / 0, can bash but strength too low.
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 100, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.True(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
        Assert.Empty(h.Sent);
    }

    // Before a stat screen is read Strength and Picklocks are 0, so any numbered door
    // has no viable verb: that is not the door beating the character, and the walk
    // must fail as it always did, not give the door up for good.
    [Fact]
    public void NoViableVerb_BeforeAnyStatScreenIsRead_IsAnOrdinaryFailure()
    {
        using Harness h = new() { StatsRead = false };
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 100, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.False(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
        Assert.Empty(h.Sent);
    }

    // Ten picks that drew no reply are a lost reply, not the lock winning.
    [Fact]
    public void PickCapHit_ByTimeoutsAlone_IsAnOrdinaryFailure()
    {
        using Harness h = new() { MaxPick = 3 };
        h.Stats.Strength = 120;
        h.Stats.Picklocks = 303;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 0, "walker", r => result = r);

        for (int i = 0; i < 3; i++)
        {
            Assert.Null(result);
            h.FireTimeout();
        }

        Assert.False(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
        Assert.Equal(3, h.Sent.Count);
    }

    // The Ancient Coliseum's door down: Picklocks just over the lock, so a pick is
    // the only verb, and all of the tries miss. That is the door beating the
    // character, which the walker goes round; a key that turns out missing is not.
    [Fact]
    public void PickCapHit_WithNoOtherVerb_FailsAsUnopenable()
    {
        using Harness h = new() { MaxPick = 3 };
        h.Stats.Strength = 120;
        h.Stats.Picklocks = 303;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 0, "walker", r => result = r);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("pick d", h.LastSent);
            Assert.Null(result);
            h.Line("Your skill fails you this time.");
        }

        Assert.True(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    [Fact]
    public void KeyMissing_FailsWithoutCallingTheDoorUnopenable()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 172, "walker", r => result = r);
        Assert.Equal("use black star key n", h.LastSent);

        h.Line("You don't have black star key.");

        Assert.False(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
    }

    // A keyed door the character tried to pick: the picks ran out, the key was the
    // last thing left to try, and it isn't held. That is the door beating the
    // character, the same as a keyless one.
    [Fact]
    public void KeyedDoor_PicksRunOut_ThenKeyNotHeld_FailsAsUnopenable()
    {
        using Harness h = new() { MaxPick = 2 };
        h.Stats.Strength = 120;
        h.Stats.Picklocks = 303;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 172, "walker", r => result = r);

        h.Line("Your skill fails you this time.");
        h.Line("Your skill fails you this time.");
        Assert.Equal("use black star key d", h.LastSent);
        Assert.Null(result);

        h.Line("You don't have black star key.");

        Assert.True(Assert.IsType<DoorOpenResult.Failed>(result).Unopenable);
    }

    // A failed door re-asked for at once (a walk failing and being issued again from
    // inside the reply) starts afresh. Still standing as the request in flight, it
    // was swallowed as a duplicate and its caller left waiting for good.
    [Fact]
    public void Failed_ThenTheSameDoorAskedForAgainFromInsideTheReply_IsStarted()
    {
        using Harness h = new() { MaxPick = 1 };
        h.Stats.Strength = 120;
        h.Stats.Picklocks = 303;
        DoorOpenResult? second = null;
        h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 0, "walker",
            _ => h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 0, "walker", r => second = r));
        Assert.Single(h.Sent);

        h.Line("Your skill fails you this time.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("pick d", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingPick, h.Mgr.CurrentState);
        h.Line("Your skill fails you this time.");
        Assert.True(Assert.IsType<DoorOpenResult.Failed>(second).Unopenable);
    }

    // The caller re-plans from inside the reply and may ask for another door out of
    // the same room at once: the failed request must be gone by then.
    [Fact]
    public void Failed_IsClearedBeforeTheCallerHearsOfIt_SoItCanAskForAnotherDoorAtOnce()
    {
        using Harness h = new();
        h.Stats.Strength = 120;
        h.Stats.Picklocks = 0;
        DoorOpenResult? second = null;
        h.Mgr.Enqueue(Direction.D, 301, canBash: true, keyItemId: 0, "walker",
            _ => h.Mgr.Enqueue(Direction.E, 21, canBash: true, keyItemId: 0, "walker", r => second = r));

        Assert.Equal("bash e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);
        h.Line("You bashed the door open.");
        Assert.IsType<DoorOpenResult.Opened>(second);
    }

    [Fact]
    public void HighReqOverThreshold_NotBashable_FallsThroughToPick()
    {
        // Stat req 250 > UnbashableStrengthThreshold (200). Player
        // has plenty of picklocks. Manager should go straight to pick.
        using Harness h = new();
        h.Stats.Strength = 500;
        h.Stats.Picklocks = 300;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 250, canBash: true, keyItemId: 0, "walker", r => result = r);

        Assert.Equal("pick e", h.LastSent);
    }

    // ----- queue / lifecycle ----------------------------------------

    [Fact]
    public void StopAll_AbortsCurrent_DrainsQueue_RepliesFailedToAll()
    {
        using Harness h = new();
        DoorOpenResult? a = null;
        DoorOpenResult? b = null;
        h.Mgr.Enqueue(Direction.N, 0, true, keyItemId: 0, "walker", r => a = r);
        h.Mgr.Enqueue(Direction.S, 0, true, keyItemId: 0, "walker", r => b = r);

        h.Mgr.StopAll();

        Assert.IsType<DoorOpenResult.Failed>(a);
        Assert.IsType<DoorOpenResult.Failed>(b);
        Assert.Equal(0, h.Mgr.QueueDepth);
    }

    [Fact]
    public void Queue_RunsRequestsInOrder()
    {
        using Harness h = new();
        var results = new List<DoorOpenResult>();
        h.Mgr.Enqueue(Direction.N, 0, true, keyItemId: 0, "walker", results.Add);
        h.Mgr.Enqueue(Direction.S, 0, true, keyItemId: 0, "walker", results.Add);

        Assert.Equal("n", h.Mgr.CurrentDirection);
        h.Line("you bashed the door open.");
        Assert.Single(results);
        Assert.IsType<DoorOpenResult.Opened>(results[0]);

        Assert.Equal("s", h.Mgr.CurrentDirection);
        h.Line("you bashed the gate open.");
        Assert.Equal(2, results.Count);
    }

    // ----- direction encoding ---------------------------------------

    [Theory]
    [InlineData(Direction.N,  "bash n")]
    [InlineData(Direction.S,  "bash s")]
    [InlineData(Direction.E,  "bash e")]
    [InlineData(Direction.W,  "bash w")]
    [InlineData(Direction.NE, "bash ne")]
    [InlineData(Direction.NW, "bash nw")]
    [InlineData(Direction.SE, "bash se")]
    [InlineData(Direction.SW, "bash sw")]
    [InlineData(Direction.U,  "bash u")]
    [InlineData(Direction.D,  "bash d")]
    public void Bash_UsesShortDirectionForm(Direction dir, string expected)
    {
        using Harness h = new();
        h.Mgr.Enqueue(dir, 0, true, keyItemId: 0, "walker", _ => { });
        Assert.Equal(expected, h.LastSent);
    }

    // ----- keyed-door FSM (commit 3) --------------------------------

    [Fact]
    public void KeyedDoor_StatAltMet_TriesBashFirst_SavesKey()
    {
        // Keyed door with bashable alternative. Player meets stats.
        // Manager prefers bash → save the key charge.
        using Harness h = new();
        h.Stats.Strength = 100;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 50, canBash: true, keyItemId: 172, "walker", r => result = r);

        Assert.Equal("bash w", h.LastSent);
        h.Line("you bashed the door open.");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void KeyedDoor_NoStatAlt_GoesStraightToUseKey()
    {
        // Keyed door, no stat req at all → use the key directly.
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        // canBash=false + picklocks=0 → no viable verb → key path.
        Assert.Equal("use black star key w", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingUseKey, h.Mgr.CurrentState);
    }

    [Fact]
    public void KeyedDoor_UseKey_Succeeds_ThenOpen_ThenOpened()
    {
        // Force the no-viable-verb path so we go straight to key.
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        Assert.Equal("use black star key e", h.LastSent);

        h.Line("You successfully unlocked the door.");
        Assert.Equal("open e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingOpen, h.Mgr.CurrentState);

        h.Line("The door is now open.");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void KeyedDoor_PickAltExhausted_FallsBackToKey()
    {
        // Keyed door whose only stat-alt is picking (not bashable). Pick is tried
        // first and exhausts (cap 1); with no bash fallback available it uses the
        // key. (Bash has no cap, so a bashable keyed door would bash until it opens
        // and never reach the key — covered by KeyedDoor_StatAltMet_TriesBashFirst.)
        using Harness h = new() { MaxPick = 1 };
        h.Stats.Strength = 0;      // can't bash
        h.Stats.Picklocks = 100;   // can pick
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.S, 50, canBash: false, keyItemId: 172, "walker", r => result = r);

        Assert.Equal("pick s", h.LastSent);
        h.Line("Your lockpicking skill fails you.");
        // Pick exhausted, no bash alt → key fallback.
        Assert.Equal("use black star key s", h.LastSent);
    }

    [Fact]
    public void KeyedDoor_UseKey_MissingKey_RepliesFailed()
    {
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        h.Line("You have no black star key.");
        // The reason is what the walk fails with, so it names the key.
        Assert.Contains("black star key", Assert.IsType<DoorOpenResult.Failed>(result).Reason);
    }

    [Fact]
    public void KeyedDoor_UseKey_DoorNotLocked_SkipsToOpen_ThenOpened()
    {
        // Live bug: keyed door, use-key path. Server replied "The door was
        // not locked." (the door was already unlocked) but the FSM only
        // handled that line in WaitingPick — the use-key state ignored it,
        // stranding the walker until a manual room reparse showed the door
        // already open. Fix: "not locked" in any breach state skips to open.
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        Assert.Equal("use black star key e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingUseKey, h.Mgr.CurrentState);

        h.Line("The door was not locked.");
        Assert.Equal("open e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingOpen, h.Mgr.CurrentState);

        h.Line("The door is already open.");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void KeyedDoor_UnknownKeyId_FailsImmediately()
    {
        // Item id not in the ItemNameStore → walker can't form the
        // verb. Fail fast.
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 9999, "walker", r => result = r);

        Assert.IsType<DoorOpenResult.Failed>(result);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void KeyedDoor_KeyNotCarried_GetsFromRoomBeforeUse()
    {
        // Live bug: a keyed door where the key sat on the room floor (e.g. a
        // bone key dropped by the guardian). The manager blindly sent
        // `use <key> <dir>` with no possession awareness, so the key never
        // entered inventory and the door never opened. Fix: when the
        // possession predicate says the key isn't held, grab it off the floor
        // first — the server processes the queued `get` before the `use`.
        using Harness h = new(holdsKeyItem: _ => false);
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        Assert.Equal(
            new[] { "get black star key", "use black star key e" }, h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingUseKey, h.Mgr.CurrentState);
    }

    [Fact]
    public void KeyedDoor_KeyAlreadyCarried_SkipsGet()
    {
        // The possession predicate says the key is in inventory → no wasted
        // `get`, just the single-shot use.
        using Harness h = new(holdsKeyItem: _ => true);
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);

        Assert.Equal(new[] { "use black star key e" }, h.AllSent);
    }

    // ----- already-open responses (commit 8 fix) --------------------

    [Fact]
    public void Bash_OnAlreadyOpenDoor_TreatsAsSuccess()
    {
        // Live bug: walker queued bash on a door whose room display
        // showed `open door <dir>` (already open). Server replied
        // "The door is already open." but the FSM only handled that
        // message in WaitingOpen — bash got stuck waiting for a
        // "bashed the door open" line that never arrived. Fix: any
        // already-open response while we're bashing counts as success.
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.S, 0, true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("bash s", h.LastSent);

        h.Line("The door is already open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    [Fact]
    public void Pick_OnAlreadyOpenDoor_TreatsAsSuccess()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("pick w", h.LastSent);

        h.Line("The door is already open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void UseKey_OnAlreadyOpenDoor_TreatsAsSuccess()
    {
        using Harness h = new();
        h.Stats.Strength = 0;
        h.Stats.Picklocks = 0;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: false, keyItemId: 172, "walker", r => result = r);
        Assert.Equal("use black star key e", h.LastSent);

        h.Line("The door is already open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void OpenHitsLocked_WithKeyAvailable_SwingsToUseKey()
    {
        // Edge case: pick succeeded → open sent → server replies "is
        // locked" (the lock came back on, or pick was on a different
        // chamber). With a key, fall back rather than fail.
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 50, canBash: false, keyItemId: 172, "walker", r => result = r);

        h.Line("You successfully unlock the door.");      // pick OK
        Assert.Equal("open n", h.LastSent);
        h.Line("The door is locked.");                    // open says locked
        Assert.Equal("use black star key n", h.LastSent);
    }

    // A pick stands a resting character up, so the next try waits out a rest hold that
    // something else started.
    [Fact]
    public void RestHold_HoldsThePick_UntilTheRestIsOver()
    {
        using Harness h = new() { PicklocksOverBash = true };
        bool resting = true;
        h.Mgr.SetRestHold(() => resting);
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "loop", _ => { });
        Assert.Empty(h.AllSent);

        h.Mgr.NotifyRestHoldChanged();                  // still resting
        Assert.Empty(h.AllSent);

        resting = false;
        h.Mgr.NotifyRestHoldChanged();
        Assert.Equal("pick w", h.LastSent);
    }

    // A door someone else opens while our try is held still completes the request.
    [Fact]
    public void RestHold_DoorOpensMeanwhile_ReportsOpened()
    {
        using Harness h = new() { PicklocksOverBash = true };
        h.Mgr.SetRestHold(() => true);
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "loop", r => result = r);

        h.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Empty(h.AllSent);
    }

    // ----- the door isn't here ---------------------------------------

    // Report paradigm-20260924-053941: `bash se` at an exit that is no door drew
    // "Your command had no effect." sixteen times, each read as silence and re-bashed
    // on the watchdog. The line ends the request so the caller can re-plan.
    [Fact]
    public void Bash_NoEffect_EndsAsNotHere_AndStopsBashing()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 11, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("bash se", h.LastSent);

        h.Line("Your command had no effect.");

        Assert.IsType<DoorOpenResult.NotHere>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
        Assert.False(h.HasWatchdogArmed);
        Assert.Single(h.AllSent);
    }

    // A pick at an exit with no door draws the same line a bash does.
    [Fact]
    public void Pick_NoEffect_EndsAsNotHere()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("pick w", h.LastSent);

        h.Line("Your command had no effect.");

        Assert.IsType<DoorOpenResult.NotHere>(result);
        Assert.Single(h.AllSent);
    }

    // An open there is told so in its own words.
    [Fact]
    public void Open_NotADoorOrAGate_EndsAsNotHere()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        h.Line("You successfully unlocked the door.");
        Assert.Equal("open w", h.LastSent);

        h.Line("That is not a door or a gate!");

        Assert.IsType<DoorOpenResult.NotHere>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    // The generic line isn't an open's answer, and the door line isn't a bash's:
    // each is taken only for the verb that draws it.
    [Fact]
    public void EachLine_IsTakenOnlyForTheVerbThatDrawsIt()
    {
        using Harness opening = new() { PicklocksOverBash = true };
        DoorOpenResult? openResult = null;
        opening.Mgr.Enqueue(Direction.W, 0, canBash: true, keyItemId: 0, "walker", r => openResult = r);
        opening.Line("You successfully unlocked the door.");
        opening.Line("Your command had no effect.");
        Assert.Null(openResult);

        using Harness bashing = new();
        DoorOpenResult? bashResult = null;
        bashing.Mgr.Enqueue(Direction.SE, 11, canBash: true, keyItemId: 0, "walker", r => bashResult = r);
        bashing.Line("That is not a door or a gate!");
        Assert.Null(bashResult);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, bashing.Mgr.CurrentState);
    }

    // The caller re-plans from inside the reply and may ask for a door the same way
    // at once. That request has to start, not be dropped as a duplicate of the one
    // just abandoned (which left the caller waiting for good).
    [Fact]
    public void NotHere_ARequestMadeFromInsideTheReply_ForTheSameWay_IsStarted()
    {
        using Harness h = new();
        var results = new List<DoorOpenResult>();
        h.Mgr.Enqueue(Direction.SE, 11, canBash: true, keyItemId: 0, "walker", first =>
        {
            results.Add(first);
            h.Mgr.Enqueue(Direction.SE, 11, canBash: true, keyItemId: 0, "walker", results.Add);
        });
        Assert.Equal("bash se", h.LastSent);

        h.Line("Your command had no effect.");

        Assert.IsType<DoorOpenResult.NotHere>(Assert.Single(results));
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);
        Assert.Equal(2, h.AllSent.Count);               // the second request's own bash

        h.Line("Your command had no effect.");
        Assert.Equal(2, results.Count);                 // and it gets its answer too
    }

    // The same line answers any command the game refuses. With the door's own echo
    // ahead of it, it is the door's.
    [Fact]
    public void NoEffect_AfterTheBashEcho_IsTheDoors()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        h.Echoed("bash se");
        h.Line("Your command had no effect.");

        Assert.IsType<DoorOpenResult.NotHere>(result);
    }

    // With another command's echo ahead of it (a buff cast sent while the bash was
    // out), it says nothing about the door.
    [Fact]
    public void NoEffect_AfterAnotherCommandsEcho_LeavesTheBashWaiting()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        h.Echoed("tige");
        h.Line("Your command had no effect.");

        Assert.Null(result);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);

        h.Line("You bashed the door open.");
        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    // No door verb is on the wire while a bash waits out a rest, so the line belongs
    // to something else.
    [Fact]
    public void NoEffect_WhileNoDoorVerbAwaitsAReply_IsIgnored()
    {
        using Harness h = new() { BashRestNeeded = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBashRest, h.Mgr.CurrentState);

        h.Line("Your command had no effect.");

        Assert.Null(result);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBashRest, h.Mgr.CurrentState);
    }

    // The tracker confirmed another room while the bash was out: the next swing would
    // be at that room's exit, so the request ends instead.
    [Fact]
    public void LeftTheRoom_NextBashIsNotSent_EndsAsNotHere()
    {
        using Harness h = new();
        RoomKey? confirmed = DoorRoom;
        h.Mgr.SetConfirmedRoomProbe(() => confirmed);
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => result = r);
        Assert.Equal("bash se", h.LastSent);

        confirmed = NextRoom;
        h.Line("Your attempts to bash through fail!");

        Assert.IsType<DoorOpenResult.NotHere>(result);
        Assert.Single(h.AllSent);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    [Fact]
    public void LeftTheRoom_WatchdogExpiry_EndsAsNotHere_InsteadOfReBashing()
    {
        using Harness h = new();
        RoomKey? confirmed = DoorRoom;
        h.Mgr.SetConfirmedRoomProbe(() => confirmed);
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        confirmed = NextRoom;
        h.FireTimeout();

        Assert.IsType<DoorOpenResult.NotHere>(result);
        Assert.Single(h.AllSent);
        Assert.False(h.HasWatchdogArmed);
    }

    // An "opened" line read after the room changed is about some other door.
    [Fact]
    public void LeftTheRoom_OpenedLine_IsNotReportedAsOpened()
    {
        using Harness h = new();
        RoomKey? confirmed = DoorRoom;
        h.Mgr.SetConfirmedRoomProbe(() => confirmed);
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => result = r);

        confirmed = NextRoom;
        h.Line("You bashed the door open.");

        Assert.IsType<DoorOpenResult.NotHere>(result);
    }

    // The tracker merely losing its certainty (a move in flight, a suspect room) is
    // not a different room, and neither is a request started while it was unsure.
    [Fact]
    public void RoomUnsure_NowOrAtTheStart_KeepsBashing()
    {
        using Harness h = new();
        RoomKey? confirmed = DoorRoom;
        h.Mgr.SetConfirmedRoomProbe(() => confirmed);
        DoorOpenResult? first = null;
        h.Mgr.Enqueue(Direction.SE, 0, canBash: true, keyItemId: 0, "walker", r => first = r);

        confirmed = null;
        h.Line("Your attempts to bash through fail!");
        Assert.Null(first);
        Assert.Equal(new[] { "bash se", "bash se" }, h.AllSent);
        h.Line("You bashed the door open.");
        Assert.IsType<DoorOpenResult.Opened>(first);

        // Started unsure: a room confirmed later is no evidence of having left.
        DoorOpenResult? second = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, keyItemId: 0, "maze", r => second = r);
        confirmed = NextRoom;
        h.Line("Your attempts to bash through fail!");
        Assert.Null(second);
        Assert.Equal("bash n", h.LastSent);
        Assert.Equal(4, h.AllSent.Count);
    }
}
