using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Reacts to the local character dropping — HP falling to or below 0, the
// "mortally wounded" state — and slams three doors shut at once until HP climbs
// back positive:
//
//   1. EngineSendGate hold — a dropped character can't act. The game rejects
//      every action command with "You may not do that while you are mortally
//      wounded!", so every background engine's wire-send is pure noise (worse:
//      log spam and wasted round-trips) until we recover. The hold silences all
//      ~48 wrapped engines in one move. It's a NAMED hold so it composes with
//      the suicide-password hold rather than clobbering it — either can be up
//      independently and sends stay gated until both clear. The emergency
//      low-HP hangup survives this because HealthManager routes it through a
//      separate un-wrapped sender (SetHangupWireSender), so a dropped character
//      can still bail out.
//
//   2. MovementCoordinator.MortallyWoundedGate — a blanket engine gate stops
//      sends, but the walker / loop / auto-lair each also key off the
//      coordinator for a clean, visible pause reason ("why are we paused?" =
//      MortallyWounded) rather than looking silently wedged. Auto-clears on
//      recovery, unlike the death halt's UserGate which waits for a manual
//      resume — a drop is recoverable in place (aid + heal), a death is not.
//
//   3. PartyManager.NoteSelfDropped — dropping removes us from the party on the
//      game-engine side (a follower is dropped; a leader can't lead mortally
//      wounded). Being dragged afterwards is physical relocation, not
//      membership, so the tracked roster / leader / following flags go stale the
//      instant we drop. Clearing them stops PartyFollowerMovementGate from
//      holding movement forever on a party we're no longer in, and lets recovery
//      re-confirm membership only from a real follow / par signal.
//
// Keyed on PlayerState.Hp <= 0 rather than the "You drop to the ground!" line:
// the prompt-driven HP is the same signal HealthManager's bleeding-out logic
// rides, it fires on recovery too (the drop line has no symmetric "you get back
// up" counterpart we can reliably match), and it's realm-wording-independent.
// The MaxHp > 0 && HasPromptData guard skips the zero-on-zero prompt race before
// the first real status line lands (PromptParser writes Hp + MaxHp before
// flipping HasPromptData, but a producer could still momentarily race the order).
public sealed class PlayerDroppedGate : IDisposable
{
    // Surfaced in MovementCoordinator.History and the EngineSendGate hold set.
    public const string AsserterName = "PlayerDroppedGate";

    // EngineSendGate hold reason. Matches MortallyWoundedGate's semantics so the
    // two locks read the same in logs / diagnostics.
    public const string HoldReason = "MortallyWounded";

    // LogService category — rides the same [Health] rows as the HP machinery
    // that drives this transition.
    private const string LogCategory = "Health";

    private readonly PlayerState _state;
    private readonly EngineSendGate _gate;
    private readonly MovementCoordinator _coordinator;
    private readonly PartyManager _party;
    private readonly LogService? _log;
    private bool _disposed;

    // True while the local character is dropped (HP <= 0 with real prompt data).
    // Drives the EngineSendGate hold + MortallyWoundedGate + party-state wipe.
    public bool IsMortallyWounded { get; private set; }

    // Fires whenever IsMortallyWounded flips, so a status chip can refresh its
    // "mortally wounded — waiting to recover" flavour.
    public event Action? MortallyWoundedChanged;

    public PlayerDroppedGate(
        PlayerState state,
        EngineSendGate gate,
        MovementCoordinator coordinator,
        PartyManager party,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(party);
        _state = state;
        _gate = gate;
        _coordinator = coordinator;
        _party = party;
        _log = log;

        _state.PropertyChanged += OnStateChanged;
        // Cover the case where prompt data already reflects a drop when we wire
        // up (reconnect into a mortally-wounded body).
        Evaluate();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.HasPromptData):
                Evaluate();
                break;
        }
    }

    // Recompute the dropped state and fire the transition once on each edge.
    // Public so tests can drive it deterministically without a PropertyChanged.
    public void Evaluate()
    {
        bool dropped = _state.HasPromptData && _state.MaxHp > 0 && _state.Hp <= 0;
        if (dropped == IsMortallyWounded) return;

        if (dropped)
        {
            string reason = $"HP {_state.Hp}/{_state.MaxHp} <= 0 — mortally wounded";
            _gate.Hold(HoldReason);
            _coordinator.AssertGate(MovementCoordinator.MortallyWoundedGate, AsserterName, reason);
            // Drop removes us from the party game-side; correct the stale roster
            // so a leader/follower gate doesn't hold on a party we've left.
            _party.NoteSelfDropped();
            _log?.Info(LogCategory,
                $"Mortally wounded ({reason}) — engine sends held, movement paused, party state cleared.");
        }
        else
        {
            _gate.Release(HoldReason);
            _coordinator.ClearGate(MovementCoordinator.MortallyWoundedGate, AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} > 0 — recovered");
            _log?.Info(LogCategory,
                $"Recovered from mortal wound (HP {_state.Hp}/{_state.MaxHp}) — engines resume.");
        }

        IsMortallyWounded = dropped;
        MortallyWoundedChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
        // Don't leave a stuck hold / gate if we're torn down mid-drop (character
        // swap while mortally wounded).
        if (IsMortallyWounded)
        {
            _gate.Release(HoldReason);
            _coordinator.ClearGate(MovementCoordinator.MortallyWoundedGate, AsserterName,
                "disposed while mortally wounded");
        }
    }
}
