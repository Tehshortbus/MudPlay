using MudPlay.Services;

namespace MudPlay.Game.Map;

// Base auto-search engine — issues a room-wide `sea` to reveal concealed items,
// which then surface on the "You notice ... here." survey line and are picked up
// by AutoGetItemsManager / CashManager exactly as visible loot would be.
//
// Combat sequencing: a `search` won't run while you're fighting, so firing on
// bare room entry (as this used to) sent the `sea` into the middle of the attack
// announcements, where it was lost. The search now waits for the room to be clear
// of engageable hostiles:
//   - Entry with NO fight → search on entry as before, after a short classify
//     delay. Room entry fires before the "Also here:" occupant line is parsed, so
//     at the entry instant an empty room and a fight-about-to-start look
//     identical; the delay lets the occupants reveal first.
//   - Entry WITH a fight → the search is deferred and the Search movement gate is
//     held so the walker can't leave; when the room clears the `sea` fires and the
//     gate stays held a settle window so the revealed survey comes back and the
//     get engines collect it BEFORE the loop sneaks (pre-move hook) and steps on.
//
// Driven from two seams (wired in AppServices): RoomTracker.StateChanged
// (OnRoomChanged — resets per-room state) and RoomEntityClassifier.EntitiesObserved
// (OnRoomObserved — wired after the combat tracker so the hostile check is current).
//
// This is the whole-room scan, distinct from HiddenExitRevealManager's targeted
// sea <dir> retry loop. The two are complementary and never contend.
//
// Two independent gates arm the search: the persisted master toggle (AutoSearch)
// and a transient path-item demand gate. Either issues the search; the demand
// gate never mutates the persisted flag.
public sealed class AutoSearchManager : IDisposable
{
    // LogService category — [AutoSearch] rows per sent search.
    public const string LogCategory = "AutoSearch";

    // Brief arrival grace before the clear-room `sea` fires. RoomTracker confirms a
    // move on the "Obvious exits:" line, which the server sends AFTER "Also here:",
    // so the room's roster is already parsed by the time OnRoomChanged arms this — the
    // occupants don't need "revealing." What this window still guards is a hostile that
    // walks/spawns in a fraction of a second AFTER the room render completes (a
    // separate wire message): a fight that reveals within it cancels the timer via
    // OnRoomObserved and the defer path takes over, so the `sea` isn't sent into the
    // start of a fight (where the server drops it). Trimmed from a longer wait once the
    // roster-already-parsed timing was understood — the leftover was dead per-room
    // latency on a search-along-route walk (report paradigm-20260909-004947).
    internal static readonly TimeSpan ClassifyDelay = TimeSpan.FromMilliseconds(90);

    // Keep the Search gate held this long after the post-fight `sea` goes out,
    // bridging the search's server round-trip so the revealed "You notice" survey
    // lands and the get engines take over the hold (their Acquisition gate) before
    // this releases. The `sea` reply is just the command→reply latency (~150 ms, no
    // server-side delay) and the get path parses the survey immediately, so this
    // only has to outlast that round-trip plus a little parse margin.
    internal static readonly TimeSpan SearchSettle = TimeSpan.FromMilliseconds(200);

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isDemandActive;
    private readonly Func<bool> _hasEngageableHostiles;
    // Whether the client will ACTUALLY fight the hostile (auto-attack armed) —
    // distinct from _hasEngageableHostiles, which is a pure "is a hostile here"
    // probe. Deferring the search for combat only makes sense when a fight will
    // happen and clear the room; with auto-combat off, no attacks fly, the `sea`
    // wouldn't be lost, and holding just deadlocks the walker (report -074607).
    private readonly Func<bool> _isCombatEngaging;
    private readonly Func<bool> _hasGetEngineArmed;
    private readonly Func<bool> _hasQueuedMoves;
    // SneakGuard.Holds — a search ends a sneak (GAME_MECHANICS "Sneaking — commands,
    // equip order, and the sneak state machine", its What ends a sneak list).
    private Func<bool>? _sneakKept;

    public void SetSneakKeptProbe(Func<bool> sneakKept) => _sneakKept = sneakKept;
    private readonly MovementCoordinator? _coordinator;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private IDisposable? _classify;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _settle;

    // The room a search is owed for, keyed to its confirmed identity — armed on entry
    // while a gate is on, null when nothing is owed. Keying by room (not a bare bool)
    // means a search armed for one room can never fire after we've moved to another,
    // and a null-room (death) transition clears it rather than firing in the wrong
    // room (report paradigm-20260820-090736 Face B).
    private RoomKey? _owedFor;
    // The room the last `sea` actually went out for. Lets a movement-start search
    // (OnMovementStarting) skip a room we already searched on arrival — otherwise
    // every loop leg would re-search the room it starts from. Null until the first
    // search fires; never auto-reset (it just tracks "last searched", compared only
    // against the current room at movement start).
    private RoomKey? _lastSearchedFor;
    // A fight was seen this room — the search waits for it to clear and the Search
    // gate is held meanwhile.
    private bool _deferredForCombat;

    // For a bug report: the search owed, or null.
    public string? DescribePending() =>
        _owedFor is null && !_deferredForCombat ? null
        : (_owedFor is { } owed ? $"a search is owed for {owed}" : "no search owed")
          + (_deferredForCombat ? ", put off for combat" : string.Empty);
    private bool _gateAsserted;
    private bool _disposed;

    public AutoSearchManager(
        Func<bool> isEnabled,
        Func<bool>? isDemandActive = null,
        Func<bool>? hasEngageableHostiles = null,
        Func<bool>? isCombatEngaging = null,
        Func<bool>? hasGetEngineArmed = null,
        Func<bool>? hasQueuedMoves = null,
        MovementCoordinator? coordinator = null,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(isEnabled);
        _isEnabled = isEnabled;
        _isDemandActive = isDemandActive ?? (static () => false);
        _hasEngageableHostiles = hasEngageableHostiles ?? (static () => false);
        // Default true so an unwired manager keeps the defer-on-hostile
        // behaviour; AppServices wires the auto-attack-aware flag.
        _isCombatEngaging = isCombatEngaging ?? (static () => true);
        _hasGetEngineArmed = hasGetEngineArmed ?? (static () => false);
        _hasQueuedMoves = hasQueuedMoves ?? (static () => false);
        _coordinator = coordinator;
        _log = log;
    }

    // True from the moment a `sea` goes out until its settle window elapses — the
    // round-trip during which the surfaced "You notice" survey comes back. This is
    // the ONLY window in which a search re-exposes concealed coin/items, so the
    // stash-room collect guard keys off it: a pile re-revealed here is the one we
    // just hid (don't re-grab), whereas coin shown on plain room entry or a corpse
    // drop is visible loot to collect. The settle gate holds the walker meanwhile,
    // so the window can never straddle a room change (a stale flag can't leak into
    // the next room's entry survey). False when no loot consumer is armed — nothing
    // collects then, so there is nothing to suppress.
    public bool IsRevealInFlight => _settle is not null;

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    private bool ShouldSearch() => _isEnabled() || _isDemandActive();

    // Whether anything will collect what the search reveals: a get engine
    // (AutoGetItems / AutoGetCash) or an active path-item demand. When nothing is
    // armed the post-search settle hold has no hand-off target, so the walker is
    // released the moment the `sea` goes out rather than idling a full window for it.
    private bool HasLootConsumer() => _hasGetEngineArmed() || _isDemandActive();

    // Genuine room change. Reset per-room state, discard any pending / held search
    // from the room we left, and — when a search is armed — start the classify
    // delay that resolves clear-room-vs-fight.
    public void OnRoomChanged(RoomKey? key)
    {
        _classify?.Dispose();
        _classify = null;
        _settle?.Dispose();
        _settle = null;
        ReleaseGate("room changed");
        _deferredForCombat = false;
        // A null room (death → respawn-pending) leaves nothing to search AND must
        // clear the owed search — otherwise a search deferred in the room we died in
        // would fire on the death-driven roster wipe, in a room we've already left.
        if (key is null) { _owedFor = null; return; }
        _owedFor = ShouldSearch() ? key : null;
        if (_owedFor is not null)
        {
            // Hold the walker for THIS room while we classify + search. Without the
            // gate a zero-dwell loop steps out in the same synchronous dispatch as
            // the room-confirm — before the classify-delayed `sea` fires — so the
            // room is never searched in place (report stock-20260730-163244).
            // AutoSearch's RoomTracker.StateChanged handler is registered ahead of
            // the loop's, so this pauses the coordinator before the loop's
            // SendNextStep runs.
            AssertGate("room entered — holding to search");
            _classify?.Dispose();
            _classify = _schedule(ClassifyDelay, OnClassifyElapsed);
        }
    }

    // A walk / loop / auto-lair is about to send its first step from the room we're
    // standing in. Auto-search fires on room ENTRY, but this room was entered earlier
    // — before auto-search was armed, or at login — so it never got that entry search
    // (report paradigm-20260909-055045: turned auto-search on standing still, then
    // walked; the start room was skipped). Arm + search it now, before the walker
    // steps out, on the same hold-and-classify path a room entry uses. Deduped: a leg
    // that starts from a room we just searched on arrival (every loop hop past the
    // first) is a no-op, so this never double-searches. The caller passes a Confirmed
    // room key only.
    public void OnMovementStarting(RoomKey? currentRoom)
    {
        if (_disposed || currentRoom is null) return;
        if (!ShouldSearch()) return;
        // A search is already armed / deferred / in flight for this room — leave it.
        if (_owedFor is not null || _deferredForCombat) return;
        // Already searched this room this visit (arrived here and searched on entry).
        if (_lastSearchedFor is { } last && last.Equals(currentRoom.Value)) return;
        _owedFor = currentRoom;
        AssertGate("movement starting — search start room");
        _classify?.Dispose();
        _classify = _schedule(ClassifyDelay, OnClassifyElapsed);
    }

    // Each room-entity observation (wired after the combat tracker so the hostile
    // check is current). Holds the search while a fight is engageable and fires it
    // the moment the room clears.
    public void OnRoomObserved()
    {
        // Defer only when a hostile is present AND we'll actually fight it — with
        // auto-attack off we never clear the room, so holding here deadlocks the
        // walker (report -074607); fall through and let the classify timer search.
        if (_hasEngageableHostiles() && _isCombatEngaging())
        {
            // Fight in the room — defer the still-owed search past it and hold the
            // walker so it can't step out before we've searched the cleared room.
            // Only one search per room: once it has fired (_owedFor cleared), a fight
            // that wanders in later doesn't re-arm another.
            _classify?.Dispose();
            _classify = null;
            if (_owedFor is not null && !_deferredForCombat)
            {
                _deferredForCombat = true;
                AssertGate("hostiles present — search deferred");
            }
            return;
        }

        // No engageable hostiles. Only fire here if we were holding for a fight;
        // the plain empty room-entry observation (which precedes the occupant line)
        // is left for the classify timer so a genuinely clear room still searches.
        if (_deferredForCombat) FireSearch(postCombat: true);
    }

    // Classify delay elapsed with no fight seen — treat the room as clear and
    // search now. A fight that revealed in the window stopped this timer.
    private void OnClassifyElapsed()
    {
        _classify?.Dispose();
        _classify = null;
        if (_owedFor is null || _deferredForCombat) return;
        if (_hasEngageableHostiles() && _isCombatEngaging())
        {
            // A fight revealed right at the classify boundary — hand off to the
            // defer path. The Search gate we're already holding stays asserted and
            // is released after the post-fight `sea` settles.
            _deferredForCombat = true;
            return;
        }
        FireSearch(postCombat: false);
    }

    // Search settle elapsed — the revealed survey has had time to come back and the
    // get engines to take over the hold; release the Search gate.
    private void OnSettleElapsed()
    {
        _settle?.Dispose();
        _settle = null;
        ReleaseGate("search settle elapsed");
    }

    // Something else searched this room (the Stock spill sweep, which sends its own
    // `sea` at a trail stop). Recorded as searched, so the movement-start search
    // doesn't search it again as the next walk leaves.
    public void NoteSearchedByOther(RoomKey room)
    {
        if (!_disposed) _lastSearchedFor = room;
    }

    // The room-wide `sea` came back empty (KnownPatterns.SearchRevealedNothing, wired
    // in AppServices). There's nothing concealed to collect, so the settle's whole
    // job — bridging the reveal so the get engines can take over the hold — is moot:
    // release the walker at once rather than idling out the window. This is what keeps
    // an empty transit room from costing the full settle every step. Only while our
    // own search is awaiting its reveal (settle running); an empty result outside that
    // window (a manual search) is ignored. A fruitful search surfaces "You notice …
    // here." instead, which the get engines act on and the settle timer still covers.
    public void NotifySearchRevealedNothing()
    {
        if (_disposed || _settle is null) return;
        _settle?.Dispose();
        _settle = null;
        ReleaseGate("search revealed nothing");
    }

    private void FireSearch(bool postCombat)
    {
        _classify?.Dispose();
        _classify = null;

        // Moves still queued → the player hasn't settled in this room (a transit room
        // of an n;e;n burst). A `sea` only searches the server's current room, so it
        // would land in whichever room they stop in; skip this room's search entirely
        // and let the room they settle in arm + fire its own. Gated engine travel
        // drains the queue at each room, so every room still gets its one search.
        if (_hasQueuedMoves())
        {
            _owedFor = null;
            _deferredForCombat = false;
            _log?.Debug(LogCategory, "owed search skipped — moves still queued (transit room)");
            ReleaseGate("moves queued — skip transit-room search");
            return;
        }

        // Keeping a sneak here (sneaking past hostiles, or a backstab still owed): a
        // search would end it, so this room goes unsearched; a room we fight in gets
        // its post-combat search once it's cleared.
        if (_sneakKept?.Invoke() == true)
        {
            _owedFor = null;
            _deferredForCombat = false;
            _log?.Debug(LogCategory, "room search skipped — it would end the sneak we're keeping");
            ReleaseGate("keeping the sneak");
            return;
        }

        RoomKey? searching = _owedFor;
        _owedFor = null;
        _deferredForCombat = false;

        if (!ShouldSearch())   // toggle went off between arming and firing
        {
            ReleaseGate("search no longer armed");
            return;
        }

        _wire.Send("sea");
        // Remember what we just searched so a movement-start search (OnMovementStarting)
        // doesn't re-fire on the room it's about to leave (every loop leg starts from a
        // room we searched on arrival).
        _lastSearchedFor = searching;
        _log?.Debug(LogCategory, postCombat
            ? "sent 'sea' — room cleared of hostiles"
            : _isEnabled() ? "sent 'sea' on room entry"
                           : "sent 'sea' on room entry (path-item demand)");

        // Keep the walker held through the reveal round-trip so the get engines
        // collect what the search surfaces before the loop sneaks / steps on — but
        // ONLY while a get engine (or path-item demand) is actually armed to collect.
        // With them off, the settle window has no hand-off target and is just dead
        // time on every room (report paradigm-20260818-060742: auto-search made travel
        // ~30-50% slower even though the `sea` resolves instantly). Release the moment
        // the search is sent instead, so a bare "reveal the room" scan doesn't stall
        // the walk. Both the clear-room and post-fight paths reach here holding the
        // Search gate (asserted in OnRoomChanged / OnRoomObserved).
        if (HasLootConsumer())
        {
            _settle?.Dispose();
            _settle = _schedule(SearchSettle, OnSettleElapsed);
        }
        else
        {
            ReleaseGate("search sent — no loot consumer to settle for");
        }
    }

    private void AssertGate(string reason)
    {
        if (_gateAsserted || _coordinator is null) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.SearchGate, LogCategory, reason);
    }

    private void ReleaseGate(string reason)
    {
        if (!_gateAsserted || _coordinator is null) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.SearchGate, LogCategory, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classify?.Dispose();
        _classify = null;
        _settle?.Dispose();
        _settle = null;
        ReleaseGate("disposed");
    }
}
