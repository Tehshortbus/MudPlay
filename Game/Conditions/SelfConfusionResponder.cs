using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

// Handles the LOCAL side of our own confusion — the piece AilmentSyncEngine
// can't cover for a party leader (or a solo player).
//
// AilmentSyncEngine already announces '.@confused on' and telepaths the leader
// an @wait so the party pauses. But that @wait is eaten when we ARE the leader
// (PartyRestSync.CanSignal returns false — a leader has no one to ping), so a
// confused leader kept walking. It also never reflected our own confusion on our
// own party-window chip (the say-driven chip mirror only fires for OTHER
// members' announcements — our state is owned here).
//
// So on our own confusion this:
//   1. sets our own party-window Confused chip (SetMemberAilment on the self row),
//      independent of the settings gates — the chip reflects the fact we're
//      confused, whether or not we pause for it, and
//   2. asserts MovementCoordinator.ConfusionGate so our own walk / loop /
//      auto-lair holds while confused — the local analogue of the @wait a
//      confused follower sends the leader. Honours Ignore Confusion (the same
//      SpellsSettings gate that suppresses the @wait): checked → no pause.
//
// Both undo on clear. The announce stays in AilmentSyncEngine — this only owns
// the self-chip and the local movement hold.
public sealed class SelfConfusionResponder : IDisposable
{
    public const string AsserterName = "ConfusionMovementGate";
    private const string LogCategory = "Ailment";

    private readonly ConditionTracker _conditions;
    private readonly PartyManager _party;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly LogService? _log;

    private bool _wasConfused;
    private bool _gateAsserted;
    private bool _disposed;

    public SelfConfusionResponder(
        ConditionTracker conditions,
        PartyManager party,
        MovementCoordinator coordinator,
        Func<SpellsSettings> readSpells,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readSpells);
        _conditions = conditions;
        _party = party;
        _coordinator = coordinator;
        _readSpells = readSpells;
        _log = log;

        _conditions.PropertyChanged += OnConditionsChanged;
    }

    private void OnConditionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConditionTracker.ActiveFlags))
            Evaluate();
    }

    // Recompute the confused state and fire the transition once per edge. Public
    // so tests can drive it without a PropertyChanged round-trip.
    public void Evaluate()
    {
        bool confused = _conditions.IsConfused;
        if (confused == _wasConfused) return;
        _wasConfused = confused;

        if (confused)
        {
            SetSelfChip(true);
            if (!IgnoreConfusion())
            {
                AssertGate();
                _log?.Info(LogCategory, "Confused — self chip set, navigation held.");
            }
            else
            {
                _log?.Info(LogCategory, "Confused — self chip set (Ignore Confusion on, not pausing).");
            }
        }
        else
        {
            SetSelfChip(false);
            ClearGate();
            _log?.Info(LogCategory, "Confusion cleared — self chip cleared, navigation resumes.");
        }
    }

    // Reconcile the hold against the CURRENT Ignore Confusion setting while still
    // confused — the mirror of AilmentSyncEngine.ReevaluateWaits. Toggling the
    // gate mid-confusion would otherwise leave the onset-time decision latched
    // (a flip to "ignore" would strand the pause; a flip off would never place
    // it). No-op when not confused. Doesn't touch the chip.
    public void Reevaluate()
    {
        if (!_conditions.IsConfused) return;
        if (IgnoreConfusion()) ClearGate();
        else AssertGate();
    }

    private bool IgnoreConfusion() => _readSpells().IgnoreConfusion;

    private void SetSelfChip(bool active)
    {
        if (string.IsNullOrEmpty(_party.LocalCharacterName)) return;
        _party.SetMemberAilment(_party.LocalCharacterName, MessageFlags.Confused, active);
    }

    private void AssertGate()
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.ConfusionGate, AsserterName, "self confused");
    }

    private void ClearGate()
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.ConfusionGate, AsserterName, "confusion cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conditions.PropertyChanged -= OnConditionsChanged;
        // Don't leave a stuck gate if torn down mid-confusion (character swap).
        ClearGate();
    }
}
