using System.Collections.Generic;
using System.Linq;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Random-walk scheduler — picks a marked room at random and dispatches
/// the walker to it; on arrival picks another. Foundation for the
/// deterministic Auto-Lair scheduler (PR 7.18+) which will replace the
/// random pick with a respawn-timer-driven selection.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from MudProxy's <c>AutoWalkManager</c> auto-roam surface.
/// Differences:
/// <list type="bullet">
///   <item>Session-only state (matches MudProxy). Auto-Lair gets
///         persistence when it ships.</item>
///   <item>Uniform random selection from the marked set minus the
///         current room. Falls back to the whole set when current
///         is the only marked room.</item>
///   <item>Requires at least 2 marked rooms before <see cref="Start"/>
///         will dispatch.</item>
///   <item>Honours <see cref="MovementCoordinator"/> pause gates
///         transitively via the walker.</item>
///   <item>Path-failure retries via a 2-second cooldown — gives the
///         tracker a chance to settle before the next attempt.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AutoRoamManager : IDisposable
{
    private readonly AutoWalkManager _walker;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private readonly HashSet<RoomKey> _marked = new();
    private readonly Random _rng = new();
    private readonly System.Timers.Timer _retryTimer;

    public AutoRoamManager(AutoWalkManager walker, RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(tracker);
        _walker = walker;
        _tracker = tracker;
        _log = log;

        _retryTimer = new System.Timers.Timer(2000) { AutoReset = false };
        _retryTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(PickAndDispatchNextLeg);

        _walker.Event += OnWalkerEvent;
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _retryTimer.Dispose();
        _walker.Event -= OnWalkerEvent;
    }

    public bool IsActive { get; private set; }

    /// <summary>Read-only snapshot of marked rooms.</summary>
    public IReadOnlyCollection<RoomKey> Marked => _marked.ToArray();

    /// <summary>Fires after every mutation to <see cref="Marked"/>.</summary>
    public event Action? MarkedChanged;

    /// <summary>Fires when <see cref="IsActive"/> flips. Carries the new value.</summary>
    public event Action<bool>? ActiveChanged;

    public bool IsMarked(RoomKey key) => _marked.Contains(key);

    public void Mark(RoomKey key)
    {
        if (!_marked.Add(key)) return;
        _log?.Info("AutoRoam", $"marked {key}");
        MarkedChanged?.Invoke();
    }

    public void Unmark(RoomKey key)
    {
        if (!_marked.Remove(key)) return;
        _log?.Info("AutoRoam", $"unmarked {key}");
        MarkedChanged?.Invoke();
    }

    public void Toggle(RoomKey key)
    {
        if (_marked.Contains(key)) Unmark(key);
        else Mark(key);
    }

    public void Clear()
    {
        if (_marked.Count == 0) return;
        _marked.Clear();
        MarkedChanged?.Invoke();
    }

    /// <summary>
    /// Start the random walk. No-op when already running, when fewer
    /// than 2 rooms are marked, or when the tracker has no current
    /// room to start from.
    /// </summary>
    public bool Start()
    {
        if (IsActive) return true;
        if (_marked.Count < 2)
        {
            _log?.Warn("AutoRoam", $"need at least 2 marked rooms; have {_marked.Count}.");
            return false;
        }
        if (_tracker.State.CurrentRoom is null)
        {
            _log?.Warn("AutoRoam", "no current room — locate before starting auto-roam.");
            return false;
        }

        IsActive = true;
        ActiveChanged?.Invoke(true);
        _log?.Info("AutoRoam", $"start ({_marked.Count} marked rooms)");
        PickAndDispatchNextLeg();
        return true;
    }

    public void Stop(string reason = "user stop")
    {
        if (!IsActive) return;
        IsActive = false;
        _retryTimer.Stop();
        ActiveChanged?.Invoke(false);
        _log?.Info("AutoRoam", $"stop: {reason}");

        // Cancel any in-flight leg we own.
        if (_walker.State != WalkState.Idle) _walker.Stop("auto-roam stop");
    }

    // ----- internals -------------------------------------------------

    private void PickAndDispatchNextLeg()
    {
        if (!IsActive) return;
        if (_marked.Count == 0) { Stop("no marked rooms"); return; }
        if (_tracker.State.CurrentRoom is not { } current) { Stop("no current room"); return; }

        // Pick from marked \ {current}; fall back to full set when
        // current is the only marked room (just sit there happily —
        // shouldn't happen with the >=2 gate but is safe regardless).
        RoomKey[] candidates = _marked.Where(k => !k.Equals(current.Key)).ToArray();
        if (candidates.Length == 0) candidates = _marked.ToArray();
        if (candidates.Length == 0) return;

        RoomKey target = candidates[_rng.Next(candidates.Length)];
        _log?.Info("AutoRoam", $"next leg: {current.Key} → {target}");

        if (!_walker.WalkTo(target))
        {
            // No path — retry in 2s with a fresh pick.
            _log?.Warn("AutoRoam", $"path to {target} failed; retrying in 2s.");
            _retryTimer.Stop();
            _retryTimer.Start();
        }
    }

    private void OnWalkerEvent(WalkEvent evt)
    {
        if (!IsActive) return;

        switch (evt.Kind)
        {
            case WalkEventKind.Finished:
                // Leg complete — pick the next one.
                PickAndDispatchNextLeg();
                break;

            case WalkEventKind.Failed:
                // The walker failed mid-leg. Schedule a retry after
                // the tracker has a chance to settle.
                _log?.Warn("AutoRoam", $"walker failed: {evt.Detail}");
                _retryTimer.Stop();
                _retryTimer.Start();
                break;

            case WalkEventKind.Stopped:
                // Could be us stopping (Stop()) or the user manually
                // stopping the walker. If we're still IsActive after
                // the stop, the user did it — bail.
                if (IsActive)
                {
                    Stop("walker stopped externally");
                }
                break;
        }
    }
}
