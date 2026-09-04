using System;
using System.Collections.Generic;
using MudPlay.Game.Combat;

namespace MudPlay.Game.Inventory;

// How Grab-All applies to a boss row, resolved from its name against game data:
//   Monster — it's a monster; grab its drop table the instant it dies.
//   Item    — it's an item that just sits in the room (a box); `get` it on room entry.
//   None    — the name resolves to neither (e.g. a touch-to-awaken mechanic like
//             Iceforge); Grab-All doesn't apply and the checkbox is hidden.
public enum BossGrabKind { None, Monster, Item }

// Builds the blind "grab everything" command list fired the moment a Grab-All boss
// dies: one `get <item name>` per DISTINCT item in the monster's game-data drop
// table, in drop-slot order, skipping ids that don't resolve to a name in the active
// set. There's no bulk "get all" verb in MajorMUD, so each item is its own line. The
// drop percentages are ignored on purpose — "every item it COULD have dropped". Pure
// (no game-data / send dependency) so it's unit-tested off a fake catalog + namer.
public static class BossGrabAllCommands
{
    // Classify a boss from whether its name resolves to a monster and/or an item.
    // A known monster number (or a matching Monsters row) always means Monster;
    // Monster wins if a name resolves to both. Only an item-and-not-monster is Item;
    // neither is None.
    public static BossGrabKind ClassifyKind(bool hasMonsterNumber, bool isMonster, bool isItem)
        => hasMonsterNumber || isMonster ? BossGrabKind.Monster
           : isItem ? BossGrabKind.Item
           : BossGrabKind.None;

    public static IReadOnlyList<string> Build(IReadOnlyList<MonsterDropSlot>? drops, Func<int, string?> itemName)
    {
        ArgumentNullException.ThrowIfNull(itemName);
        if (drops is null) return Array.Empty<string>();

        var cmds = new List<string>();
        var seen = new HashSet<int>();
        foreach (MonsterDropSlot drop in drops)
        {
            if (drop.ItemId <= 0 || !seen.Add(drop.ItemId)) continue;
            string? name = itemName(drop.ItemId);
            if (string.IsNullOrWhiteSpace(name)) continue;
            cmds.Add($"get {name.Trim()}");
        }
        return cmds;
    }
}
