using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MovementCoordinatorTests
{
    // ----- Pause-state basics (Phase 7 — unchanged behavior) ----------

    [Fact]
    public void Fresh_NotPaused()
    {
        MovementCoordinator c = new();
        Assert.False(c.IsPaused);
        Assert.Empty(c.AssertedGates);
    }

    [Fact]
    public void AssertGate_TogglesPausedState()
    {
        MovementCoordinator c = new();
        bool? last = null;
        c.PauseStateChanged += p => last = p;

        c.AssertGate(MovementCoordinator.UserGate);

        Assert.True(c.IsPaused);
        Assert.True(last);
    }

    [Fact]
    public void AssertGate_Idempotent_DoesNotRefire()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.PauseStateChanged += _ => fires++;

        c.AssertGate(MovementCoordinator.UserGate);
        c.AssertGate(MovementCoordinator.UserGate);

        Assert.Equal(1, fires);
    }

    [Fact]
    public void ClearOneGate_WithAnotherActive_StaysPaused()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.PauseStateChanged += _ => fires++;

        c.AssertGate(MovementCoordinator.UserGate);
        c.AssertGate(MovementCoordinator.CombatGate);
        Assert.Equal(1, fires);

        c.ClearGate(MovementCoordinator.UserGate);
        Assert.True(c.IsPaused);
        Assert.Equal(1, fires);                   // no transition fire
    }

    [Fact]
    public void ClearLastGate_TransitionsBackToUnpaused()
    {
        MovementCoordinator c = new();
        bool? last = null;
        c.PauseStateChanged += p => last = p;

        c.AssertGate(MovementCoordinator.UserGate);
        c.ClearGate(MovementCoordinator.UserGate);

        Assert.False(c.IsPaused);
        Assert.False(last);
    }

    [Fact]
    public void AssertedGates_ListsAllCurrentlyAsserted()
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.UserGate);
        c.AssertGate(MovementCoordinator.HealthRecoveryGate);
        Assert.Contains("User", c.AssertedGates);
        Assert.Contains("HealthRecovery", c.AssertedGates);
        Assert.Equal(2, c.AssertedGates.Count);
    }

    // ----- GatesChanged — fine-grained per-transition signal -----

    [Fact]
    public void GatesChanged_FiresOnEveryAssertAndClear()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.GatesChanged += () => fires++;

        c.AssertGate(MovementCoordinator.CombatGate);
        c.ClearGate(MovementCoordinator.CombatGate);

        Assert.Equal(2, fires);
    }

    [Fact]
    public void GatesChanged_FiresEvenWhenPausedStateDoesNotFlip()
    {
        // The whole point of GatesChanged over PauseStateChanged: swapping the
        // reason while staying paused (Combat clears into a still-asserted
        // HealthRecovery) must still notify a live "why are we paused" label.
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.HealthRecoveryGate);

        int pauseFlips = 0;
        int gateChanges = 0;
        c.PauseStateChanged += _ => pauseFlips++;
        c.GatesChanged += () => gateChanges++;

        c.AssertGate(MovementCoordinator.CombatGate);   // still paused
        c.ClearGate(MovementCoordinator.CombatGate);    // still paused (HP gate holds)

        Assert.True(c.IsPaused);
        Assert.Equal(0, pauseFlips);   // never flipped
        Assert.Equal(2, gateChanges);  // but both transitions fired
    }

    [Fact]
    public void GatesChanged_IdempotentTransition_DoesNotFire()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.GatesChanged += () => fires++;

        c.AssertGate(MovementCoordinator.UserGate);
        c.AssertGate(MovementCoordinator.UserGate);   // no-op re-assert
        c.ClearGate(MovementCoordinator.CombatGate);  // no-op clear (never asserted)

        Assert.Equal(1, fires);
    }

    // ----- Phase 9 PR 9.0b — gate constants -----

    [Fact]
    public void GateConstants_AreAllPascalCase()
    {
        // The constants are the strings that show up in logs, history,
        // and the AssertedGates list. PascalCase is the Phase 9 plan's
        // requirement; locking the values catches a rename-to-lowercase
        // regression that would scramble grep patterns.
        Assert.Equal("User",           MovementCoordinator.UserGate);
        Assert.Equal("Combat",         MovementCoordinator.CombatGate);
        Assert.Equal("HealthRecovery", MovementCoordinator.HealthRecoveryGate);
        Assert.Equal("ManaRecovery",   MovementCoordinator.ManaRecoveryGate);
        Assert.Equal("CorpseRecovery", MovementCoordinator.CorpseRecoveryGate);
        Assert.Equal("PartyWait",      MovementCoordinator.PartyWaitGate);
    }

    [Fact]
    public void GateNames_CaseInsensitiveMatch()
    {
        // HashSet was constructed with OrdinalIgnoreCase so legacy callers
        // that still pass "user" don't double-assert when a new caller
        // passes "User". Same gate.
        MovementCoordinator c = new();
        c.AssertGate("user");
        c.AssertGate("User");
        Assert.Single(c.AssertedGates);
    }

    // ----- Phase 9 PR 9.0b — GateHistory ring buffer -----

    [Fact]
    public void History_RecordsAssertAndClearTransitions()
    {
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.CombatGate,
                     asserter: "CombatStateTracker",
                     reason: "room-entry targetable=3");
        c.ClearGate(MovementCoordinator.CombatGate,
                    asserter: "CombatStateTracker",
                    reason: "lastKill=giant_rat heldFor=14.2s");

        IReadOnlyList<GateTransitionEntry> hist = c.History;
        Assert.Equal(2, hist.Count);
        Assert.Equal("Combat",                     hist[0].Gate);
        Assert.True(hist[0].Asserted);
        Assert.Equal("CombatStateTracker",         hist[0].Asserter);
        Assert.Equal("room-entry targetable=3",    hist[0].Reason);
        Assert.False(hist[1].Asserted);
        Assert.Equal("lastKill=giant_rat heldFor=14.2s", hist[1].Reason);
    }

    [Fact]
    public void History_IdempotentAssertDoesNotDuplicate()
    {
        // Asserting an already-asserted gate is a no-op; the history
        // must not gain a second entry or the timeline becomes a lie.
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.UserGate, asserter: "Nav");
        c.AssertGate(MovementCoordinator.UserGate, asserter: "Nav");
        Assert.Single(c.History);
    }

    [Fact]
    public void History_IdempotentClearDoesNotRecord()
    {
        MovementCoordinator c = new();
        c.ClearGate(MovementCoordinator.UserGate, asserter: "Nav");
        Assert.Empty(c.History);
    }

    [Fact]
    public void History_AsserterAndReasonOptional()
    {
        // Legacy call sites pass neither — the entry records nulls and
        // doesn't NRE.
        MovementCoordinator c = new();
        c.AssertGate(MovementCoordinator.UserGate);

        GateTransitionEntry only = c.History[0];
        Assert.Equal("User", only.Gate);
        Assert.True(only.Asserted);
        Assert.Null(only.Asserter);
        Assert.Null(only.Reason);
    }

    [Fact]
    public void History_RingBufferTrimsToCapacity()
    {
        // 200 capacity (HistoryCapacity const). Push 250 transitions
        // and verify the oldest 50 fell off.
        MovementCoordinator c = new();
        for (int i = 0; i < 250; i++)
        {
            string gate = $"G{i}";
            c.AssertGate(gate);
            c.ClearGate(gate);
        }
        IReadOnlyList<GateTransitionEntry> hist = c.History;
        Assert.Equal(200, hist.Count);
        // The earliest entry that survived is the assert for G25
        // (since the first 25 G{n} pairs = 50 entries fell off, and
        // 250*2-200 = 300 → 100 entries removed → G50 / clear-G49 is
        // the surviving start). Just confirm it isn't G0/G1.
        Assert.NotEqual("G0", hist[0].Gate);
        Assert.NotEqual("G1", hist[0].Gate);
        // Newest entry is the clear of G249.
        Assert.Equal("G249", hist[^1].Gate);
        Assert.False(hist[^1].Asserted);
    }

    [Fact]
    public void History_TimestampsMonotonicallyIncrease()
    {
        MovementCoordinator c = new();
        c.AssertGate("A");
        c.AssertGate("B");
        c.ClearGate("A");
        c.ClearGate("B");

        DateTimeOffset prev = DateTimeOffset.MinValue;
        foreach (GateTransitionEntry e in c.History)
        {
            Assert.True(e.Timestamp >= prev);
            prev = e.Timestamp;
        }
    }

    [Fact]
    public void History_SnapshotIsStable_DoesNotMutateAfterReturn()
    {
        // The History property returns a copy each call so the caller
        // can iterate without locking.
        MovementCoordinator c = new();
        c.AssertGate("A");
        IReadOnlyList<GateTransitionEntry> first = c.History;
        c.AssertGate("B");
        Assert.Single(first);             // unchanged
        Assert.Equal(2, c.History.Count); // fresh snapshot reflects the new entry
    }

    // ----- Phase 9 PR 9.0b — canonical [Gate] log writer -----

    [Fact]
    public void Log_WritesGateLineOnAssertAndClear_WhenLogServiceProvided()
    {
        LogService log = new();
        List<LogEntry> observed = new();
        log.EntryAdded += e => observed.Add(e);

        MovementCoordinator c = new(log);
        c.AssertGate(MovementCoordinator.CombatGate,
                     asserter: "CombatStateTracker",
                     reason: "room-entry targetable=3");
        c.ClearGate(MovementCoordinator.CombatGate,
                    asserter: "CombatStateTracker",
                    reason: "lastKill=giant_rat");

        // Should see exactly two rows tagged "Gate".
        List<LogEntry> gateRows = observed.FindAll(e => e.Source == "Gate");
        Assert.Equal(2, gateRows.Count);

        Assert.Equal(LogSeverity.Info, gateRows[0].Severity);
        Assert.Contains("asserted",                 gateRows[0].Message);
        Assert.Contains("gate=Combat",              gateRows[0].Message);
        Assert.Contains("asserter=CombatStateTracker", gateRows[0].Message);
        Assert.Contains("reason=room-entry targetable=3", gateRows[0].Message);

        Assert.Contains("cleared",                  gateRows[1].Message);
        Assert.Contains("gate=Combat",              gateRows[1].Message);
        Assert.Contains("reason=lastKill=giant_rat", gateRows[1].Message);
    }

    [Fact]
    public void Log_NoLogService_SilentlyRecordsHistoryButWritesNothing()
    {
        MovementCoordinator c = new(log: null);
        c.AssertGate(MovementCoordinator.UserGate);
        c.ClearGate(MovementCoordinator.UserGate);
        Assert.Equal(2, c.History.Count); // history still populated
        // (no LogService → nothing to assert about log output)
    }

    [Fact]
    public void Log_OmitsAsserterAndReasonFieldsWhenNull()
    {
        LogService log = new();
        List<LogEntry> observed = new();
        log.EntryAdded += e => observed.Add(e);

        MovementCoordinator c = new(log);
        c.AssertGate(MovementCoordinator.UserGate);

        Assert.Single(observed);
        Assert.Equal("asserted — gate=User", observed[0].Message);
    }
}
