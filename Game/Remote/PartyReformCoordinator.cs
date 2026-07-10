using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Remote;

// Leader-side reconnect party reform — the mirror of PartyRejoinCoordinator. That
// one rejoins a leader we were FOLLOWING; this re-collects the followers we were
// LEADING. When we drop (a nightly-cleanup reconnect in particular) the server
// dissolves our party — leadership doesn't survive a disconnect — so on the next
// login the roster is empty. A returning follower's @comeback would then be denied
// (they're no longer an active member and, once par reconciles, no longer in the
// disconnect grace map either), and the movement loop would sprint off the instant
// we're back in-game, stranding the party.
//
// The fix rides the machinery already built for a live follower drop. At the
// moment we drop we snapshot the given names of the followers we were leading
// (NoteDisconnected — PartyState is still intact then; par reconciliation after the
// reconnect wipes it, so the snapshot can't wait until reform time). On the first
// in-game room display after the reconnect we hand that snapshot to
// PartyManager.BeginLeaderReconnectReform, which rebases the reconnect grace window
// to now (re-authorising each follower's @comeback and their auto-invite-on-return)
// and fires MemberDisconnected per follower so PartyDisconnectMovementGate holds the
// loop for the wait window — resuming when they re-party or when the window elapses.
// The leader waits up to the wait period for the others to reconnect and reform the
// party, then carries on.
//
// One-shot per connect, snapshot-at-disconnect. Arm() opens the latch on every
// connect (alongside PartyRejoin.Arm); the reform only fires if a snapshot was
// taken (i.e. we were leading when we dropped). A solo or follower session never
// snapshots, so it stays quiet.
//
// Gated by the Auto-All kill switch like PartyRejoin — a manual-play character that
// silenced automation won't auto-reform. Read-only on party state beyond the
// explicit reform call it delegates to PartyManager, so it composes with every
// other party subsystem.
public sealed class PartyReformCoordinator : IDisposable
{
    public const string LogCategory = "PartyReform";

    private readonly PartyManager _party;
    private readonly Func<bool> _isAutoEnabled;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    // Given names of the followers we were leading when we last dropped, captured
    // at disconnect. Empty when we weren't leading or after the reform fires.
    private IReadOnlyList<string> _pendingReform = Array.Empty<string>();

    // One-shot reform latch — opened by Arm on connect, consumed on the first
    // in-game room display.
    private bool _armed;
    private bool _disposed;

    // The followers we'd re-collect on the next in-game room display, or empty when
    // there's no party to reform. Surfaced in the bug report's Party section.
    public IReadOnlyList<string> PendingReform => _pendingReform;

    public PartyReformCoordinator(
        MessageRouter router,
        PartyManager party,
        Func<bool>? isAutoEnabled = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        // Master auto-responses gate — mirrors PartyRejoin. Defaults to always-on
        // so tests behave without wiring it.
        _isAutoEnabled = isAutoEnabled ?? (() => true);
        _log = log;

        // First in-game room display after a connect is our fire signal, same as
        // PartyRejoin: it proves we're back in the realm.
        _subs.Add(router.Subscribe(KnownPatterns.RoomExits, OnRoomDisplayed));
    }

    // Snapshot the followers we're leading at the moment we drop. Called from the
    // Disconnected handler while PartyState still reflects the live party — par
    // reconciliation after the reconnect clears the roster, so a snapshot taken at
    // reform time would come back empty. Overwrites any prior snapshot; comes back
    // empty (no reform) when we weren't leading a party.
    public void NoteDisconnected()
    {
        _pendingReform = _party.SnapshotLedFollowers();
        if (_pendingReform.Count > 0)
            _log?.Info(LogCategory,
                $"Dropped while leading {_pendingReform.Count} follower(s): {string.Join(", ", _pendingReform)} — will reform on reconnect.");
    }

    // Open the one-shot reform latch. Called on every connect; the reform only
    // actually fires on the next in-game room display if a snapshot was taken.
    public void Arm() => _armed = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }

    private void OnRoomDisplayed(MatchResult _)
    {
        if (!_armed) return;
        _armed = false; // one-shot per connect
        if (_pendingReform.Count == 0) return;
        if (!_isAutoEnabled())
        {
            _log?.Info(LogCategory,
                $"Auto-responses off — not reforming party ({string.Join(", ", _pendingReform)}).");
            _pendingReform = Array.Empty<string>();
            return;
        }
        _log?.Info(LogCategory,
            $"Reconnected — reforming party: waiting for {string.Join(", ", _pendingReform)} to return.");
        _party.BeginLeaderReconnectReform(_pendingReform);
        _pendingReform = Array.Empty<string>();
    }
}
