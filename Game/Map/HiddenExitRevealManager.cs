using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Map;

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
public sealed class HiddenExitRevealManager : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly MessageRouter? _router;
    private readonly IDisposable? _searchOkSub;
    private readonly IDisposable? _searchFailSub;
    private readonly Func<int> _maxAttemptsProvider;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();
    private bool _disposed;

    private readonly Queue<HiddenRequest> _queue = new();
    private HiddenRequest? _current;
    private int _attempts;

    // Direction of the in-flight request, or null when idle.
    public string? CurrentDirection => _current is { } cur
        ? DirectionShort(cur.Direction)
        : null;

    // Outstanding queue depth.
    public int QueueDepth => _queue.Count;

    // True when a search is in flight (sent sea, awaiting room obs).
    public bool IsBusy => _current is not null;

    public HiddenExitRevealManager(
        RoomTracker tracker,
        Func<int> maxAttemptsProvider,
        MessageRouter? router = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(maxAttemptsProvider);
        _tracker = tracker;
        _router = router;
        _maxAttemptsProvider = maxAttemptsProvider;
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
        }
    }

    // Bind the wire-sender — same shape as the rest of the engine-side handlers.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _searchOkSub?.Dispose();
        _searchFailSub?.Dispose();
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
        SendSea();
    }

    private void SendSea()
    {
        if (_current is not { } cur) return;
        _attempts++;
        _wire.Send($"sea {DirectionShort(cur.Direction)}");
        _log?.Info("Hidden",
            $"sea {DirectionShort(cur.Direction)} (attempt {_attempts}/{_maxAttemptsProvider()}).");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (_current is not { } cur) return;

        // Check if the searched direction now appears in the
        // tracker's current room. The trigger is broad — any state
        // change while we're in-flight prompts a re-check, including
        // a "same room redisplay after sea".
        Room? room = transition.NewRoom;
        if (room is not null && room.Exits.ContainsKey(cur.Direction))
        {
            _log?.Info("Hidden",
                $"reveal {DirectionShort(cur.Direction)} succeeded on attempt {_attempts}.");
            cur.Reply(HiddenSearchResult.Revealed.Instance);
            Reset();
            return;
        }

        // Still not visible — retry or exhaust.
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
