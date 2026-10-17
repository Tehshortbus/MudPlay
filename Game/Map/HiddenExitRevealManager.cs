using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Map;

// Drives the walker's sea <dir> retry loop for searchable-hidden exits.
// Mirrors DoorOpenManager's shape — one request in flight, FIFO queue,
// single terminal HiddenSearchResult callback per request — but with a
// different revelation signal: the watcher subscribes to
// RoomTracker.StateChanged and inspects the new current room's exit map
// for the searched direction. When the direction appears, the exit is
// "revealed" and the manager fires Revealed.
//
// This is the targeted reveal loop, distinct from the no-arg-sea
// auto-search-room feature. The attempt cap reads live from
// Settings.Other.MaxHiddenSearchAttempts on each retry so the user can
// tune mid-session.
//
// The game won't search while you're blind — `sea` answers only "You are
// blind." — so a search waits out the blindness (OnBlindnessChanged) instead of
// burning attempts or hanging on a reply that never comes.
public sealed class HiddenExitRevealManager : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly MessageRouter? _router;
    private readonly IDisposable? _searchOkSub;
    private readonly IDisposable? _searchFailSub;
    private readonly IDisposable? _blindSub;
    private readonly Func<int> _maxAttemptsProvider;
    private readonly Func<bool> _isBlinded;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private bool _disposed;

    private readonly Queue<HiddenRequest> _queue = new();
    private HiddenRequest? _current;
    private int _attempts;
    private bool _heldForBlindness;
    private bool _heldForRest;
    // True while the movement engines hold for a rest or a meditate. Unset → never.
    private Func<bool>? _restHold;
    // The room the in-flight search started in (null when unknown). A confirmed move
    // to another room ends the search — see HiddenSearchResult.LeftRoom.
    private RoomKey? _searchRoom;

    // Direction of the in-flight request, or null when idle.
    public string? CurrentDirection => _current is { } cur
        ? DirectionShort(cur.Direction)
        : null;

    // Outstanding queue depth.
    public int QueueDepth => _queue.Count;

    // True when a search is in flight (sent sea, awaiting room obs).
    public bool IsBusy => _current is not null;

    // The in-flight search is waiting for blindness to clear.
    public bool HeldForBlindness => _heldForBlindness;

    // The in-flight search is waiting for a rest hold to end.
    public bool HeldForRest => _heldForRest;

    public HiddenExitRevealManager(
        RoomTracker tracker,
        Func<int> maxAttemptsProvider,
        MessageRouter? router = null,
        LogService? log = null,
        Func<bool>? isBlinded = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(maxAttemptsProvider);
        _tracker = tracker;
        _router = router;
        _maxAttemptsProvider = maxAttemptsProvider;
        _isBlinded = isBlinded ?? (static () => false);
        _log = log;
        _tracker.StateChanged += OnTrackerStateChanged;

        // Primary success/failure signal — server emits either
        // "You found an exit ..." (success) or "You notice nothing
        // different to the <dir>" (failure) immediately after a `sea`.
        // The tracker-based check is kept as a fallback (the server
        // sometimes ALSO redisplays the room post-search), but for the
        // typical case where there's no room redisplay these patterns
        // are the only signal the search actually completed. Without
        // them the manager would wait forever and the walker would
        // stall in the room — the live bug we caught.
        if (_router is not null)
        {
            _searchOkSub   = _router.Subscribe(KnownPatterns.UserSearchSucceeded, OnSearchSucceededPattern);
            _searchFailSub = _router.Subscribe(KnownPatterns.UserSearchFailed,    OnSearchFailedPattern);
            _blindSub      = _router.Subscribe(KnownPatterns.BlindMoveStarved,    OnBlindRefusal);
        }
    }

    // The blind flag changed: a search held for blindness goes out once it clears.
    public void OnBlindnessChanged()
    {
        if (!_heldForBlindness || _isBlinded()) return;
        _heldForBlindness = false;
        _log?.Info("Hidden", "can see again — resuming the held search.");
        SendSea();
    }

    // A search stands a resting character up (GAME_MECHANICS "What ends a rest"), so
    // the next `sea` waits out a rest hold instead of breaking the rest it would
    // only have to start again.
    public void SetRestHold(Func<bool> isHeld) => _restHold = isHeld;

    // The rest hold changed: a search that was waiting on it goes out once it's over.
    public void NotifyRestHoldChanged()
    {
        if (_disposed || !_heldForRest || _restHold?.Invoke() == true) return;
        _heldForRest = false;
        _log?.Info("Hidden", "rest over — resuming the held search.");
        SendSea();
    }

    // "You are blind." while our `sea` is out: the game refused the search. The
    // walker holds every move until the reveal answers, so no move of ours can be
    // what drew this line. Hold (the refused try doesn't count) until the blind
    // flag clears.
    private void OnBlindRefusal(MatchResult _)
    {
        if (_current is not { } cur || _heldForBlindness || _attempts == 0) return;
        _attempts--;
        HoldForBlindness(cur);
    }

    private void HoldForBlindness(HiddenRequest cur)
    {
        _heldForBlindness = true;
        _log?.Info("Hidden",
            $"reveal {DirectionShort(cur.Direction)} held — the game won't search while you're blind; waiting to see again.");
    }

    // Bind the wire-sender — same shape as the rest of the engine-side handlers.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _searchOkSub?.Dispose();
        _searchFailSub?.Dispose();
        _blindSub?.Dispose();
    }

    private void OnSearchSucceededPattern(MatchResult m)
    {
        if (_current is not { } cur) return;
        // The capture might be "down" / "downwards" / "northeast" /
        // etc. We accept any form that resolves to the in-flight
        // direction; mismatched directions (a separate search the user
        // typed) are ignored.
        if (m.Groups.Count > 0
            && TryParseDirectionWord(m.Groups[0]) is { } parsed
            && parsed != cur.Direction)
        {
            return;
        }
        _log?.Info("Hidden",
            $"reveal {DirectionShort(cur.Direction)} succeeded via search-pattern on attempt {_attempts}.");
        cur.Reply(HiddenSearchResult.Revealed.Instance);
        Reset();
    }

    private void OnSearchFailedPattern(MatchResult _)
    {
        if (_current is not { } cur) return;
        if (_attempts >= _maxAttemptsProvider())
        {
            cur.Reply(new HiddenSearchResult.Failed(
                $"exit {DirectionShort(cur.Direction)} never revealed after {_attempts} sea attempts"));
            Reset();
            return;
        }
        SendSea();
    }

    private static Direction? TryParseDirectionWord(string? word) => word?.ToLowerInvariant() switch
    {
        "n" or "north"           => Direction.N,
        "s" or "south"           => Direction.S,
        "e" or "east"            => Direction.E,
        "w" or "west"            => Direction.W,
        "ne" or "northeast"      => Direction.NE,
        "nw" or "northwest"      => Direction.NW,
        "se" or "southeast"      => Direction.SE,
        "sw" or "southwest"      => Direction.SW,
        "u" or "up" or "upwards"     => Direction.U,
        "d" or "down" or "downwards" => Direction.D,
        _ => null,
    };

    // Queue a hidden-reveal request. Callback fires once on terminal state.
    public void Enqueue(
        Direction direction,
        string sender,
        Action<HiddenSearchResult> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        _queue.Enqueue(new HiddenRequest(direction, sender, reply));
        _log?.Info("Hidden",
            $"reveal {DirectionShort(direction)} queued (sender={sender}, depth={_queue.Count}).");
        TryStartNext();
    }

    // Abort the in-flight request + drain the queue.
    public void StopAll()
    {
        if (_current is { } cur)
        {
            cur.Reply(new HiddenSearchResult.Failed("hidden search stopped"));
            _current = null;
        }
        _heldForBlindness = false;
        _heldForRest = false;
        while (_queue.Count > 0)
        {
            HiddenRequest q = _queue.Dequeue();
            q.Reply(new HiddenSearchResult.Failed("hidden search stopped"));
        }
        _attempts = 0;
        _log?.Info("Hidden", "Hidden-reveal flow stopped — queue drained.");
    }

    private void TryStartNext()
    {
        if (_current is not null) return;
        if (_queue.Count == 0) return;
        _current = _queue.Dequeue();
        _attempts = 0;
        _searchRoom = _tracker.State.CurrentRoom?.Key;
        SendSea();
    }

    private void SendSea()
    {
        if (_current is not { } cur) return;
        if (_isBlinded())
        {
            if (!_heldForBlindness) HoldForBlindness(cur);
            return;
        }
        if (_restHold?.Invoke() == true)
        {
            if (!_heldForRest)
                _log?.Info("Hidden",
                    $"reveal {DirectionShort(cur.Direction)} held — resting; searching once the rest is over.");
            _heldForRest = true;
            return;
        }
        _attempts++;
        _wire.Send($"sea {DirectionShort(cur.Direction)}");
        _log?.Info("Hidden",
            $"sea {DirectionShort(cur.Direction)} (attempt {_attempts}/{_maxAttemptsProvider()}).");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (_current is not { } cur) return;

        // We're somewhere else now (report paradigm-20260924-135311: a queued move
        // landed mid-search and 19 more `sea d` went out below the room that had the
        // exit). Stop, before the success check can match an exit in the WRONG room.
        Room? room = transition.NewRoom;
        if (_searchRoom is { } searched && room is not null && room.Key != searched
            && transition.NewConfidence == RoomConfidence.Confirmed)
        {
            _log?.Info("Hidden",
                $"reveal {DirectionShort(cur.Direction)} abandoned — left {searched} for {room.Key} mid-search.");
            cur.Reply(new HiddenSearchResult.LeftRoom(searched, room.Key));
            Reset();
            return;
        }

        // Check if the searched direction now appears in the
        // tracker's current room. The trigger is broad — any state
        // change while we're in-flight prompts a re-check, including
        // a "same room redisplay after sea".
        if (room is not null && room.Exits.ContainsKey(cur.Direction))
        {
            _log?.Info("Hidden",
                $"reveal {DirectionShort(cur.Direction)} succeeded on attempt {_attempts}.");
            cur.Reply(HiddenSearchResult.Revealed.Instance);
            Reset();
            return;
        }

        // Still not visible — retry or exhaust (a blind or rest hold waits instead).
        if (_heldForBlindness || _heldForRest) return;
        if (_attempts >= _maxAttemptsProvider())
        {
            cur.Reply(new HiddenSearchResult.Failed(
                $"exit {DirectionShort(cur.Direction)} never revealed after {_attempts} sea attempts"));
            Reset();
            return;
        }
        SendSea();
    }

    private void Reset()
    {
        _current = null;
        _attempts = 0;
        _searchRoom = null;
        _heldForBlindness = false;
        _heldForRest = false;
        TryStartNext();
    }

    private static string DirectionShort(Direction d) => d switch
    {
        Direction.N => "n",
        Direction.S => "s",
        Direction.E => "e",
        Direction.W => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U => "u",
        Direction.D => "d",
        _ => "?",
    };

    private sealed record HiddenRequest(
        Direction Direction,
        string Sender,
        Action<HiddenSearchResult> Reply);
}
