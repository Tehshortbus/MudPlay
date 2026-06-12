using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One reorderable row in a <see cref="PriorityRankingViewModel"/> list. The
/// row's <see cref="Rank"/> is simply its 1-based position in the list — the
/// list always renders top-to-bottom in priority order, so there are never
/// duplicate ranks. <see cref="Key"/> is the stable identifier the host VM
/// maps back to its DTO field on save; <see cref="Label"/> and
/// <see cref="Tip"/> are display-only.
/// </summary>
public sealed partial class PriorityItemViewModel : ObservableObject
{
    public PriorityItemViewModel(string key, string label, string? tip = null)
    {
        Key = key;
        Label = label;
        Tip = tip;
    }

    /// <summary>Stable identifier the host VM uses to resolve this row's
    /// rank back onto a specific settings field.</summary>
    public string Key { get; }

    /// <summary>Row label shown to the user.</summary>
    public string Label { get; }

    /// <summary>Optional tooltip describing what this priority slot governs.</summary>
    public string? Tip { get; }

    /// <summary>1-based position in the list — the priority number shown.</summary>
    [ObservableProperty] private int _rank;

    /// <summary>False for the top row (can't move earlier).</summary>
    [ObservableProperty] private bool _canMoveUp;

    /// <summary>False for the bottom row (can't move later).</summary>
    [ObservableProperty] private bool _canMoveDown;
}
