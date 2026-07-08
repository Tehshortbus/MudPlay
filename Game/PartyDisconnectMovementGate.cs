using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Leader-side hold that stops us sprinting off when a party follower drops
// connection. PartyManager fires MemberDisconnected (given name) whenever a
// follower disconnects while we lead; this asserts MovementCoordinator's
// MemberDisconnectGate so the active movement engine (loop / Auto-Lair /
// walk-to) pauses in place. The dropped member then has the reconnect grace
// window to come back and re-party — the same window the auto-invite-on-return
// flow uses (Settings → Party "If leading, wait only").
//
// Two release paths per pending member:
//   • They re-follow us (PartyManager.MemberFollowConfirmed fires their name) —
//     the returning member rejoined, so drop their hold immediately.
//   • The grace window elapses — a member who never came back can't strand the
//     party forever, so time them out and resume.
// The gate clears once the last pending member resolves either way.
//
// Read-only on party state (mirrors PartyWaitMovementGate / PartyVitalsWatcher):
// it only listens to PartyManager events and rides the shared
// MovementCoordinator, so it composes with every other pause source — clearing
// this gate leaves movement held if anything else is still asserting.
public sealed class PartyDisconnectMovementGate : IDisposable
{
    // Identifier surfaced in MovementCoordinator.History when the gate flips.
    public const string AsserterName = "PartyDisconnectMovementGate";

    private readonly PartyManager _party;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;

    // Pending dropped members keyed by given name → the moment they dropped.
    private readonly Dictionary<string, DateTime> _pending
        = new(StringComparer.OrdinalIgnoreCase);
    private bool _gateAsserted;

    // Overridable clock so tests drive the timeout deterministically.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // How long we hold for a dropped follower before giving up and resuming
    // (Settings → Party "If leading, wait only", mirrored from
    // PartySettings.IfLeadingWaitTotalSec by AppServices). Zero or negative
    // disables the timeout — the hold then persists until the member re-follows.
    public TimeSpan GraceWindow { get; set; } = TimeSpan.FromSeconds(90);

    private Avalonia.Threading.DispatcherTimer? _timer;

    public PartyDisconnectMovementGate(
        PartyManager party,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(coordinator);
        _party = party;
        _coordinator = coordinator;
        _log = log;

        _party.MemberDisconnected += OnMemberDisconnected;
        _party.MemberFollowConfirmed += OnMemberFollowConfirmed;
    }

    private void OnMemberDisconnected(string given)
    {
        if (string.IsNullOrWhiteSpace(given)) return;
        _pending[given] = NowProvider();
        _log?.Info("Party",
            $"Follower {given} dropped — holding for reconnect (up to {GraceWindow.TotalSeconds:0}s).");
        UpdateGate();
        StartTimer();
    }

    private void OnMemberFollowConfirmed(string name)
    {
        // MemberFollowConfirmed fires for every join, not just reconnects —
        // only act when the joiner is one of our pending drops. Match on the
        // given-name token so a long-form "Suijin WuzHere" follow confirms a
        // short-form "Suijin" pending entry.
        string given = GivenNameOf(name);
        if (!_pending.Remove(given)) return;
        _log?.Info("Party", $"Follower {given} reconnected and re-partied — resuming.");
        UpdateGate();
    }

    // Assert while any member is pending, clear when none remain. Idempotent —
    // only touches the coordinator on the held-state flip.
    private void UpdateGate()
    {
        bool shouldHold = _pending.Count > 0;
        if (shouldHold == _gateAsserted) return;
        _gateAsserted = shouldHold;
        if (shouldHold)
        {
            _coordinator.AssertGate(MovementCoordinator.MemberDisconnectGate, AsserterName,
                $"members={string.Join(", ", _pending.Keys)}");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.MemberDisconnectGate, AsserterName,
                "reconnected / timed out");
            StopTimer();
        }
    }

    private void StartTimer()
    {
        if (GraceWindow <= TimeSpan.Zero) return;
        if (_timer is not null) return;
        _timer = new Avalonia.Threading.DispatcherTimer(
            interval: TimeSpan.FromSeconds(1),
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => Tick());
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    // Test seam — one expiry sweep without a real timer.
    internal void TickForTests() => Tick();

    private void Tick()
    {
        if (_pending.Count == 0 || GraceWindow <= TimeSpan.Zero) { StopTimer(); return; }
        DateTime now = NowProvider();
        List<string>? expired = null;
        foreach (KeyValuePair<string, DateTime> kv in _pending)
        {
            if (now - kv.Value < GraceWindow) continue;
            (expired ??= new()).Add(kv.Key);
        }
        if (expired is null) return;
        foreach (string given in expired)
        {
            _pending.Remove(given);
            _log?.Info("Party",
                $"Reconnect wait for {given} expired after {GraceWindow.TotalSeconds:0}s — resuming.");
        }
        UpdateGate();
    }

    // First whitespace-delimited token — mirrors PartyManager.GivenNameOf so a
    // long-form follow-confirmation matches a short-form pending entry.
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    public void Dispose()
    {
        _party.MemberDisconnected -= OnMemberDisconnected;
        _party.MemberFollowConfirmed -= OnMemberFollowConfirmed;
        StopTimer();
        if (_gateAsserted)
        {
            _coordinator.ClearGate(MovementCoordinator.MemberDisconnectGate, AsserterName, "disposed");
            _gateAsserted = false;
        }
    }
}
