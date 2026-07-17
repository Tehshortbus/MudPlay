using System.Text;
using Avalonia.Threading;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Map;

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
// spamming `rm` at the wire. The engine is paused for the round-trip by the
// gate, so the reported room reflects a stationary position — no stale answer.
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
    private readonly DispatcherTimer? _timeout;

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
        LogService? log = null)
        : this(router, tracker, gate, gameData, log, useTimer: true) { }

    internal ParadigmPositionResolver(
        MessageRouter router,
        RoomTracker tracker,
        EngineRecoveryGate gate,
        GameDataCache gameData,
        LogService? log,
        bool useTimer)
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

        if (useTimer)
        {
            // One-shot: armed on each request, stopped by the reply or by its
            // own Tick. DispatcherTimer fires on the UI thread, so the gate /
            // tracker mutations below stay single-threaded like every other
            // engine send.
            _timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = ResyncTimeout };
            _timeout.Tick += (_, _) => OnTimeout();
        }
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
    public bool TryRequestResync(string reason)
    {
        if (_gameData.ActiveRealm != RealmType.ParaMud) return false;
        if (_wireSender is null) return false;
        if (_inFlight) return true;   // already waiting on a reply — coalesce

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastRequestAtUtc < MinResyncInterval)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"resync throttled ({(now - _lastRequestAtUtc).TotalMilliseconds:F0}ms since last); reason: {reason}");
            return false;
        }

        _inFlight = true;
        _requestedAtUtc = now;
        _lastRequestAtUtc = now;
        _wireSender(Encoding.Latin1.GetBytes("rm\r"));
        _timeout?.Stop();
        _timeout?.Start();
        _log?.Log(LogSeverity.Info, LogSource, $"sent `rm` for authoritative resync; reason: {reason}");
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
        _timeout?.Stop();
        _inFlight = false;
        RoomKey key = new(map, room);
        _lastResolved = key;
        _log?.Log(LogSeverity.Info, LogSource,
            $"authoritative position resolved → {key} ({(solicited ? "rm resync" : "manual rm")})");

        // Hard-locate the tracker first (refuses + warns if the key isn't in the
        // active graph, leaving state untouched), then re-anchor the gate. The
        // gate re-checks graph presence and falls back to heuristic on a miss.
        _tracker.SetLocated(key);
        _gate.NoteAuthoritativePosition(key);
        PositionResolved?.Invoke(key);
    }

    private void OnTimeout()
    {
        _timeout?.Stop();
        if (!_inFlight) return;
        _inFlight = false;
        _log?.Log(LogSeverity.Warn, LogSource,
            $"`rm` resync timed out after {ResyncTimeout.TotalSeconds:F0}s with no Location: reply");
        ResyncFailed?.Invoke();
    }

    // ----- test seams ------------------------------------------------
    internal void FeedLocationForTests(int map, int room)
        => OnLocationObserved(new MatchResult(
            KnownPatterns.ParadigmLocation,
            default,
            new[] { map.ToString(), room.ToString() }));

    internal void FireTimeoutForTests() => OnTimeout();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _locationSub.Dispose();
        _timeout?.Stop();
    }
}
