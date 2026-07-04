using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels;

// One entry in the File → Game Data → Active set submenu. Carries the set
// name + a checkbox-style IsActive flag (the currently-selected set's row
// gets the checkmark) + the command that switches the active set when the
// user clicks the row.
public sealed partial class GameDataSetMenuItem : ObservableObject
{
    // On-disk folder name under Data/game data/.
    public string Name { get; }

    // True when this entry matches the cache's current ActiveSet.
    [ObservableProperty] private bool _isActive;

    // Invoked when the user clicks this entry. Switches the cache and writes the active profile.
    public ICommand SwitchCommand { get; }

    public GameDataSetMenuItem(string name, bool isActive, ICommand switchCommand)
    {
        Name = name;
        IsActive = isActive;
        SwitchCommand = switchCommand;
    }
}
