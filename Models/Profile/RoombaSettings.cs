using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// Root DTO for the realm's roomba.json (AppPaths.RealmRoombaFile) — Roomba
// Mode's realm-wide settings. A realm ties to one game-data set and every
// character on it shares the same gang house, so room labels and the
// sweep-tuning knobs are realm-wide rather than per-character: label a room
// once on any character and it's there (and its floor is queryable via
// @roomba) for every other character on that realm. See GhRoomLabelStore.
public sealed class RoombaSettings
{
    // Labeled gang-house rooms and their sort rules. null/empty = GH not set
    // up yet on this realm.
    public List<GhRoomLabel>? RoomLabels { get; set; }

    // Per-room hidden-search count — null = GhRoomLabelStore's default (3).
    public int? SearchesPerRoom { get; set; }

    // Whether recon searches (`sea`) each room for hidden items during recon.
    // null/false = sort visible floor items only.
    public bool? SearchForHidden { get; set; }

    // Items a sweep leaves wherever it finds them, by canonical item name.
    // null/empty = sort everything.
    public List<string>? IgnoredItems { get; set; }
}
