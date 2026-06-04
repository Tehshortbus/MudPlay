using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// FSM coverage for <see cref="DoorOpenManager"/>. Drives the manager
/// through every transition by injecting fabricated server lines via
/// <see cref="MessageRouter.Dispatch"/> and inspecting the wire bytes
/// + terminal callback result.
/// </summary>
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
        public int MaxBash { get; set; } = 10;
        public int MaxPick { get; set; } = 10;
        public bool PicklocksOverBash { get; set; }

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Stats = new PlayerStats();
            Stats.Strength = 50;
            Stats.Picklocks = 50;
            Items[172] = "black star key";
            Items[1980] = "ancient brass key";
            Mgr = new DoorOpenManager(Router, Stats,
                () => MaxBash, () => MaxPick, () => PicklocksOverBash,
                itemNameLookup: id => Items.TryGetValue(id, out string? name) ? name : null);
            Mgr.SetWireSender(Sent.Add);
        }

        public void Line(string text)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, [], DateTimeOffset.UtcNow, false);
            Router.Dispatch(emitted);
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose() => Mgr.Dispose();
    }

    // ----- happy paths ----------------------------------------------

    [Fact]
    public void Bash_FirstAttempt_Succeeds_RepliesOpened()
    {
        using Harness h = new();
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, "walker", r => result = r);

        Assert.Equal("bash e", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingBash, h.Mgr.CurrentState);

        h.Line("With a roar, you bashed the door open!");

        Assert.IsType<DoorOpenResult.Opened>(result);
        Assert.Equal(DoorOpenManager.DoorState.Idle, h.Mgr.CurrentState);
    }

    [Fact]
    public void Pick_FirstAttempt_Succeeds_ThenOpen_ThenOpened()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.W, 0, canBash: true, "walker", r => result = r);

        Assert.Equal("pick w", h.LastSent);

        h.Line("You successfully unlock the door.");
        Assert.Equal("open w", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingOpen, h.Mgr.CurrentState);

        h.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Pick_DoorNotLocked_SkipsToOpenImmediately()
    {
        using Harness h = new() { PicklocksOverBash = true };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, "walker", r => result = r);

        h.Line("The door was not locked.");
        Assert.Equal("open n", h.LastSent);
        h.Line("The door is now open.");

        Assert.IsType<DoorOpenResult.Opened>(result);
    }

    [Fact]
    public void Bash_RetriesOnFailure_UpToCap_ThenFallsBackToPick()
    {
        using Harness h = new() { MaxBash = 2, MaxPick = 2 };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.N, 0, canBash: true, "walker", r => result = r);

        Assert.Equal("bash n", h.LastSent);

        h.Line("Your attempts to bash through fail.");
        Assert.Equal(2, h.Sent.Count);

        h.Line("Your attempts to bash through fail.");
        // Bash exhausted — manager falls back to pick.
        Assert.Equal("pick n", h.LastSent);
        Assert.Equal(DoorOpenManager.DoorState.WaitingPick, h.Mgr.CurrentState);
    }

    [Fact]
    public void BothVerbsExhausted_RepliesFailed()
    {
        using Harness h = new() { MaxBash = 1, MaxPick = 1 };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.E, 0, canBash: true, "walker", r => result = r);

        h.Line("Your attempts to bash through fail.");
        // Bash exhausted (1 of 1) → fall back to pick.
        Assert.Equal("pick e", h.LastSent);

        h.Line("Your lockpicking skill fails you.");
        // Pick also exhausted → terminal failure.
        Assert.IsType<DoorOpenResult.Failed>(result);
    }

    // ----- viability gates ------------------------------------------

    [Fact]
    public void UnbashablePickOnly_BashSettingPreferred_StillPicks()
    {
        // canBash=false + low picklocks meets the (0) requirement.
        // Manager should pick, not bash, regardless of preference.
        using Harness h = new() { PicklocksOverBash = false };
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.S, 0, canBash: false, "walker", r => result = r);

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
        h.Mgr.Enqueue(Direction.N, 100, canBash: true, "walker", r => result = r);

        Assert.IsType<DoorOpenResult.Failed>(result);
        Assert.Empty(h.Sent);
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
        h.Mgr.Enqueue(Direction.E, 250, canBash: true, "walker", r => result = r);

        Assert.Equal("pick e", h.LastSent);
    }

    // ----- queue / lifecycle ----------------------------------------

    [Fact]
    public void StopAll_AbortsCurrent_DrainsQueue_RepliesFailedToAll()
    {
        using Harness h = new();
        DoorOpenResult? a = null;
        DoorOpenResult? b = null;
        h.Mgr.Enqueue(Direction.N, 0, true, "walker", r => a = r);
        h.Mgr.Enqueue(Direction.S, 0, true, "walker", r => b = r);

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
        h.Mgr.Enqueue(Direction.N, 0, true, "walker", results.Add);
        h.Mgr.Enqueue(Direction.S, 0, true, "walker", results.Add);

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
        h.Mgr.Enqueue(dir, 0, true, "walker", _ => { });
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
    public void KeyedDoor_StatAltExhausted_FallsBackToKey()
    {
        // Player meets stats; bash tried first; bash exhausts → key.
        using Harness h = new() { MaxBash = 1, MaxPick = 1 };
        h.Stats.Strength = 100;
        h.Stats.Picklocks = 100;
        DoorOpenResult? result = null;
        h.Mgr.Enqueue(Direction.S, 50, canBash: true, keyItemId: 172, "walker", r => result = r);

        Assert.Equal("bash s", h.LastSent);
        h.Line("Your attempts to bash through fail.");
        Assert.Equal("pick s", h.LastSent);
        h.Line("Your lockpicking skill fails you.");
        // Bash + pick both exhausted → key fallback.
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
        Assert.IsType<DoorOpenResult.Failed>(result);
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
        h.Mgr.Enqueue(Direction.S, 0, true, "walker", r => result = r);
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
        h.Mgr.Enqueue(Direction.W, 0, true, "walker", r => result = r);
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
}
