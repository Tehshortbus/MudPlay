using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// AutoSearchManager — a `search` won't run mid-combat, so the engine defers the
// `sea` past a fight and holds the walker via the Search gate until the room
// clears. The DispatcherTimer ticks (classify delay before a clear-room search,
// settle after a post-fight search) are Avalonia plumbing not exercised in
// headless xUnit; the internal OnClassifyElapsed / OnSettleElapsed stand in for
// those ticks so the state machine runs deterministically.
public sealed class AutoSearchManagerTests
{
    // The manager's classify and settle timers.
    private readonly ManualTimers _searchTimers = new();

    // What the manager put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    // A confirmed room key for OnRoomChanged; the search is now keyed by room.
    private static RoomKey Key(int room = 1) => new(1, room);

    private static bool SearchHeld(MovementCoordinator c) =>
        c.AssertedGates.Contains(MovementCoordinator.SearchGate);

    // ----- clear room (no fight): searches after the classify delay -----

    [Fact]
    public void ClearRoom_Enabled_SearchesAfterClassify()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());                 // arm — nothing sent yet
        Assert.Empty(_sent);
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);             // classify delay fires

        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));
    }

    [Fact]
    public void ClearRoom_Disabled_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Empty(_sent);
    }

    [Fact]
    public void ClearRoom_SearchesOnEveryEntry()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        for (int i = 0; i < 3; i++) { mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay); }

        Assert.Equal(3, _sent.Count);
        Assert.All(_sent, b => Assert.Equal("sea", Decode(b)));
    }

    [Fact]
    public void ClassifyElapsed_ReadsMasterGateLive()
    {
        bool enabled = false;
        var mgr = new AutoSearchManager(isEnabled: () => enabled, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);   // off — no send
        enabled = true;
        mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);   // on — one send

        Assert.Single(_sent);
    }

    [Fact]
    public void Send_ReachesBoundSink()
    {
        var sink = new List<byte[]>();
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(sink.Add);

        mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Single(sink);
        Assert.Equal("sea", Decode(sink[0]));
    }

    [Fact]
    public void DemandGate_ArmsSearch_WhenMasterOff()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));
    }

    [Fact]
    public void BothGatesOff_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => false, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key()); _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Empty(_sent);
    }

    // ----- settle hold only when a loot consumer is armed -----

    [Fact]
    public void ClearRoom_NoLootConsumer_ReleasesGateImmediately_NoSettleHold()
    {
        // report paradigm-20260818-060742: with the get engines off (nothing collects
        // what the search reveals) the settle window is dead time on every room — release
        // the walker the moment the `sea` goes out rather than idling a full settle for it.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);              // no get engine, no demand
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        Assert.True(SearchHeld(coord));       // held from entry so the sea fires in place
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);              // clear room → sea fires

        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));
        Assert.False(SearchHeld(coord));      // released immediately — no settle idle
    }

    [Fact]
    public void ClearRoom_GetEngineArmed_HoldsForSettle()
    {
        // A get engine is armed to collect the reveal, so the walker is held through the
        // settle window (until OnSettleElapsed) exactly as before.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasGetEngineArmed: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Single(_sent);
        Assert.True(SearchHeld(coord));       // held for the collection settle
        _searchTimers.RunDue(AutoSearchManager.SearchSettle);
        Assert.False(SearchHeld(coord));
    }

    // ----- reveal-in-flight window (stash-room collect guard keys off this) -----

    [Fact]
    public void RevealInFlight_TrueOnlyBetweenSeaAndSettle()
    {
        // The stash-room collect guard suppresses re-grabbing a just-hidden pile ONLY
        // while the `sea` reveal is round-tripping. False on entry (no sea yet), true
        // once the sea fires with a consumer to collect the reveal, false again once
        // the settle elapses.
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasGetEngineArmed: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        Assert.False(mgr.IsRevealInFlight);   // armed, but no sea sent yet
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);              // clear room → sea fires, settle armed
        Assert.True(mgr.IsRevealInFlight);    // reveal is round-tripping
        _searchTimers.RunDue(AutoSearchManager.SearchSettle);
        Assert.False(mgr.IsRevealInFlight);   // window closed
    }

    [Fact]
    public void RevealInFlight_FalseWithNoLootConsumer()
    {
        // With no get engine armed the settle window is never held (report
        // paradigm-20260818-060742), so there is nothing collecting the reveal and
        // nothing to suppress — the guard stays open.
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);   // no consumer
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);              // sea fires but releases immediately
        Assert.Single(_sent);
        Assert.False(mgr.IsRevealInFlight);
    }

    [Fact]
    public void RevealInFlight_ClearedOnRoomChange()
    {
        // Leaving the room ends any reveal window — a stale flag must never leak into
        // the next room's entry survey (the mis-attribution that dropped coin in the
        // room after a stash room, report paradigm-20260829-212158).
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasGetEngineArmed: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.True(mgr.IsRevealInFlight);
        mgr.OnRoomChanged(Key());             // moved on before settle elapsed
        Assert.False(mgr.IsRevealInFlight);
    }

    // ----- empty-search reactive release -----

    [Fact]
    public void RevealedNothing_ReleasesHoldWithoutWaitingSettle()
    {
        // An empty room's `sea` prints "Your search revealed nothing." — release the
        // walker the instant that lands rather than idling out the settle window, so
        // an empty transit room doesn't cost the full settle every step (report
        // paradigm-20260909-004947).
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasGetEngineArmed: () => true,   // settle would otherwise hold the walker
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);              // sea fires, settle holds
        Assert.True(SearchHeld(coord));
        Assert.True(mgr.IsRevealInFlight);

        mgr.NotifySearchRevealedNothing();    // empty result → release now, no settle wait
        Assert.False(SearchHeld(coord));
        Assert.False(mgr.IsRevealInFlight);
    }

    [Fact]
    public void RevealedNothing_NoOpWhenNoSearchInFlight()
    {
        // Outside our own reveal window (e.g. a manually typed `sea`), the empty-result
        // line must not release a Search gate we aren't holding.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(isEnabled: () => true, coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.NotifySearchRevealedNothing();    // nothing in flight
        Assert.False(SearchHeld(coord));      // no-op, no throw
    }

    // ----- movement-start: search the room the walk begins from -----

    [Fact]
    public void MovementStarting_SearchesStartRoom_NeverEntered()
    {
        // Auto-search armed while standing still (turned on in place, or a login
        // room) → the start room never got an entry search. Movement start arms +
        // searches it before the walker steps out (report paradigm-20260909-055045).
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(isEnabled: () => true, coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnMovementStarting(Key(58));      // arms + holds the walker
        Assert.True(SearchHeld(coord));
        Assert.Empty(_sent);   // nothing sent until classify

        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));
    }

    [Fact]
    public void MovementStarting_SkipsRoomAlreadySearchedOnArrival()
    {
        // Arrived here and searched on entry, then a new leg starts from the same
        // room — don't re-search it. This is what stops every loop leg re-searching
        // the room it departs.
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key(96));
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);              // entry search fires (1 sea)
        Assert.Single(_sent);

        mgr.OnMovementStarting(Key(96));      // leg departs the same room
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.Single(_sent);  // still one — no re-search
    }

    [Fact]
    public void MovementStarting_NoOpWhenDisabled()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(isEnabled: () => false, coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnMovementStarting(Key(58));
        Assert.False(SearchHeld(coord));      // not armed → no hold, no search
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.Empty(_sent);
    }

    [Fact]
    public void MovementStarting_NoOpWhenSearchAlreadyOwed()
    {
        // A search is already armed for the room (entry search classifying) — the
        // start hook must not double-arm it.
        var mgr = new AutoSearchManager(isEnabled: () => true, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key(58));           // arms the entry search
        mgr.OnMovementStarting(Key(58));      // must not re-arm
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);

        Assert.Single(_sent);  // exactly one search
    }

    [Fact]
    public void MovementStarting_NullRoom_NoOp()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(isEnabled: () => true, coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnMovementStarting(null);
        Assert.False(SearchHeld(coord));
    }

    // ----- fight in the room: defer + hold, fire on clear -----

    [Fact]
    public void Fight_DefersAndHolds_ThenSearchesOnClear()
    {
        var coord = new MovementCoordinator();
        bool hostiles = true;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            hasGetEngineArmed: () => true,   // a consumer is armed, so the settle window is held
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());          // arm
        mgr.OnRoomObserved();         // occupants → fight → defer + hold, no send
        Assert.Empty(_sent);
        Assert.True(SearchHeld(coord));

        // A stray classify tick during combat must not leak a mid-fight search.
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.Empty(_sent);

        hostiles = false;
        mgr.OnRoomObserved();         // room cleared → sea fires, gate held for settle
        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));
        Assert.True(SearchHeld(coord));

        _searchTimers.RunDue(AutoSearchManager.SearchSettle);        // settle → release
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void HostilePresent_ButAutoCombatOff_Searches_InsteadOfDeadlocking()
    {
        // A hostile is in the room but auto-attack is off, so nothing will fight or
        // clear it. Deferring the search here would hold the Search gate forever and
        // deadlock the walker (report -074607). The manager must search instead and
        // release the gate so pathing continues.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            isCombatEngaging: () => false,   // auto-combat off — no fight incoming
            hasGetEngineArmed: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());     // arm + assert the Search gate
        mgr.OnRoomObserved();         // hostile seen but not engaging → must NOT defer
        Assert.Empty(_sent);          // nothing yet: the classify timer sends it
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);      // classify fires the search
        Assert.Single(_sent);
        Assert.Equal("sea", Decode(_sent[0]));

        _searchTimers.RunDue(AutoSearchManager.SearchSettle);        // settle → release; walker proceeds
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void Fight_DoesNotSearchMidCombat_OnRepeatObservations()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();

        Assert.Empty(_sent);
        Assert.True(SearchHeld(coord));
    }

    [Fact]
    public void EmptyObservation_BeforeOccupants_HoldsButDoesNotSearch()
    {
        // Room entry holds the Search gate so a zero-dwell loop can't step out
        // before the classify-delayed `sea` fires (report stock-20260730-163244).
        // The empty room-entry observation (no hostiles, precedes the occupant
        // line) must NOT fire the search — the classify timer covers a genuinely
        // clear room, and a fight arriving a beat later still defers under the
        // same hold.
        var coord = new MovementCoordinator();
        bool hostiles = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        Assert.True(SearchHeld(coord));   // held from entry to search in place
        mgr.OnRoomObserved();             // empty wipe → nothing sent, still held
        Assert.Empty(_sent);
        Assert.True(SearchHeld(coord));

        hostiles = true;
        mgr.OnRoomObserved();             // occupants revealed → defer, still held
        Assert.True(SearchHeld(coord));
        Assert.Empty(_sent);
    }

    [Fact]
    public void SearchesOncePerRoom_NoResearchWhenFightArrivesAfterClearSearch()
    {
        var coord = new MovementCoordinator();
        bool hostiles = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);      // clear room → one sea, gate held for settle
        Assert.Single(_sent);

        // A monster wanders in after the clear-room search: must not re-arm or
        // fire a second search for this room.
        hostiles = true;
        mgr.OnRoomObserved();
        Assert.Single(_sent);

        hostiles = false;
        mgr.OnRoomObserved();         // that fight clears — still no second search
        Assert.Single(_sent);

        _searchTimers.RunDue(AutoSearchManager.SearchSettle);        // settle window ends → gate released
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void RoomChange_DiscardsHeldSearch_ReleasesGate()
    {
        var coord = new MovementCoordinator();
        bool enabled = true;
        var mgr = new AutoSearchManager(
            isEnabled: () => enabled,
            hasEngageableHostiles: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();         // deferred + held
        Assert.True(SearchHeld(coord));

        // Search disarmed for the next room so the release isn't masked by the
        // fresh room-entry hold; the old room's held search must still be dropped.
        enabled = false;
        mgr.OnRoomChanged(Key());          // moved on → discard + release, nothing new to hold
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void Dispose_ReleasesGate()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);
        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();
        Assert.True(SearchHeld(coord));

        mgr.Dispose();
        Assert.False(SearchHeld(coord));
    }

    // ----- reliability: queued-move skip + death reset (report -090736) ----------

    [Fact]
    public void QueuedMoves_SkipTransitRoomSearch_FiresOnceSettled()
    {
        // A manual n;e;n burst: transit rooms are already passed, so a `sea` would land
        // in whichever room the player stops in. While moves are queued the transit
        // search is skipped; once settled (queue empty) the current room searches once.
        bool queued = true;
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasQueuedMoves: () => queued,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key(1));       // transit room
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);         // moves still queued → skipped, gate released
        Assert.Empty(_sent);
        Assert.False(SearchHeld(coord));

        queued = false;                  // settled in the landing room
        mgr.OnRoomChanged(Key(2));
        _searchTimers.RunDue(AutoSearchManager.ClassifyDelay);
        Assert.Single(_sent);   // one search, in the room we stopped in
    }

    [Fact]
    public void NullRoom_OnDeath_ClearsDeferredSearch_NoLateFire()
    {
        // Death → respawn-pending sends a null-room change; a search deferred in the
        // room we died in must be cleared so the death-driven roster wipe can't fire a
        // `sea` for a room we've already left (report paradigm-20260820-090736 Face B).
        bool hostiles = true;
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord, schedule: _searchTimers.Arm);
        mgr.SetWireSender(_sent.Add);

        mgr.OnRoomChanged(Key(934));     // died-in room, fight present
        mgr.OnRoomObserved();            // deferred + held
        Assert.True(SearchHeld(coord));

        mgr.OnRoomChanged(null);         // death → respawn-pending → clear owed, release
        Assert.False(SearchHeld(coord));

        hostiles = false;                // the death-driven wipe (now no hostiles)…
        mgr.OnRoomObserved();
        Assert.Empty(_sent);   // …must NOT fire a stale search
    }
}
