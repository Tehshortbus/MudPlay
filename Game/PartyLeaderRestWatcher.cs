using System.Collections.Specialized;
using System.ComponentModel;

namespace FujinTerm.Game;

// Bridges the party leader's rest / meditate posture onto a callback so a
// follower's HealthManager can opportunistically rest during the leader's
// downtime.
//
// PartyManager only flips the leader's Resting / Meditating flags on the
// 5-second `par` poll, and a standing-idle follower's own PlayerState may not
// change in between — so without this nudge the health engine wouldn't
// re-evaluate (and start resting) until its next prompt tick. Edge-triggered:
// the callback fires only when the leader's resting-or-meditating posture
// actually flips, so a non-leader member sitting down doesn't spuriously poke
// the engine.
//
// Read-only on party state — only subscribes to PartyState.Members collection
// changes and each member's property changes, never writing a PartyMember
// field, so PartyManager stays the single writer.
public sealed class PartyLeaderRestWatcher : IDisposable
{
    private readonly PartyState _party;
    private readonly Action _onLeaderRestChanged;
    private readonly HashSet<PartyMember> _watched = new();
    private bool _leaderResting;

    public PartyLeaderRestWatcher(PartyState party, Action onLeaderRestChanged)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(onLeaderRestChanged);
        _party = party;
        _onLeaderRestChanged = onLeaderRestChanged;

        _party.Members.CollectionChanged += OnMembersChanged;
        foreach (PartyMember m in _party.Members) Watch(m);
        _leaderResting = LeaderResting();
    }

    // True when we're a follower and the party leader is currently resting or
    // meditating. Live read — recomputed on each access.
    public bool LeaderIsResting => LeaderResting();

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
        Reevaluate();
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
        if (e.PropertyName is nameof(PartyMember.Resting)
                          or nameof(PartyMember.Meditating)
                          or nameof(PartyMember.IsLeader))
            Reevaluate();
    }

    private void Reevaluate()
    {
        bool now = LeaderResting();
        if (now == _leaderResting) return;
        _leaderResting = now;
        _onLeaderRestChanged();
    }

    private bool LeaderResting()
    {
        if (!_party.IsInParty || _party.SelfIsLeader) return false;
        foreach (PartyMember m in _party.Members)
            if (m.IsLeader) return m.Resting || m.Meditating;
        return false;
    }

    public void Dispose()
    {
        _party.Members.CollectionChanged -= OnMembersChanged;
        foreach (PartyMember m in _watched) m.PropertyChanged -= OnMemberPropertyChanged;
        _watched.Clear();
    }
}
