using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Live party-membership state. <see cref="Members"/> mirrors the in-game
/// <c>par</c> table (one row per actively-following character including
/// the local player); <see cref="LeaderName"/> tracks who's currently
/// leading. Consumed by <c>PartyWindow</c> (PR 6.6), the Phase 6 remote-
/// command engine (<c>@party &lt;sub&gt;</c> whitelist gating in PR 6.2),
/// and any future automation that needs to know who's grouped.
/// </summary>
/// <remarks>
/// Ownership: <see cref="PartyManager"/> is the sole writer. The Phase 3
/// PR 3.5 IL-scan test enforces this for the
/// <see cref="ObservablePropertyAttribute"/> fields below. <see cref="Members"/>
/// is an <see cref="ObservableCollection{T}"/> the manager mutates
/// directly (Add / Remove / Clear); consumers bind to it.
/// </remarks>
public sealed partial class PartyState : ObservableObject
{
    /// <summary>
    /// The live party roster. Updated by <see cref="PartyManager"/> on
    /// follows-you / stops-following / <c>par</c> observations. Empty when
    /// no party is active. Order is insertion order from observation
    /// (typically leader-first since the leader row is what triggers
    /// <see cref="IsInParty"/>).
    /// </summary>
    public ObservableCollection<PartyMember> Members { get; } = new();

    /// <summary>
    /// Name of the current party leader, or <c>null</c> when no party is
    /// active. Matches the row marked <c>*</c> in the <c>par</c> table.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private string? _leaderName;

    /// <summary>
    /// True once we've observed at least one party-membership signal
    /// (follows-you / a non-empty <c>par</c> table). Goes back to false
    /// when the last member is removed.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _isInParty;

    /// <summary>True if the locally connected character is the party leader.</summary>
    [ObservableProperty] [field: Owner(typeof(PartyManager))] private bool _selfIsLeader;
}
