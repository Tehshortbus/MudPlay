using System.Collections.Generic;

namespace MudPlay.Game.Map;

// One item entry from a sysop room-status dump: the MDB item Number and the
// parenthesised value the game prints beside it ("521(0)").
//
// An entry is one object on the floor, and RawValue is that object's stack size
// minus one — (0) is a single item, (1) is a stack of two. An item id can appear
// MORE THAN ONCE in a list: dropping two black star keys one at a time reads
// "172(0) 172(0)" (two objects of one each), while dropping two diamonds reads
// "902(1)" (one object of two). So the room's true count of an item is the sum
// of Count across every entry carrying that id — never a lookup of one entry.
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
// The populated form is space-separated bare numbers ("Monsters: 4510 8407"),
// but those are NOT Monsters.Number — none of the observed values exist in the
// active set's Monsters table, while the same dump's "Specific Monster:" line
// does carry a real catalogue number ("784-Mayor Godfrey"). Until we know what
// they identify, parsing them into ids would invent a meaning they may not have,
// so the line is carried verbatim and HasMonsters answers the only question
// anything currently asks.
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
