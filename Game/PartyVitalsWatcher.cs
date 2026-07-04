using System.Collections.Specialized;
using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Bridges party-member HP% observations onto the
// MovementCoordinator.PartyVitalsGate. While any other observed party
// member's HP% sits below the Party-tab WaitIfMemberBelowPercent threshold
// the gate is asserted so the active movement engine (loop / Auto-Lair /
// walk-to) holds, giving the hurt member time to rest or be healed before
// the group moves on. Clears once every observed member has recovered past
// the threshold.
//
// Read-only on party state: the watcher only subscribes to
// PartyState.Members collection changes and each member's HpPercent property
// change. It never writes a PartyMember field — the single-writer invariant
// keeps PartyManager as the sole writer of HpPercent.
//
// Two guards keep the gate from false-tripping: (1) the self row is
// excluded — the local character's own HP gates are HealthManager's job;
// (2) members whose HP% hasn't been observed yet (HpPercent == 0, the
// just-joined / pre-@health state) don't count, so a fresh invite doesn't
// pause the group before we know its real vitals.
public sealed class PartyVitalsWatcher : IDisposable
{
    // Identifier surfaced in MovementCoordinator.History when the watcher
    // flips the PartyVitals gate.
    public const string AsserterName = "PartyVitalsWatcher";

    private readonly PartyState _party;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<PartySettings> _readSettings;
    private readonly LogService? _log;
    private readonly HashSet<PartyMember> _watched = new();
    private bool _gateAsserted;

    public PartyVitalsWatcher(
        PartyState party,
        MovementCoordinator coordinator,
        Func<PartySettings> readSettings,
        LogService? log = null)
    {
        _party = party;
        _coordinator = coordinator;
        _readSettings = readSettings;
        _log = log;

        _party.Members.CollectionChanged += OnMembersChanged;
        foreach (PartyMember m in _party.Members) Watch(m);
        Evaluate();
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (PartyMember m in e.OldItems) Unwatch(m);
        if (e.NewItems is not null)
            foreach (PartyMember m in e.NewItems) Watch(m);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (PartyMember m in _watched) m.PropertyChanged -= OnMemberPropertyChanged;
            _watched.Clear();
            foreach (PartyMember m in _party.Members) Watch(m);
        }
        Evaluate();
    }

    private void Watch(PartyMember m)
    {
        if (_watched.Add(m)) m.PropertyChanged += OnMemberPropertyChanged;
    }

    private void Unwatch(PartyMember m)
    {
        if (_watched.Remove(m)) m.PropertyChanged -= OnMemberPropertyChanged;
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PartyMember.HpPercent)) Evaluate();
    }

    // Recompute whether any other observed member is below threshold and
    // assert / clear the gate accordingly. Idempotent — only touches the
    // coordinator when the asserted state actually changes.
    public void Evaluate()
    {
        int threshold = _readSettings().WaitIfMemberBelowPercent;
        bool shouldHold = threshold > 0 && _party.IsInParty && AnyMemberHurt(threshold);

        if (shouldHold == _gateAsserted) return;
        _gateAsserted = shouldHold;

        if (shouldHold)
        {
            PartyMember? worst = LowestHurt(threshold);
            string reason = worst is null
                ? $"threshold={threshold}"
                : $"threshold={threshold} member={worst.Name} hp={worst.HpPercent}%";
            _coordinator.AssertGate(MovementCoordinator.PartyVitalsGate, AsserterName, reason);
            _log?.Info("Party", $"Holding — party member below {threshold}% HP.");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.PartyVitalsGate, AsserterName, "members recovered");
            _log?.Info("Party", "Party vitals recovered — resuming.");
        }
    }

    private bool AnyMemberHurt(int threshold)
    {
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (m.HpPercent > 0 && m.HpPercent < threshold) return true;
        }
        return false;
    }

    private PartyMember? LowestHurt(int threshold)
    {
        PartyMember? worst = null;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.HpPercent <= 0 || m.HpPercent >= threshold) continue;
            if (worst is null || m.HpPercent < worst.HpPercent) worst = m;
        }
        return worst;
    }

    public void Dispose()
    {
        _party.Members.CollectionChanged -= OnMembersChanged;
        foreach (PartyMember m in _watched) m.PropertyChanged -= OnMemberPropertyChanged;
        _watched.Clear();
    }
}
