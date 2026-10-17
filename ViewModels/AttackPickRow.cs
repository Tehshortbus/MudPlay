using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels;

// One row in Monster Intel's "Edit Attacks" picker — a single attack the
// character can use (a melee attack type, or an obtained attack spell), or the
// leading "fastest of all" basis row. The two toggles are independent: Shown gates
// whether the attack appears in the Your Matchup panel; IsRoundsAttack is a radio
// (one across the whole picker) picking what fills the master list's "Est. Rounds
// to Kill" column. Key is the stable persistence id ("melee:<Type>" /
// "spell:<Short>" / "best"); the VM owns the hidden-set + selected-key state and
// reacts to these via PropertyChanged.
public sealed partial class AttackPickRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }

    // The basis row is a rounds-to-kill option only — it isn't an attack, so it has
    // no Your Matchup visibility to toggle. Its checkbox stays in the layout (hidden)
    // so the radios still line up down the column.
    public bool BasisOnly { get; }
    public bool CanToggleShown => !BasisOnly;
    public double ShownToggleOpacity => BasisOnly ? 0 : 1;

    [ObservableProperty] private bool _shown;
    [ObservableProperty] private bool _isRoundsAttack;

    public AttackPickRow(string key, string label, bool shown, bool isRoundsAttack,
        bool basisOnly = false)
    {
        Key = key;
        Label = label;
        _shown = shown;
        _isRoundsAttack = isRoundsAttack;
        BasisOnly = basisOnly;
    }
}
