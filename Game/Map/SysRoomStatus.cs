using System.Collections.Generic;

namespace MudPlay.Game.Map;

// One item entry from a sysop room-status dump: the MDB item Number and the
// parenthesised value the game prints beside it ("521(0)").
//
// RawValue is stored verbatim and interpreted in exactly one place. The reading
// is UNVERIFIED: it is believed to be quantity-1, so (0) means one present and
// (1) means two. Every consumer goes through Count so a corrected reading is a
// one-line change here rather than a hunt through the callers. The confirming
// experiment is to drop a second copy of an item already on the floor and watch
// its entry move from (0) to (1).
public readonly record struct SysRoomItem(int ItemId, int RawValue)
{
    public int Count => RawValue + 1;
}

// A parsed `sysop status` room dump. Carries only what a consumer reads: the
// room identity for location recovery, and the floor contents for Roomba's
// recon pass. The dump also prints area / lair-group / regen numbers and the
// Patrollable / Ganghouse / controlling-room flags; the parser recognises those
// lines so it delimits the block correctly, but nothing consumes them yet and
// carrying them would be dead weight.
//
// MonstersRaw is the verbatim text after "Monsters:" rather than a parsed list.
// The populated form of that line has never been captured — only "None" — so
// splitting it would be guessing at a format. HasMonsters answers the only
// question anything currently needs.
public sealed record SysRoomStatus(
    RoomKey Room,
    string MonstersRaw,
    IReadOnlyList<SysRoomItem> Items,
    IReadOnlyList<SysRoomItem> HiddenItems)
{
    public bool HasMonsters =>
        !string.IsNullOrWhiteSpace(MonstersRaw)
        && !MonstersRaw.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);
}
