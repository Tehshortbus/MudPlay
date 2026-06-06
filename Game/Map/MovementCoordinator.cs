using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Map;

/// <summary>
/// Central pause-gate aggregator for the Phase 7 movement stack.
/// <see cref="AutoWalkManager"/> (walk-to), <c>LoopManager</c> (PR 7.8),
/// and <c>AutoLairScheduler</c> (PR 7.18+) all share this single
/// instance so a pause from any source (user button, @wait, health
/// auto-rest, stealth window, etc.) halts whichever movement engine
/// is active.
/// </summary>
/// <remarks>
/// <para>
/// Gates are named so the UI / log can surface "why are we paused?" —
/// the active reason is the gate name. Gates are tri-state via the
/// <see cref="AssertGate"/> / <see cref="ClearGate"/> pair; callers
/// (HealthManager, StealthManager, party @wait, the user pause button)
/// own the state of their own gate.
/// </para>
/// <para>
/// PR 7.7 ships the coordinator with one built-in gate name
/// (<c>"user"</c>); other gates plug in as their owning subsystems
/// land. Health / Stealth gates wire in Phase 13; @wait wires when
/// PartyManager exposes a parked-by-wait observable.
/// </para>
/// </remarks>
public sealed class MovementCoordinator
{
    /// <summary>Built-in gate name for the user's manual Pause button.</summary>
    public const string UserGate = "user";

    private readonly HashSet<string> _assertedGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when at least one gate is asserting pause.</summary>
    public bool IsPaused => _assertedGates.Count > 0;

    /// <summary>
    /// Read-only snapshot of currently-asserted gate names. Useful for
    /// the Navigation status strip: <c>"paused: user, @wait"</c>.
    /// </summary>
    public IReadOnlyCollection<string> AssertedGates => _assertedGates.ToArray();

    /// <summary>
    /// Fires after every transition that changes <see cref="IsPaused"/>
    /// from <c>false</c> to <c>true</c> or vice versa. Single-gate
    /// changes that don't flip the overall paused state do not fire
    /// (e.g. clearing <c>"@wait"</c> while <c>"user"</c> is still
    /// asserted).
    /// </summary>
    public event Action<bool>? PauseStateChanged;

    /// <summary>
    /// Assert <paramref name="gate"/>. Idempotent — re-asserting an
    /// already-asserted gate doesn't refire the event.
    /// </summary>
    public void AssertGate(string gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        bool wasPaused = IsPaused;
        if (!_assertedGates.Add(gate)) return;
        if (!wasPaused) PauseStateChanged?.Invoke(true);
    }

    /// <summary>
    /// Clear <paramref name="gate"/>. Idempotent — clearing a gate
    /// that wasn't asserted is a no-op.
    /// </summary>
    public void ClearGate(string gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        if (!_assertedGates.Remove(gate)) return;
        if (!IsPaused) PauseStateChanged?.Invoke(false);
    }
}
