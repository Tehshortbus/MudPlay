using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Settings;

// One reorderable row in a PriorityRankingViewModel list. Rank is simply the
// row's 1-based position in the list — the list always renders top-to-bottom
// in priority order, so there are never duplicate ranks. Key is the stable
// identifier the host VM maps back to its DTO field on save; Label and Tip are
// display-only.
public sealed partial class PriorityItemViewModel : ObservableObject
{
    public PriorityItemViewModel(string key, string label, string? tip = null)
    {
        Key = key;
        Label = label;
        Tip = tip;
    }

    // Stable identifier the host VM uses to resolve this row's rank back onto
    // a specific settings field.
    public string Key { get; }

    // Row label shown to the user.
    public string Label { get; }

    // Optional tooltip describing what this priority slot governs.
    public string? Tip { get; }

    // 1-based position in the list — the priority number shown.
    [ObservableProperty] private int _rank;

    // False for the top row (can't move earlier).
    [ObservableProperty] private bool _canMoveUp;

    // False for the bottom row (can't move later).
    [ObservableProperty] private bool _canMoveDown;
}
