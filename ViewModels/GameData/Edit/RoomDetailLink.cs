using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Edit;

// One clickable row in the room-detail popup — a monster name (click opens the
// monster's Game Data record) or an exit destination (click centres the
// Navigation map on that room). Detail is an optional muted suffix (a monster
// note like "placed", or an exit hint like "Door" / "Trap: 40 dmg").
public sealed partial class RoomDetailLink : ObservableObject
{
    public string Text { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public ICommand Open { get; }

    // Effective per-link text colour, overriding whatever the template's style class
    // sets. The route-details window drives this live: it starts at the monster's
    // alignment tint (evil red / neutral cyan / good white) and, while "Color
    // monsters by hit %" is on, is swapped to a green/amber/red band brush keyed off
    // HitPercent. Null leaves the class colour in force (every non-monster consumer).
    [ObservableProperty] private IBrush? _accent;

    // The monster's alignment tint (the default look), kept so the route-details
    // window can restore it when hit-% colouring is switched back off. Null for a
    // non-monster link.
    public IBrush? AlignAccent { get; init; }

    // The monster's live "Hits You %" (the Monster Intel weighted incoming-hit), or
    // null when it isn't computable (no character context, or the monster has no
    // physical attack). Drives the band colour when hit-% colouring is on; a null
    // reads as a muted "no data" tint.
    public int? HitPercent { get; init; }

    // True for a monster carrying the SeeHidden ability (defeats sneak) — the
    // route-details template flanks the name with an eyeball marker. False for a
    // non-monster link or a monster without it.
    public bool SeesHidden { get; init; }

    public RoomDetailLink(string text, string? detail, ICommand open)
    {
        Text = text;
        Detail = detail;
        Open = open;
    }
}
