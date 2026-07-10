using System.Windows.Input;

namespace FujinTerm.ViewModels.GameData.Edit;

// One clickable row in the room-detail popup — a monster name (click opens the
// monster's Game Data record) or an exit destination (click centres the
// Navigation map on that room). Detail is an optional muted suffix (a monster
// note like "placed", or an exit hint like "Door" / "Trap: 40 dmg").
public sealed class RoomDetailLink
{
    public string Text { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public ICommand Open { get; }

    public RoomDetailLink(string text, string? detail, ICommand open)
    {
        Text = text;
        Detail = detail;
        Open = open;
    }
}
