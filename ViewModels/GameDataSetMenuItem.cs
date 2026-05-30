using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels;

/// <summary>
/// One entry in the File → Game Data → Active set submenu. Carries the
/// set name + a checkbox-style <see cref="IsActive"/> flag (the
/// currently-selected set's row gets the checkmark) + the command
/// that switches the active set when the user clicks the row.
/// </summary>
public sealed partial class GameDataSetMenuItem : ObservableObject
{
    /// <summary>On-disk folder name under <c>Data/game data/</c>.</summary>
    public string Name { get; }

    /// <summary>True when this entry matches the cache's current <c>ActiveSet</c>.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Invoked when the user clicks this entry. Switches the cache and writes the active profile.</summary>
    public ICommand SwitchCommand { get; }

    public GameDataSetMenuItem(string name, bool isActive, ICommand switchCommand)
    {
        Name = name;
        IsActive = isActive;
        SwitchCommand = switchCommand;
    }
}
