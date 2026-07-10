using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// One row of the item dialog's chest-contents table: the item a container can
// yield and the chance (already formatted, e.g. "42%") that a single `open`
// produces at least one of it. The name is a clickable link that jumps the Game
// Data browser's Items tab to that item, mirroring the room popup's shop rows.
public sealed class ChestDropRow
{
    public string Name { get; }
    public string Chance { get; }
    public IRelayCommand Open { get; }

    public ChestDropRow(int itemId, string name, string chance)
    {
        Name = name;
        Chance = chance;
        Open = new RelayCommand(() => AppServices.Current.OpenItemGameData(itemId));
    }
}
