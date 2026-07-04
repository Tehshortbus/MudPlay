using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Live party-membership state. Members mirrors the in-game par table (one
// row per actively-following character including the local player);
// LeaderName tracks who's currently leading. Consumed by PartyWindow, the
// remote-command engine (@party <sub> whitelist gating), and any automation
// that needs to know who's grouped.
//
// PartyManager is the sole writer; the IL-scan test enforces this for the
// observable fields below. Members is an ObservableCollection the manager
// mutates directly (Add / Remove / Clear); consumers bind to it.
public sealed partial class PartyState : ObservableObject
{
    // The live party roster. Updated by PartyManager on follows-you /
    // stops-following / par observations. Empty when no party is active.
    // Order is insertion order from observation (typically leader-first
    // since the leader row is what triggers IsInParty).
    public ObservableCollection<PartyMember> Members { get; } = new();

    // Name of the current party leader, or null when no party is active.
    // Matches the row marked * in the par table.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string? _leaderName;

    // True once we've observed at least one party-membership signal
    // (follows-you / a non-empty par table). Goes back to false when the
    // last member is removed.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isInParty;

    // True if the locally connected character is the party leader.
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _selfIsLeader;
}
