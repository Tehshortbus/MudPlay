using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Map;

// Paradigm-only authoritative position re-sync. On a ParaMud realm the game
// answers `rm` with a "Location: <map>,<room>" line reporting the player's own
// true position (see GAME_MECHANICS.md). When the recovery gate suspects the
// heuristic tracker has drifted, it asks this resolver to fire `rm`; the reply
// hard-locates the tracker and re-anchors the gate, so navigation never falls
// through to the footprint backtrack / "Lost" dialog on Paradigm. Stock realms
// have no `rm`, so TryRequestResync no-ops (returns false) and the gate keeps
// its existing heuristic ladder.
//
// One request at a time: while an `rm` is in flight we ignore new requests and
// arm a short timeout. The reply (or the timeout) clears the in-flight state.
// A brief throttle after each request stops a rapid-fire mismatch storm from
// spamming `rm` at the wire. The gate pauses its engine for the round-trip, so
// the room it is told is a stationary one. A one-shot caller holds nothing, so a
// move can go out behind its `rm`; OnLocationObserved keeps that move when the
// answer only names the room it is leaving.
public sealed class ParadigmPositionResolver : IDisposable
{
    private const string LogSource = "ParadigmResync";

    // How long to wait for the Location: reply before giving up and letting the
    // gate fall back to the heuristic backtrack.
    private static readonly TimeSpan ResyncTimeout = TimeSpan.FromSeconds(3);

    // Minimum spacing between `rm` sends — a fresh mismatch inside this window
    // reuses the last request rather than firing another.
    private static readonly TimeSpan MinResyncInterval = TimeSpan.FromSeconds(2);

    private readonly RoomTracker _tracker;
    private readonly EngineRecoveryGate _gate;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly IDisposable _locationSub;
    // One-shot: armed on each request, cancelled by the reply or run out.
    private IDisposable? _timeout;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private Action<byte[]>? _wireSender;
    private bool _inFlight;
    private DateTimeOffset _requestedAtUtc;
    private DateTimeOffset _lastRequestAtUtc = DateTimeOffset.MinValue;
    private RoomKey? _lastResolved;
    private bool _disposed;

    // Fires when an in-flight `rm` request times out or resolves to a room the
    // active graph doesn't contain. The gate subscribes and falls back to its
    // heuristic recovery ladder.
    public event Action? ResyncFailed;

    // Fires with the authoritative RoomKey every time a `Location:` line is
    // parsed — solicited (recovery resync) or manual. Distinct from the tracker's
    // StateChanged, which also fires on ordinary move-confirms and blocked-move
    // refusals: this fires ONLY on an actual `rm` reply. The maze solver keys its
    // Paradigm relocalization off this so it advances on the true `rm` answer and
    // never on a same-second move-confirm that would race it (report 152718).
    public event Action<RoomKey>? PositionResolved;

    // ----- bug-report surface ----------------------------------------
    public bool Enabled => _gameData.ActiveRealm == RealmType.ParaMud;
    public bool RequestInFlight => _inFlight;
    public DateTimeOffset? LastRequestedAt => _inFlight ? _requestedAtUtc : (DateTimeOffset?)null;
    public RoomKey? LastResolved => _lastResolved;

    public ParadigmPositionResolver(
        MessageRouter router,
        RoomTracker tracker,
        EngineRecoveryGate gate,
        GameDataCache gameData,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(gameData);

        _tracker = tracker;
        _gate = gate;
        _gameData = gameData;
        _log = log;

        _locationSub = router.Subscribe(KnownPatterns.ParadigmLocation, OnLocationObserved);
        // The default timer fires on the UI thread, so the gate / tracker mutations
        // below stay single-threaded like every other engine send.
        _schedule = schedule ?? DispatcherTicks.Once;
    }

    // Bind the wire sender (main-window VM supplies the EngineSendGate-wrapped
    // SendUserInput). Without it the resolver still observes Location: lines but
    // can't send `rm`.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Ask the game for our authoritative position. Returns true when an `rm`
    // was (or already is) in flight — the caller should pause and wait for the
    // resolver to re-anchor. Returns false on stock realms, when throttled, or
    // when no wire sender is bound, so the caller keeps its heuristic path.
    //
    // force skips the anti-storm throttle (but still coalesces on an in-flight
    // request): a caller about to give up entirely — the recovery gate before it
    // escalates to the heuristic backtrack or pops the "Lost" dialog — would
    // rather spend one `rm` than fail out, even if a resync fired inside the
    // throttle window (issue: paradigm-20260902-223159, a Paradigm client went
    // "Lost — footprint exhausted" while `rm` could have relocated it).
    public bool TryRequestResync(string reason, bool force = false)
    {
        if (_gameData.ActiveRealm != RealmType.ParaMud) return false;
        if (_wireSender is null) return false;
        if (_inFlight) return true;   // already waiting on a reply — coalesce

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastRequestAtUtc < MinResyncInterval)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"resync throttled ({(now - _lastRequestAtUtc).TotalMilliseconds:F0}ms since last); reason: {reason}");
            return false;
        }

        _inFlight = true;
        _requestedAtUtc = now;
        _lastRequestAtUtc = now;
        _wireSender(Encoding.Latin1.GetBytes("rm\r"));
        _timeout?.Dispose();
        _timeout = _schedule(ResyncTimeout, OnTimeout);
        _log?.Log(LogSeverity.Info, LogSource, $"sent `rm` for authoritative resync; reason: {reason}");
        return true;
    }

    // One-shot convenience for a consumer that wants a single re-fix answer
    // rather than a standing event subscription — e.g. an @where reply that
    // needs the game's authoritative position before it can respond. Fires `rm`
    // via TryRequestResync; whichever of PositionResolved / ResyncFailed lands
    // first invokes the matching callback exactly once and detaches both. Returns
    // false when a re-fix can't be started (stock realm / no wire / throttled) so
    // the caller can fall back immediately. Runs on the UI thread like the rest of
    // the resolver, so the subscribe-then-detach needs no locking.
    public bool RequestResyncOnce(string reason, Action<RoomKey> onResolved, Action onFailed)
    {
        ArgumentNullException.ThrowIfNull(onResolved);
        ArgumentNullException.ThrowIfNull(onFailed);
        if (!TryRequestResync(reason)) return false;

        Action<RoomKey>? resolved = null;
        Action? failed = null;
        resolved = key =>
        {
            PositionResolved -= resolved;
            ResyncFailed -= failed;
            onResolved(key);
        };
        failed = () =>
        {
            PositionResolved -= resolved;
            ResyncFailed -= failed;
            onFailed();
        };
        PositionResolved += resolved;
        ResyncFailed += failed;
        return true;
    }

    private void OnLocationObserved(MatchResult match)
    {
        if (match.Groups.Count < 2
            || !int.TryParse(match.Groups[0], out int map)
            || !int.TryParse(match.Groups[1], out int room))
        {
            if (_inFlight)
                _log?.Log(LogSeverity.Warn, LogSource, $"Location: reply unparseable: '{match.Text}'");
            return;
        }

        // `rm` is authoritative no matter who fired it. A reply we asked for
        // (recovery resync) additionally clears the wait and re-anchors the gate;
        // an unsolicited one — the user typing `rm` by hand after a bonk lost the
        // heuristic position — still hard-locates the tracker so the manual `rm`
        // re-anchors too. NoteAuthoritativePosition no-ops unless the gate is
        // actually awaiting a resync, so it's safe on the unsolicited path.
        bool solicited = _inFlight;
        CancelTimeout();
        _inFlight = false;
        RoomKey key = new(map, room);
        _lastResolved = key;
        _log?.Log(LogSeverity.Info, LogSource,
            $"authoritative position resolved → {key} ({(solicited ? "rm resync" : "manual rm")})");

        // The game answers commands in the order they were sent, so the answer to an
        // `rm` we asked for BEFORE a move that is still unconfirmed names the room
        // that move is leaving. When that is the room the tracker already holds,
        // there is nothing to correct: locating would only drop the move it is
        // waiting on and leave it sure of a room the character is walking out of
        // (report paradigm-20260924-053941: a door's follow-up move went out behind
        // the `rm`, and the walk replanned from the room it had just left). An
        // answer naming any OTHER room still locates — the tracker was wrong.
        if (solicited
            && _tracker.State.Confidence == RoomConfidence.Pending
            && _tracker.LastMoveSentAt is { } moveSentAt && moveSentAt >= _requestedAtUtc
            && _tracker.State.CurrentRoom?.Key == key)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"{key} is the room a move sent after the `rm` is leaving — tracker already there; keeping the move in flight");
        }
        else
        {
            // Hard-locate the tracker (refuses + warns if the key isn't in the
            // active graph, leaving state untouched).
            _tracker.SetLocated(key);
        }

        // Re-anchor the gate. It re-checks graph presence and falls back to
        // heuristic on a miss.
        _gate.NoteAuthoritativePosition(key);
        PositionResolved?.Invoke(key);
    }

    private void OnTimeout()
    {
        _timeout = null;
        if (!_inFlight) return;
        _inFlight = false;
        _log?.Log(LogSeverity.Warn, LogSource,
            $"`rm` resync timed out after {ResyncTimeout.TotalSeconds:F0}s with no Location: reply");
        ResyncFailed?.Invoke();
    }

    private void CancelTimeout()
    {
        _timeout?.Dispose();
        _timeout = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _locationSub.Dispose();
        CancelTimeout();
    }
}
