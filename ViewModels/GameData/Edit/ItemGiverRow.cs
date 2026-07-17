using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// One "given by" entry in an item's read-only MDB info: a monster or room that
// hands the item over through a textblock `giveitem` award, with an optional
// requirement note ("turn in <item>", "purchase", "quest reward") beneath it.
// The name is a clickable link — a monster source jumps the Game Data browser to
// that Monsters-tab record, a room source to the Rooms-tab record — mirroring the
// bought/sold shop rows.
public sealed class ItemGiverRow
{
    public string Name { get; }
    public string Requirement { get; }
    public bool HasRequirement => Requirement.Length > 0;

    // False when the source coordinate couldn't be resolved (a monster with id
    // <= 0 or a room with map/room <= 0) — the row still shows the name but has
    // nothing to navigate to, so the view greys the link.
    public bool CanOpen { get; }
    public ICommand Open { get; }

    public ItemGiverRow(ItemGiver giver)
    {
        Name = giver.Name;
        Requirement = giver.Requirement;
        CanOpen = giver.Kind == ItemGiverKind.Monster
            ? giver.Number > 0
            : giver.Map > 0 && giver.Room > 0;
        Open = new RelayCommand(
            () =>
            {
                if (giver.Kind == ItemGiverKind.Monster)
                    AppServices.Current.OpenMonsterGameData(giver.Number);
                else
                    AppServices.Current.OpenRoomGameData(giver.Map, giver.Room);
            },
            () => CanOpen);
    }
}
