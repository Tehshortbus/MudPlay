using System.Collections.Specialized;
using System.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

// Mirrors our OWN curable, non-movement ailments (poison / blindness / disease)
// onto the self row's party-window chip. The say-driven chip mirror
// (PartyAilmentTracker) only lights OTHER members' chips off their '.@X on'
// announcements — our own say echo is ignored there because our condition state
// is owned by ConditionTracker — so without this our self row never lit a poison
// / blind / disease chip even though "You feel ill." (etc.) and the `par` display
// both showed it. That was the reported bug: self poison invisible on the party
// window while every other member's poison showed fine.
//
// Confusion and held are handled by their own responders (SelfConfusionResponder
// / SelfHeldResponder) because they additionally assert a movement gate; these
// three carry no movement side-effect, so they only need the chip. This is the
// pure-chip sibling covering the remaining surfaced self ailments.
public sealed class SelfAilmentChipResponder : IDisposable
{
    private const string LogCategory = "Ailment";

    // The surfaced, non-movement self ailments. Confused / Held are owned by the
    // gated responders; poison / blind / disease have no movement effect, so a
    // chip mirror is all they need.
    private static readonly (MessageFlags Flag, string Label)[] Ailments =
    {
        (MessageFlags.Poisoned, "poison"),
        (MessageFlags.Blinded,  "blind"),
        (MessageFlags.Diseased, "disease"),
    };

    private readonly ConditionTracker _conditions;
    private readonly PartyManager _party;
    private readonly LogService? _log;
    private bool _disposed;

    public SelfAilmentChipResponder(
        ConditionTracker conditions,
        PartyManager party,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(party);
        _conditions = conditions;
        _party = party;
        _log = log;

        _conditions.PropertyChanged += OnConditionsChanged;
        _party.State.Members.CollectionChanged += OnMembersChanged;
    }

    private void OnConditionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConditionTracker.ActiveFlags))
            Evaluate();
    }

    // A self row appearing (party just formed) is exactly when we must stamp our
    // current conditions onto it — the same reason PartyManager re-syncs self HP/MP
    // on add. Without this a poison that landed while solo would never show once we
    // joined, because no ActiveFlags edge follows the join.
    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Evaluate();
    }

    // Reconcile each surfaced self ailment against ConditionTracker, writing only
    // where the self row's chip disagrees. Reads the current chip off the self row
    // itself (the owner of truth) rather than caching a private mirror, so a
    // rebuilt responder / re-added self row re-syncs for free. Public so tests can
    // drive it directly without a PropertyChanged round-trip.
    public void Evaluate()
    {
        PartyMember? self = FindSelf();
        if (self is null) return;
        MessageFlags active = _conditions.ActiveFlags;
        foreach ((MessageFlags flag, string label) in Ailments)
        {
            bool now = active.HasFlag(flag);
            if (now == ChipOf(self, flag)) continue;
            _party.SetMemberAilment(self.Name, flag, now);
            _log?.Info(LogCategory, $"self {label} chip {(now ? "set" : "cleared")}.");
        }
    }

    private PartyMember? FindSelf()
    {
        foreach (PartyMember m in _party.State.Members)
            if (m.IsSelf) return m;
        return null;
    }

    private static bool ChipOf(PartyMember m, MessageFlags flag) => flag switch
    {
        MessageFlags.Poisoned => m.Poisoned,
        MessageFlags.Blinded  => m.Blinded,
        MessageFlags.Diseased => m.Diseased,
        _ => false,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conditions.PropertyChanged -= OnConditionsChanged;
        _party.State.Members.CollectionChanged -= OnMembersChanged;
    }
}
