using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One "Hide combat lines" box in Settings → Combat.
public sealed partial class CombatLineToggle : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty] private bool _isHidden;

    public CombatLineCategory Category { get; }
    public string Label { get; }
    public string Tip { get; }

    public CombatLineToggle(CombatLineCategory category, string label, string tip, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Category = category;
        Label = label;
        Tip = tip;
        _changed = changed;
    }

    partial void OnIsHiddenChanged(bool value) => _changed();
}

// A titled row of those boxes.
public sealed record CombatLineToggleGroup(string Title, IReadOnlyList<CombatLineToggle> Toggles);
