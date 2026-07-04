using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// State machine that drives the auto-disarm flow for @trap <direction> remote
// commands. Owns the per-request search → disarm loop, the queue of pending
// requests, and the telepath-the-sender-on-completion contract.
//
// One request is in flight at a time. The state machine cycles
// Idle → Searching → DisarmPending → Done per direction; subsequent
// @trap <dir> calls queue FIFO. A @trap stop aborts whatever's in flight, drains
// the queue, telepaths each queued sender that their trap was cancelled, and
// returns to State.Idle.
//
// The Stats-skill gate (CanDisarm) lives in this manager so the handler can
// interrogate it before deciding whether to enqueue or send a denial reply. The
// handler also owns the channel-aware silence (Say/Gangpath when no skill →
// silent; Telepath → reply) since the channel context lives at handler dispatch
// time.
//
// The only stop conditions are the configurable attempt cap (MaxDisarmAttempts)
// and a successful disarm observation.
public sealed class TrapDisarmManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerStats _stats;
    private readonly LogService? _log;
    private readonly IDisposable _foundSub;
    private readonly IDisposable _noneSub;
    private readonly IDisposable _disarmedSub;
    private readonly WireSender _wire = new();
    private bool _disposed;

    // FIFO queue of pending trap requests (oldest at the front).
    private readonly Queue<TrapRequest> _queue = new();
    // The currently-in-flight request, or null when idle.
    private TrapRequest? _current;
    private State _state = State.Idle;
    private int _searchAttempts;
    private int _disarmAttempts;

    // Max sea <dir> attempts before giving up on the current request. Default 20;
    // pushed from Models.Profile.OtherSettings.MaxTrapSearchAttempts.
    public int MaxSearchAttempts { get; set; } = 20;

    // Max disarm trap <dir> attempts after a successful search before giving up.
    // Default 5; pushed from Models.Profile.OtherSettings.MaxTrapDisarmAttempts.
    public int MaxDisarmAttempts { get; set; } = 5;

    // True when the local character has the Traps skill (Stats.Traps > 0).
    public bool CanDisarm => _stats.Traps > 0;

    // Current state — exposed for tests + diagnostics.
    public State CurrentState => _state;

    // Current request's direction, or null when idle.
    public string? CurrentDirection => _current?.Direction;

    // Outstanding queue depth (excludes the in-flight request).
    public int QueueDepth => _queue.Count;

    public TrapDisarmManager(MessageRouter router, PlayerStats stats, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        _router = router;
        _stats  = stats;
        _log    = log;

        _foundSub    = _router.Subscribe(KnownPatterns.TrapFoundInSearch,   OnSearchFound);
        _noneSub     = _router.Subscribe(KnownPatterns.TrapNoneInSearch,    OnSearchNone);
        _disarmedSub = _router.Subscribe(KnownPatterns.TrapDisarmedSuccess, OnDisarmedSuccess);
    }

    // Bind the wire-sender. Same shape as the rest of the engine-side handlers —
    // MainWindowVM supplies the gate-wrapped SendUserInput.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _foundSub.Dispose();
        _noneSub.Dispose();
        _disarmedSub.Dispose();
    }

    // Queue a new @trap <direction> request. direction must already be normalised
    // to the short form ("n" / "ne" / "u" / etc.). reply is the per-request
    // channel-bound callback the handler captured at dispatch time; the manager
    // invokes it once on terminal state (success / max-attempts / stop).
    //
    // Same-direction duplicate while already in-flight or queued is silently
    // ignored — we're already on it; sending a second {Trap to the N disarmed.}
    // would be misleading.
    public void Enqueue(string direction, string sender, Action<string> reply)
    {
        if (string.IsNullOrEmpty(direction)) return;
        ArgumentNullException.ThrowIfNull(reply);

        // Same-direction duplicate while already in flight → ignore.
        if (_current is { } cur && cur.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Log(LogSeverity.Debug, "Trap",
                $"@trap {direction} from {sender} duplicates in-flight request; ignored.");
            return;
        }
        // Same-direction duplicate in the queue → ignore.
        foreach (TrapRequest q in _queue)
        {
            if (q.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Log(LogSeverity.Debug, "Trap",
                    $"@trap {direction} from {sender} duplicates queued request; ignored.");
                return;
            }
        }

        _queue.Enqueue(new TrapRequest(direction, sender, reply));
        _log?.Log(LogSeverity.Info, "Trap",
            $"@trap {direction} queued (sender={sender}, depth={_queue.Count}).");
        TryStartNext();
    }

    // Abort the current in-flight request (if any), drain the queue, and telepath
    // each pending sender that their trap was cancelled. The stop sender does NOT
    // receive a notification here — the handler that called Stop sends its own
    // {ok} ack to them.
    public void StopAll()
    {
        if (_current is { } cur)
        {
            cur.Reply("Trap flow stopped.");
            _current = null;
        }
        while (_queue.Count > 0)
        {
            TrapRequest q = _queue.Dequeue();
            q.Reply("Trap flow stopped.");
        }
        _state = State.Idle;
        _searchAttempts = 0;
        _disarmAttempts = 0;
        _log?.Log(LogSeverity.Info, "Trap", "Trap flow stopped — queue drained.");
    }

    // ----- State machine -------------------------------------------------

    private void TryStartNext()
    {
        if (_state != State.Idle) return;
        if (_queue.Count == 0) return;
        _current = _queue.Dequeue();
        _state = State.Searching;
        _searchAttempts = 0;
        _disarmAttempts = 0;
        SendSearch();
    }

    private void SendSearch()
    {
        if (_current is not { } cur) return;
        _searchAttempts++;
        _wire.Send($"sea {cur.Direction}");
        _log?.Log(LogSeverity.Info, "Trap",
            $"Searching {cur.Direction} (attempt {_searchAttempts}/{MaxSearchAttempts}).");
    }

    private void SendDisarm()
    {
        if (_current is not { } cur) return;
        _disarmAttempts++;
        _wire.Send($"disarm trap {cur.Direction}");
        _log?.Log(LogSeverity.Info, "Trap",
            $"Disarming {cur.Direction} (attempt {_disarmAttempts}/{MaxDisarmAttempts}).");
    }

    private void OnSearchNone(MatchResult result)
    {
        if (_state != State.Searching) return;
        if (_current is not { } cur) return;
        if (!MatchesCurrentDirection(result)) return;

        if (_searchAttempts >= MaxSearchAttempts)
        {
            cur.Reply($"Couldn't find trap to the {cur.Direction} ({_searchAttempts} attempts).");
            CompleteCurrent();
            return;
        }
        SendSearch();
    }

    private void OnSearchFound(MatchResult result)
    {
        if (_state != State.Searching) return;
        if (_current is null) return;
        if (!MatchesCurrentDirection(result)) return;

        _state = State.DisarmPending;
        SendDisarm();
    }

    private void OnDisarmedSuccess(MatchResult result)
    {
        if (_state != State.DisarmPending) return;
        if (_current is not { } cur) return;
        if (!MatchesCurrentDirection(result)) return;

        cur.Reply($"Trap to the {cur.Direction} disarmed.");
        CompleteCurrent();
    }

    // Compare the captured \w+ from a regex match against the current request's
    // direction. Both are normalised to short form before compare — game prints
    // "north" / "east" / "up" etc., we store the short form we sent on the wire.
    private bool MatchesCurrentDirection(MatchResult result)
    {
        if (_current is null) return false;
        if (result.Groups.Count == 0) return false;
        string? observed = NormaliseDirection(result.Groups[0]);
        return observed is not null
               && observed.Equals(_current.Direction, StringComparison.OrdinalIgnoreCase);
    }

    private void CompleteCurrent()
    {
        _current = null;
        _state = State.Idle;
        _searchAttempts = 0;
        _disarmAttempts = 0;
        TryStartNext();
    }

    // Normalise a direction token (long or short) to its canonical short form.
    // Returns null for unrecognised inputs so the handler can deny with a clear
    // "unknown direction" message instead of queueing a request that will never
    // resolve.
    public static string? NormaliseDirection(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return input.Trim().ToLowerInvariant() switch
        {
            "n"  or "north"     => "n",
            "s"  or "south"     => "s",
            "e"  or "east"      => "e",
            "w"  or "west"      => "w",
            "ne" or "northeast" => "ne",
            "nw" or "northwest" => "nw",
            "se" or "southeast" => "se",
            "sw" or "southwest" => "sw",
            "u"  or "up"        => "u",
            "d"  or "down"      => "d",
            _                   => null,
        };
    }

    // Phases of one in-flight trap request.
    public enum State
    {
        Idle,
        Searching,
        DisarmPending,
    }

    // One queued (or in-flight) trap request. Reply is the channel-bound callback
    // the handler captured from RemoteCommandContext at dispatch time — invoking
    // it later telepaths / says-back to the original sender on the same channel
    // they used.
    private sealed record TrapRequest(string Direction, string Sender, Action<string> Reply);
}
