using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// One clickable item chip in a monster record's item-drops list. Clicking jumps
// the Game Data browser's Items tab to that item, so the monster pane doubles as
// a jump-off to the loot it references (mirrors RoomLink for rooms).
public sealed class ItemLink
{
    public string Label { get; }
    public ICommand Open { get; }

    public ItemLink(string label, int itemNumber)
    {
        Label = label;
        Open = new RelayCommand(() => AppServices.Current.OpenItemGameData(itemNumber));
    }
}
