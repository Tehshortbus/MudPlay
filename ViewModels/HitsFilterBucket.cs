using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One selectable Hits-You-% band in Monster Intel's filter dropdown — a
// contiguous [Lo, Hi] percentage range. Selecting any set of bands shows the
// monsters whose Hits You % falls in the union of them. The band SET is
// realm-dependent: the lowest a monster's attack can land is 8% on Stock and 2%
// on ParaMUD (CombatCalculator.GetHitMin), so the low bands differ by realm.
public sealed partial class HitsFilterBucket : ObservableObject
{
    public string Label { get; }
    public int Lo { get; }
    public int Hi { get; }

    [ObservableProperty] private bool _selected;

    public HitsFilterBucket(string label, int lo, int hi)
    {
        Label = label;
        Lo = lo;
        Hi = hi;
    }

    public bool Contains(int hitPercent) => hitPercent >= Lo && hitPercent <= Hi;
}
