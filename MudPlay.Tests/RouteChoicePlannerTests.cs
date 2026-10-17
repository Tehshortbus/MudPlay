using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Free-vs-direct route comparison: the planner only offers a choice when the
// acquirable-gate route is a genuine shortcut AND the crosser lacks the gate
// item(s). These pin the offer / no-offer boundaries and the requirement
// classification the picker phrases from.
public sealed class RouteChoicePlannerTests
{
    // Direct: 1/1 ──E (Item: 5)── 1/9   (1 hop, gated on a raft).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9   (3 hops, gate-free).
    private const string ItemShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The item shortcut is the ONLY route — no gate-free alternative exists.
    private const string ItemOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Gated and free routes are the SAME length — the "shortcut" saves nothing.
    // Direct: 1/1 ──E (Item: 5)── 1/2 ──E── 1/9   (2 hops).
    // Free:   1/1 ──N── 1/3 ──E── 1/9             (2 hops).
    private const string EqualLengthJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/2 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "GateSide",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "FreeSide",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Item shortcut that saves only ONE room — a boat/skiff hop not worth buying.
    // Direct: 1/1 ──E (Item: 5)── 1/9   (1 hop, gated on a boat).
    // Free:   1/1 ──N── 1/2 ──E── 1/9   (2 hops, gate-free).
    private const string ItemShortcutSavesOneJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Same layout as ItemShortcutJson, but the shortcut gate is a Ticket.
    private const string TicketShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Ticket: 9)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Direct route crosses a pick-only locked door the crosser can't open.
    // Direct: 1/1 ──E (Key: 7 or 80 picklocks)── 1/9   (1 hop).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9        (3 hops).
    private const string KeyDoorShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Key: 7 or 80 picklocks)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A hidden exit whose unlock action needs a held item. The action rides a
    // SIBLING direction cell, exactly as the MegaMUD export writes it — the Lower
    // Caverns bloodstone-orb exit puts its S-exit action in the room's unused N
    // cell — so the requirement lives in MultiAction, not KeyItemId.
    // Direct: 1/1 ──S (hidden, `rub orb`, Item: 5)── 1/9   (1 hop).
    // Free:   1/1 ──E── 1/2 ──E── 1/3 ──E── 1/9            (3 hops, gate-free).
    private const string MultiActionItemShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Underground Lake",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "Action [on the S exit of this room]: rub orb (Item: 5)",
            "S": "1/9 (Hidden/Needs 1 Actions, any order)", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Narrow Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A plain door the crosser can't open walls off the ONLY route to 1/9. Not an
    // acquirable gate (no key), so suspending acquirable gates doesn't open it —
    // Evaluate returns null, and PlanBlocked offers "run to the blocked room".
    // Only route: 1/1 ──E── 1/2 ──E (Door [200 picklocks])── 1/9.
    private const string PlainDoorOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Antechamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Door [200 picklocks])", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Direct route steps THROUGH hazard room 1/5 (Spell 700, countered by item 42).
    // Direct: 1/1 ──E── 1/5 ──E── 1/9              (2 hops, enters the hazard).
    // Free:   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/9    (3 hops, avoids it).
    private const string HazardShortcutRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;
    private const string HazardSpellsJson = """
        [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ]
        """;
    private const string HazardItemsJson = """
        [ { "Number": 42, "NegateSpell-0": 700 } ]
        """;

    // The hazard room 1/5 is the ONLY way from 1/1 to 1/9 — no gate-free detour.
    // Direct (and only): 1/1 ──E── 1/5 (Spell 700) ──E── 1/9.
    private const string HazardOnlyRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Dest 1/9 sits BEHIND hazard room 1/5 (its only approach); a shop room 1/2
    // hangs off the gate-free side. Sourcing a counter at the shop means scoring
    // dist(cur→shop→dest), whose dest leg passes THROUGH the hazard — reachable
    // only with the acquirable gates suspended (counter in hand).
    private const string ShopBehindHazardRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Shop", "Spell": 0,
            "Light": 0, "Shop": 3, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A CMD>0 room with an "(Item: N)" exit is the teleport pattern: TryReadRoom
    // promotes that exit to RoomExitHint.Teleport, which BFS crosses as a normal
    // short edge. Here 1/1's E teleports straight to 1/9 (1 hop), while the
    // walking route detours 1/1→1/2→1/3→1/9 (3 hops).
    private const string TeleportShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The teleport is the ONLY way from 1/1 to 1/9 — no walking route exists.
    private const string TeleportOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Teleport saves only one room over the walk — below the picker's floor.
    // Teleport: 1/1 ──E(teleport)── 1/9   (1 hop).
    // Walk:     1/1 ──N── 1/2 ──E── 1/9   (2 hops).
    private const string TeleportSavesOneJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // No teleport anywhere — the shortest route already walks the whole way.
    private const string NoTeleportJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The teleport fixtures all put CMD 5 on 1/1 with an (Item: 5) exit to 1/9; the
    // Item→Teleport promotion is TBInfo-gated, so a CMD-5 chain that teleports to
    // 1/9 makes the exit legitimately promote.
    private const string TeleportTbInfo =
        """[ { "Number": 5, "Action": "go arch:teleport 9 1\n" } ]""";

    private static void WithTeleportGraph(
        string roomsJson,
        Action<BfsMapper, RoomGraphManager, MovementFilter> body)
        => WithGraph(roomsJson, body, tbInfoJson: TeleportTbInfo);

    private static void WithGraph(
        string roomsJson,
        Action<BfsMapper, RoomGraphManager, MovementFilter> body,
        string? spellsJson = null,
        string? itemsJson = null,
        Action<RoomHazardIndex, MovementFilter>? wireHazards = null,
        string? tbInfoJson = null)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "mudplay-routechoice-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            if (spellsJson is not null) File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);
            if (itemsJson is not null) File.WriteAllText(Path.Combine(setDir, "Items.json"), itemsJson);
            if (tbInfoJson is not null) File.WriteAllText(Path.Combine(setDir, "TBInfo.json"), tbInfoJson);

            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            // The Item→Teleport promotion is TBInfo-gated: build with a TBInfoStore
            // when a teleport CMD chain is supplied so the fixture's (Item: N) exit
            // legitimately promotes.
            RoomGraphManager graph;
            if (tbInfoJson is not null)
            {
                TBInfoStore tbinfo = new(cache);
                tbinfo.OnActiveSetChanged("alpha");
                graph = new(cache, log: null, tbinfo);
            }
            else
            {
                graph = new(cache);
            }
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            MovementFilter filter = new(profile);

            if (wireHazards is not null)
            {
                RoomHazardIndex index = new(cache);
                index.OnActiveSetChanged("alpha");
                wireHazards(index, filter);
            }

            body(bfs, graph, filter);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void OffersChoice_WhenItemShortcutIsShorter_AndItemMissing()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the raft

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);
            Assert.Equal(1, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
            Assert.Equal(new[] { 5 }, req.ItemIds);

            // The picker previews each route as a RoomKey line (source first,
            // then every hop's target) — free detours 1/1→1/2→1/3→1/9, the
            // direct hop is 1/1→1/9.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 9) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoChoice_WhenItemCarried_RoutesCoincide()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 5;   // already holding the raft

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Free route already takes the direct hop, so gated == free → no offer.
            Assert.Null(choice);
        });
    }

    // Report paradigm-20261007-183903: an Item exit carries across only the member
    // holding the item. A leader with a copy, leading followers without one, was
    // planned straight through the gate with no route card and crossed alone. With
    // the party short, the gate is closed to the plan and lists as needed.
    [Fact]
    public void OffersChoice_WhenTheLeaderHoldsTheItem_ButThePartyIsShort()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 5;
            filter.PartyShortOfItemProbe = id => id == 5;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);      // the plan goes round the gate
            Assert.Equal(1, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(new[] { 5 }, req.ItemIds);
            Assert.False(req.Carried);                   // not "you have it"
            // Picking the gated route arms the copies still to come.
            Assert.Equal(new[] { 5 }, RouteChoicePlanner.SourceableGateItems(choice.Requirements, NoSummons));
        });
    }

    // A key opens the door for everyone behind the leader, so a locked door is
    // never closed by the party count.
    [Fact]
    public void PartyShort_DoesNotCloseAKeyedDoor()
    {
        MovementFilter filter = new(BlankProfile());
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = _ => true;
        filter.PartyShortOfItemProbe = _ => true;

        RoomExit door = new(new RoomKey(1, 2), RoomExitHint.KeyLocked, RawHint: null, KeyItemId: 7);
        RoomExit arch = new(new RoomKey(1, 2), RoomExitHint.Item, RawHint: null, KeyItemId: 7);

        Assert.False(filter.IsExitBlocked(in door));
        Assert.True(filter.IsExitBlocked(in arch));
    }

    private static ProfileService BlankProfile()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        return profile;
    }

    [Fact]
    public void OffersSoleItemRoute_WhenNoFreeAlternative()
    {
        WithGraph(ItemOnlyJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // The item gate is the only way through — surface it (HasFreeRoute
            // false) so the caller's flag logic decides whether to arm the
            // acquisition pipeline or fail in place naming the item.
            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Empty(choice.FreePath);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
        });
    }

    // The paradigm-20260913-100733 shape: the only way to 1/9 crosses a REQUIRED orb
    // gate (807), after which two routes diverge — a shorter one through an OPTIONAL
    // amber-talisman shortcut (815) and a longer one through a gate-key door (806) the
    // crosser already holds. The planner must commit the long, reliable route (so its
    // carried key surfaces), report only the orb as required, and offer the talisman as
    // an optional shortcut — never call it "required".
    // Shortest (all gates suspended): 1/1 ─E(orb)─ 1/2 ─E(talisman)─ 1/9   (2 hops).
    // Committed (talisman avoided):    1/1 ─E(orb)─ 1/2 ─N─ 1/3 ─E(key)─ 1/9 (3 hops).
    private const string OptionalShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Bank",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/2 (Item: 807)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "0", "E": "1/9 (Item: 815)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "CityGate",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9 (Key: 806)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void SoleRoute_OptionalShortcut_IsNotRequired_AndHeldKeySurfaces()
    {
        WithGraph(OptionalShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 806;   // holds the gate key, lacks orb + talisman

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            // Committed to the reliable long route (3 hops), not the talisman shortcut (2).
            Assert.Equal(3, choice.GatedStepCount);

            // The orb is genuinely required; the held gate key surfaces as carried.
            RouteRequirement orb = Assert.Single(choice.Requirements,
                r => r.Kind == RouteRequirementKind.CarryItem && r.ItemIds.SequenceEqual(new[] { 807 }));
            Assert.False(orb.Carried);
            RouteRequirement key = Assert.Single(choice.Requirements,
                r => r.Kind == RouteRequirementKind.DoorKey && r.ItemIds.SequenceEqual(new[] { 806 }));
            Assert.True(key.Carried);
            // The amber talisman is NEVER a requirement.
            Assert.DoesNotContain(choice.Requirements, r => r.ItemIds.Contains(815));

            // It's surfaced as an optional shortcut instead, saving the one room.
            Assert.Equal(new[] { 815 }, choice.ShortcutItems);
            Assert.Equal(2, choice.ShortcutStepCount);

            // Acquisition sources the orb but never the carried key or the shortcut item.
            Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(choice.Requirements, NoSummons));
        });
    }

    // The shape of reports paradigm-20261008-173911 / -174023: both ways to 1/9 cross
    // the orb gate (807) and a keyed door the crosser can pick (806, 101 picklocks).
    // They differ only at the start: one hop through the talisman exit (815), or three
    // hops round it.
    // Shortcut:  1/1 ─E(talisman)─ 1/4 ─E(orb)─ 1/5 ─E(door)─ 1/9          (3 hops).
    // Committed: 1/1 ─N─ 1/2 ─N─ 1/3 ─E─ 1/4 ─E(orb)─ 1/5 ─E(door)─ 1/9    (5 hops).
    private const string ShortcutSharesGatesJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hills",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4 (Item: 815)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tunnel",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Tunnel End",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5 (Item: 807)", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Gate",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Key: 806 [or 101 picklocks])", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A thief who picks the gate and carries neither orb nor talisman.
    private static void AsLockpickerCarryingNothing(MovementFilter filter)
    {
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = _ => false;
        filter.StrengthProvider = () => 90;
        filter.PicklocksProvider = () => 119;
    }

    [Fact]
    public void SoleRoute_EachCardListsWhatItsOwnRouteNeeds()
    {
        WithGraph(ShortcutSharesGatesJson, (bfs, graph, filter) =>
        {
            AsLockpickerCarryingNothing(filter);

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);

            // The committed route goes round the talisman exit and needs the orb alone:
            // the door is picked, so its key is nothing to bring.
            Assert.Equal(5, choice.GatedStepCount);
            RouteRequirement orb = Assert.Single(choice.Requirements);
            Assert.Equal(new[] { 807 }, orb.ItemIds);
            Assert.Equal(new[] { 815 }, choice.ClosedGateItems);

            // The shortcut is named for the talisman only (the gate key was listed
            // beside it, for a door this crosser opens by hand)...
            Assert.Equal(new[] { 815 }, choice.ShortcutItems);
            Assert.Equal(3, choice.ShortcutStepCount);

            // ...and its own list has everything its route needs: it still crosses
            // the orb gate, which the card left out.
            Assert.NotNull(choice.ShortcutRequirements);
            Assert.Equal(2, choice.ShortcutRequirements!.Count);
            Assert.Contains(choice.ShortcutRequirements, r => r.ItemIds.SequenceEqual(new[] { 815 }) && !r.Carried);
            Assert.Contains(choice.ShortcutRequirements, r => r.ItemIds.SequenceEqual(new[] { 807 }) && !r.Carried);
        });
    }

    // A walk plans for itself, through every gate, so it has to be told which gates
    // the card's route went round. With them kept closed its search is the card's.
    [Fact]
    public void SoleRoute_ClosedGateItems_ReproduceTheCommittedRoute()
    {
        WithGraph(ShortcutSharesGatesJson, (bfs, graph, filter) =>
        {
            AsLockpickerCarryingNothing(filter);
            RoomKey src = new(1, 1), dst = new(1, 9);
            RouteChoice choice = RouteChoicePlanner.Evaluate(bfs, filter, graph, src, dst)!;

            IReadOnlyList<Direction>? everyGateOpen, asCommitted;
            using (filter.SuspendAcquirableGates())
                everyGateOpen = bfs.FindPath(src, dst, filter);
            using (filter.SuspendAcquirableGatesExcept(choice.ClosedGateItems!))
                asCommitted = bfs.FindPath(src, dst, filter);

            Assert.Equal(choice.ShortcutPath, RouteChoicePlanner.BuildKeyPath(graph, src, everyGateOpen!));
            Assert.Equal(choice.GatedPath, RouteChoicePlanner.BuildKeyPath(graph, src, asCommitted!));
        });
    }

    // Round the talisman exit there are two ways to the lake: north over a trap (two
    // hops) or south and round (three, no trap). The walk a sole-route card starts
    // plans for the fewest traps, so the card has to as well, or it shows the
    // 3-step route and the walk takes the 4-step one.
    //   1/1 ─E(815)─ 1/4 Lake ─E(807)─ 1/9
    //    ├N(Trap)─ 1/2 ─E─ 1/4
    //    └S─ 1/3 ─E─ 1/5 ─N─ 1/4
    private const string TrapOnTheWayRoundJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hills",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "1/3", "E": "1/4 (Item: 815)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Trapped Pass",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Low Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Low Road End",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "1/9 (Item: 807)", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void SoleRoute_RoundAShortcut_IsPlannedByTheTrapRuleItsWalkUses()
    {
        WithGraph(TrapOnTheWayRoundJson, (bfs, graph, filter) =>
        {
            AsLockpickerCarryingNothing(filter);
            RoomKey src = new(1, 1), dst = new(1, 9);

            RouteChoice choice = RouteChoicePlanner.Evaluate(bfs, filter, graph, src, dst)!;

            Assert.Equal(new[] { 815 }, choice.ClosedGateItems);
            Assert.Equal(
                new[] { src, new RoomKey(1, 3), new RoomKey(1, 5), new RoomKey(1, 4), dst },
                choice.GatedPath);
            Assert.Equal(4, choice.GatedStepCount);

            // The walk the card commits: through the gates but for the closed ones,
            // fewest traps, on foot (the route doesn't teleport).
            IReadOnlyList<Direction>? walk;
            using (filter.SuspendAcquirableGatesExcept(choice.ClosedGateItems!))
                walk = bfs.FindPath(src, dst, filter, refuseTeleports: true, avoidTraps: true);
            Assert.Equal(choice.GatedPath, RouteChoicePlanner.BuildKeyPath(graph, src, walk!));
        });
    }

    // A door the crosser can pick, opening into a hazard room they hold no counter
    // for. The exit is closed to them by the hazard, not the lock, so its key is no
    // gate the route "goes round": the walk would be told to keep a gate closed that
    // isn't one, and the log and bug report would name a key nobody needs.
    [Fact]
    public void APickableDoorIntoAHazardRoom_DoesNotMakeItsKeyAClosedGate()
    {
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/5 (Key: 806 [or 20 picklocks])", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 5, "Name": "Flooded Hall", "Spell": 700,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/9", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/5",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithGraph(rooms, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Null(choice!.ClosedGateItems);
            Assert.Null(choice.ShortcutItems);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            AsLockpickerCarryingNothing(filter);
        });
    }

    // Picking a card that crosses a hazard agrees to the hazard rooms on that card's
    // route, by name. This is the list the walk is given.
    [Fact]
    public void UncounteredHazardRooms_AreTheOnesOnTheCardsRoute_TheCrosserHoldsNoCounterFor()
    {
        RoomKey[] route = { new(1, 1), new(1, 5), new(1, 9) };
        bool carriesCounter = false;
        WithGraph(HazardOnlyRoomsJson, (_, _, filter) =>
        {
            Assert.Equal(new[] { new RoomKey(1, 5) }, RouteChoicePlanner.UncounteredHazardRooms(filter, route));
            Assert.Empty(RouteChoicePlanner.UncounteredHazardRooms(filter, null));

            carriesCounter = true;
            Assert.Empty(RouteChoicePlanner.UncounteredHazardRooms(filter, route));
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => carriesCounter && id == 42;
        });
    }

    // The whole of the third report's fix is this decision: a picked route that
    // teleports is walked with teleports allowed, and one that walks keeps the walk
    // on foot. "Walk it" is never overridden, nor is an explicit "Teleport".
    [Fact]
    public void PickedWalkPrefersTeleportFree_FollowsTheRouteTheCardShowed()
    {
        string[] none = Array.Empty<string>(), hole = { "Stone Tunnel, Hole Up (2/1306)" };

        Assert.True(RouteChoicePlanner.PickedWalkPrefersTeleportFree(requested: true, avoidTeleports: false, none));
        Assert.False(RouteChoicePlanner.PickedWalkPrefersTeleportFree(requested: true, avoidTeleports: false, hole));
        Assert.True(RouteChoicePlanner.PickedWalkPrefersTeleportFree(requested: true, avoidTeleports: true, hole));
        Assert.False(RouteChoicePlanner.PickedWalkPrefersTeleportFree(requested: false, avoidTeleports: false, none));
    }

    // A teleport beside a door to the same room: with the door passable the walk
    // goes through the door, so the hop is no teleport; with it shut, it is one.
    [Fact]
    public void TeleportLandings_ATeleportBesideAPassableExit_IsNoTeleportHop()
    {
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/9", "S": "0", "E": "1/9 (Item: 5)", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Vault",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithTeleportGraph(rooms, (_, graph, filter) =>
        {
            RoomKey[] hop = { new(1, 1), new(1, 9) };
            Assert.Empty(RouteChoicePlanner.TeleportLandings(graph, hop, filter));
            Assert.Equal(new[] { "Vault (1/9)" },
                RouteChoicePlanner.TeleportLandings(graph, hop, new WalkingExitsShut()));
        });
    }

    // Closes every exit that isn't a teleport, as a gate the crosser can't pass would.
    private sealed class WalkingExitsShut : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => false;
        public bool IsExitBlocked(in RoomExit exit) => !AutomaticWalkTeleportFilter.IsTeleport(in exit);
    }

    [Fact]
    public void TeleportLandings_OnACardsRoute_NameEachLanding_OrNothingWhenItWalks()
    {
        WithTeleportGraph(TeleportShortcutJson, (_, graph, _) =>
        {
            Assert.Equal(new[] { "Vault (1/9)" }, RouteChoicePlanner.TeleportLandings(
                graph, new[] { new RoomKey(1, 1), new RoomKey(1, 9) }));
            Assert.Empty(RouteChoicePlanner.TeleportLandings(
                graph, new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 9) }));
            Assert.Empty(RouteChoicePlanner.TeleportLandings(graph, null));
        });

        Assert.Null(RouteChoicePlanner.DescribeTeleports(Array.Empty<string>()));
        Assert.Equal("the teleport to Vault (1/9)", RouteChoicePlanner.DescribeTeleports(new[] { "Vault (1/9)" }));
        Assert.Equal("teleports to A, B and C", RouteChoicePlanner.DescribeTeleports(new[] { "A", "B", "C" }));
    }

    [Fact]
    public void NoChoice_WhenShortcutSavesNoSteps()
    {
        WithGraph(EqualLengthJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.Null(choice);   // gated 2 hops, free 2 hops — no bargain
        });
    }

    [Fact]
    public void NoChoice_WhenItemShortcutSavesOnlyOneStep()
    {
        WithGraph(ItemShortcutSavesOneJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the boat

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Gated 1 hop, free 2 hops — a single room saved isn't worth buying
            // the boat, so no picker; the caller just walks the free route.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void ClassifiesTicketRequirement()
    {
        WithGraph(TicketShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.Ticket, req.Kind);
            Assert.Equal(new[] { 9 }, req.ItemIds);
        });
    }

    [Fact]
    public void ClassifiesDoorKeyRequirement()
    {
        WithGraph(KeyDoorShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;          // key not held
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick statReq 80
            filter.MaxBashableStrengthProvider = () => 200;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.DoorKey, req.Kind);
            Assert.Equal(new[] { 7 }, req.ItemIds);
        });
    }

    // The gate that went unreported: a hidden exit's held-item requirement used to
    // fall through Classify to HazardRequirement, return null, and vanish — so the
    // picker offered the shortcut without ever naming the item the walk would need
    // (report paradigm-20260911-010954).
    [Fact]
    public void ClassifiesMultiActionHeldItemRequirement()
    {
        WithGraph(MultiActionItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // lacking the orb

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.CarryItem, req.Kind);
            Assert.Equal(new[] { 5 }, req.ItemIds);
        });
    }

    [Fact]
    public void NoChoice_WhenMultiActionHeldItemCarried()
    {
        WithGraph(MultiActionItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 5;   // orb in hand

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // The hidden exit is already crossable, so the free route takes it —
            // gated == free, nothing to offer.
            Assert.Null(choice);
        });
    }

    // Fully blocked by a non-acquirable door → Evaluate offers nothing, but
    // PlanBlocked walks to the room just before it.
    [Fact]
    public void PlanBlocked_PlainDoorWallsOnlyRoute_StopsBeforeIt()
    {
        WithGraph(PlainDoorOnlyJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick statReq 200
            filter.MaxBashableStrengthProvider = () => 200;

            // Evaluate offers nothing (a plain door isn't acquirable).
            Assert.Null(RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.Equal(new RoomKey(1, 2), plan!.StopRoom);       // room just before the door
            Assert.Equal(Direction.E, plan.BlockDir);
            Assert.Equal(new RoomKey(1, 9), plan.BlockExit.Target);
            Assert.Equal(new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, plan.Preview);
        });
    }

    [Fact]
    public void PlanBlocked_ReachableRoute_ReturnsNull()
    {
        WithGraph(ItemShortcutJson, (bfs, graph, filter) =>
            // The free (gate-free) route to 1/9 is open, so there's nothing to run up to.
            Assert.Null(RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }

    [Fact]
    public void PlanBlocked_BlockedAtDoorstep_ReturnsNull()
    {
        // 1/1 ──E (Door [200 picklocks])── 1/9: the block is the very first hop, so
        // there's no reachable room to run to short of it.
        const string doorstepJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Start",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/9 (Door [200 picklocks])", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Vault",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithGraph(doorstepJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;
            filter.MaxBashableStrengthProvider = () => 200;

            Assert.Null(RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));
        });
    }

    [Fact]
    public void PlanBlocked_LevelBlockedDestination_NamesTheLevelGate_NotAnImpassableDoorDetour()
    {
        // The Ancient Fortress shape: the destination (1/9) is reachable only past
        // a level gate a low-level character fails — but the graph ALSO "reaches"
        // it through an impassable 1000-picklock door (the pyramid door / pit
        // drops). PlanBlocked must name the LEVEL gate, not the incidental door,
        // so the picker tells the user WHY (they're not high enough level).
        const string fortressJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Approach",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/8", "E": "1/2", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Gatehouse",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/9 (Level: 75 to 999)", "W": "1/1",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 8, "Name": "Pyramid",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/1", "S": "0", "E": "1/9 (Door [1000 picklocks/strength])", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Jailer Room",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/2",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        WithGraph(fortressJson, (bfs, graph, filter) =>
        {
            filter.LevelProvider = () => 28;               // under the level-75 gate
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;            // can't pick the 1000-door
            filter.MaxBashableStrengthProvider = () => 200;

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.True(plan!.BlockExit.HasLevelGate,
                "the named block must be the level gate, not the impassable door");
            Assert.Equal(75, plan.BlockExit.MinLevel);
            Assert.Equal(new RoomKey(1, 2), plan.StopRoom);
        });
    }

    [Fact]
    public void PlanBlocked_LevelGatedTeleportShortcut_NamesTheDestinationsOwnLevelGate_NotTheShortcut()
    {
        // Redstone-Tunnel shape: the destination (1/9) is reachable by a LAND route
        // through a level-75 gate, OR by a level-40 teleport shortcut. A blocked
        // walker routes around the shortcut, so the reported block must be the
        // destination's own level-75 gate — not the level-40 portal it can't and
        // wouldn't take (report paradigm-20260914-201123).
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Fork", "CMD": 6,
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/2", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Approach",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/1", "S": "0", "E": "1/9 (Level: 75 to 999)", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 9, "Name": "Destination",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/2",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        // CMD 6 on 1/1 synthesises a level-40 teleport STRAIGHT to 1/9 (1 hop) — a
        // shorter shortcut than the 2-hop land route, so without refusing teleports
        // the physical route takes it and the block would read as the level-40
        // portal instead of the destination's level-75 gate.
        const string tbInfo = """[ { "Number": 6, "Action": "go portal:minlevel 40:teleport 9 1\n" } ]""";

        WithGraph(rooms, (bfs, graph, filter) =>
        {
            filter.LevelProvider = () => 28;               // under both gates
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 10;
            filter.PicklocksProvider = () => 0;
            filter.MaxBashableStrengthProvider = () => 200;

            // Guard the fixture: the level-40 teleport shortcut really is synthesised.
            Assert.True(graph.GetRoom(new RoomKey(1, 1))!.Exits
                            .TryGetValue(Direction.Teleport, out RoomExit tp) && tp.MinLevel == 40,
                "fixture no longer produces the level-40 teleport shortcut");

            BlockedRoutePlan? plan = RouteChoicePlanner.PlanBlocked(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(plan);
            Assert.Equal(75, plan!.BlockExit.MinLevel);    // the destination's gate, NOT the level-40 portal
            Assert.Equal(new RoomKey(1, 2), plan.StopRoom);
        }, tbInfoJson: tbInfo);
    }

    [Fact]
    public void ClassifiesHazardProtectionRequirement()
    {
        WithGraph(HazardShortcutRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);
            Assert.Equal(2, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    // No gate-free route AND the only path crosses a survivable hazard → offer
    // the direct route (HasFreeRoute false) so the walk can carry / buy / `use`
    // the counter instead of aborting with "a room hazard you can't survive".
    [Fact]
    public void OffersHazardOnlyRoute_WhenNoFreeAlternative()
    {
        WithGraph(HazardOnlyRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);        // every path crosses the hazard
            Assert.Empty(choice.FreePath);
            Assert.Equal(2, choice.GatedStepCount);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    //  1/1 ──E(Trap)── 1/9 (Spell 700, hazard)   trapped 1-hop approach
    //   │               │
    //   S               E (from 1/2, clean)
    //   └──── 1/2 ──────┘                         trap-free 2-hop approach
    // The hazard gates EVERY entry to 1/9, so there's no free route — but one
    // approach is trapped and the other isn't (report paradigm-20260825-125954,
    // room 17/808: manhole-gated, one exit also a 250-damage trap).
    private const string HazardTrapApproachRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/9 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Detour", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "0", "E": "1/9", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Hazard Vault", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void HazardSoleRoute_PrefersTheTrapFreeApproach()
    {
        // The reported compound case: the destination is hazard-gated on every side
        // (no free route), but one approach also crosses a trap. The forced hazard
        // crossing must take the fewest-traps approach — the longer, trap-free one —
        // not the shortest-by-hops approach that eats the trap.
        WithGraph(HazardTrapApproachRoomsJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Equal(2, choice.GatedStepCount);     // the trap-free detour, not the 1-hop trap
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) },
                choice.GatedPath);
            Assert.Equal(0, bfs.CountTrapsOnPath(new RoomKey(1, 1),
                new[] { Direction.S, Direction.E }));    // chosen route crosses no trap
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);   // still the manhole gate
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 9) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter
        });
    }

    // The hazard-counter shop resolver scores dist(cur→shop→dest), but the dest
    // sits behind the very hazard the counter answers — so no shop qualifies with
    // the hazard live. Guards ResolveHazardCounter's suspend scope: without it a
    // buyable counter is silently never offered (the FCCO "buy a rope" bug).
    [Fact]
    public void HazardCounterShop_SelectableOnlyWithGatesSuspended()
    {
        WithGraph(ShopBehindHazardRoomsJson, (bfs, graph, filter) =>
        {
            Func<RoomKey, RoomKey, int?> dist = (a, b) => bfs.DistanceBetween(a, b, filter);
            RoomKey[] shops = { new(1, 2) };

            // Hazard live: the dest is unreachable from the shop, so nothing scores.
            Assert.False(PathItemShopRouter.TrySelectShop(
                shops, new RoomKey(1, 1), new RoomKey(1, 9), dist, out _));

            // Gates suspended (counter in hand): the round-trip resolves, shop wins.
            using (filter.SuspendAcquirableGates())
            {
                Assert.True(PathItemShopRouter.TrySelectShop(
                    shops, new RoomKey(1, 1), new RoomKey(1, 9), dist, out RoomKey shop));
                Assert.Equal(new RoomKey(1, 2), shop);
            }
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no counter carried
        });
    }

    // ----- EvaluateTeleport: walk-vs-teleport fork -------------------

    [Fact]
    public void OffersTeleportChoice_WhenTeleportShortcuts_AndWalkExists()
    {
        WithTeleportGraph(TeleportShortcutJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.Teleport, choice!.Kind);
            Assert.Equal(3, choice.FreeStepCount);      // walking route
            Assert.Equal(1, choice.GatedStepCount);     // teleport hop
            Assert.Empty(choice.Requirements);          // no item gate on a teleport
            Assert.NotNull(choice.TeleportLanding);
            Assert.Contains("Vault", choice.TeleportLanding!);

            // Free path is the pure-walking detour; gated path is the teleport hop.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3), new RoomKey(1, 9) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenNoTeleportOnRoute()
    {
        WithGraph(NoTeleportJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Shortest route already walks the whole way — nothing to weigh.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenTeleportIsOnlyRoute()
    {
        WithTeleportGraph(TeleportOnlyJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // No walking alternative — there's no fork, so the walker just takes
            // the teleport rather than prompting.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTeleportChoice_WhenTeleportSavesOnlyOneStep()
    {
        WithTeleportGraph(TeleportSavesOneJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // Teleport 1 hop, walk 2 hops — a single room saved isn't worth
            // interrupting the walk to weigh against the teleport's danger.
            Assert.Null(choice);
        });
    }

    // The refuseTeleports pass backs the picker's "walk it" choice: the default
    // pass takes the teleport as the shortest route; refusing it returns the
    // pure-walking route.
    [Fact]
    public void Bfs_RefuseTeleports_ReturnsWalkingRouteNotTeleport()
    {
        WithTeleportGraph(TeleportShortcutJson, (bfs, graph, _) =>
        {
            IReadOnlyList<Direction>? tele = bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 9), null);
            Assert.NotNull(tele);
            Assert.Single(tele!);   // the 1-hop teleport

            IReadOnlyList<Direction>? walk = bfs.FindPath(
                new RoomKey(1, 1), new RoomKey(1, 9), null, refuseTeleports: true);
            Assert.NotNull(walk);
            Assert.Equal(3, walk!.Count);   // the 3-hop walking detour
        });
    }

    // ----- EvaluateTrapAvoid: avoid-traps fork -----------------------
    //
    //  1/1 ──N (Trap)── 1/2 (goal)   trapped 1-hop shortcut
    //   │                │
    //   E                W
    //   └──── 1/3 ──N────┘           clean 2-hop detour
    //
    private const string TrapForkJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Detour",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // No trap anywhere — a plain two-hop corridor.
    private const string NoTrapJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The trapped exit is the ONLY way to the goal — no clean alternative.
    private const string TrapOnlyJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    //  1/1 ──N(Trap)── 1/2 ──N(Trap)── 1/4 (goal)   shortest 2 traps; the 1/2→1/4
    //   └──── E,1/3 ──N────┘                         trap is unavoidable, the other isn't
    private const string MultiTrapJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Fork",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2 (Trap)", "S": "0", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4 (Trap)", "S": "1/1", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Detour",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OffersTrapAvoidChoice_WhenShortestCrossesTrap_AndCleanRouteExists()
    {
        WithGraph(TrapForkJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
            Assert.Equal(2, choice.FreeStepCount);      // trap-free detour
            Assert.Equal(1, choice.GatedStepCount);     // trapped shortcut
            Assert.Equal(0, choice.FreeTrapCount);      // detour is fully clean
            Assert.Equal(1, choice.GatedTrapCount);     // shortcut crosses the trap
            Assert.Empty(choice.Requirements);
            Assert.True(choice.HasFreeRoute);

            // Free path is the clean detour; gated path is the trapped shortcut.
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 3), new RoomKey(1, 2) },
                choice.FreePath);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void OffersTrapAvoidChoice_WhenFewerButNotZeroTraps()
    {
        // The reported case: a route with an avoidable AND an unavoidable trap. The
        // fork must still fire, offering the fewest-traps route (1 trap) over the
        // shortest (2 traps) — not give up because it can't be made fully clean.
        WithGraph(MultiTrapJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 4));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
            Assert.Equal(3, choice.FreeStepCount);      // longer detour
            Assert.Equal(2, choice.GatedStepCount);     // shortest
            Assert.Equal(1, choice.FreeTrapCount);      // dodged one, kept the unavoidable
            Assert.Equal(2, choice.GatedTrapCount);     // shortest crosses both
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 3), new RoomKey(1, 2), new RoomKey(1, 4) },
                choice.FreePath);
        });
    }

    [Fact]
    public void NoTrapAvoidChoice_WhenRouteCrossesNoTrap()
    {
        WithGraph(NoTrapJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            // Nothing to weigh — the shortest route is already trap-free.
            Assert.Null(choice);
        });
    }

    [Fact]
    public void NoTrapAvoidChoice_WhenTrapIsUnavoidable()
    {
        WithGraph(TrapOnlyJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 2));

            // The fewest-traps route crosses the same one trap as the shortest — it
            // can't dodge any, so there's nothing better to offer. Walker crosses and
            // disarms, no fork.
            Assert.Null(choice);
        });
    }

    // ----- Teleport / TrapAvoid forks defer to an obtainable shortcut ----
    //
    // The pre-Evaluate forks weigh only gate-honoured routes; without a deferral they
    // preempt Evaluate's obtain/cross even when buying a boat opens a far shorter route
    // (the same gap the avoid-override fork had — report paradigm-20260915-182554).

    // Teleport route (1/1 =tele=> 1/5 -E- 1/6 -E- 1/9, 3 hops) vs pure walk (1/1-N-1/2
    // -N-1/3-N-1/4-N-1/7-N-1/9, 5 hops) — teleport fires. But a boat opens 1/1-S-1/8
    // (River 700)-S-1/9 (2 hops), shorter than the teleport. 1/5 is teleport-only.
    private const string TeleportObtainBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Landing", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "L2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "W1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "W2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "W3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/7", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "W4", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/7", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // CMD-5 chain teleports to 1/5 (matching the (Item: 5) exit target so it promotes).
    private const string TeleportTo5TbInfo =
        """[ { "Number": 5, "Action": "go arch:teleport 5 1\n" } ]""";

    [Fact]
    public void NoTeleportChoice_WhenAnObtainableRouteBeatsTheTeleport()
    {
        WithGraph(TeleportObtainBypassJson, (bfs, graph, filter) =>
        {
            // The teleport would otherwise be offered (3 hops vs a 5-hop walk), but a
            // boat opens a 2-hop river route shorter than the teleport → defer.
            Assert.Null(RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        tbInfoJson: TeleportTo5TbInfo,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // Teleport 1/1 → 1/5 (1 hop) against a 4-hop walk there, then 3 hops on to 1/9:
    // teleport route 4, walking route 7. An item opens 1/5 → 1/9 directly, so with
    // the item the route is 2 hops by the teleport but 5 on foot.
    private const string TeleportThenItemShortcutJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "CMD": 5,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5 (Item: 5)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "W1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "W2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "W3",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/5", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Landing",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/9 (Item: 42)", "E": "1/6", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "L1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/7", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "L2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/7",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // An obtainable route that is shorter only because it takes the same teleport is
    // no alternative to the teleport. Deferring on it dropped the walk-vs-teleport
    // question, and the item fork's picks then walked the long way round.
    [Fact]
    public void OffersTeleportChoice_WhenTheObtainableRouteNeedsTheSameTeleport()
    {
        WithGraph(TeleportThenItemShortcutJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id != 42;   // lacking only the shortcut's item

            RouteChoice? choice = RouteChoicePlanner.EvaluateTeleport(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.Teleport, choice!.Kind);
            Assert.Equal(7, choice.FreeStepCount);
            Assert.Equal(4, choice.GatedStepCount);
            Assert.Contains("Landing", choice.TeleportLanding!);
        },
        tbInfoJson: TeleportTo5TbInfo);
    }

    // Trapped shortest (1/1-E(Trap)-1/5-E-1/6-E-1/9, 3 hops) vs a 4-hop trap-free
    // detour — trap-avoid fires. But a boat opens a 2-hop trap-free river route.
    private const string TrapObtainBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "T1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "T2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/4", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void NoTrapAvoidChoice_WhenAnObtainableTrapFreeRouteIsShorter()
    {
        WithGraph(TrapObtainBypassJson, (bfs, graph, filter) =>
        {
            // Trap-avoid would otherwise fire (3-hop trapped vs 4-hop clean detour), but
            // a boat opens a 2-hop trap-free river route → defer to Evaluate.
            Assert.Null(RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // TrapObtainBypass, but the river crossing (1/8 → 1/9) is itself trapped, so the
    // boat route isn't clean — the trap-avoid deferral must NOT fire.
    private const string TrapObtainBypassTrappedRiverJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5 (Trap)", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "T1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/6", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "T2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9 (Trap)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/4", "E": "0", "W": "1/6",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Guard: the trap-avoid deferral must NOT fire when the obtainable route still
    // crosses a trap — the trap warning has to stand.
    [Fact]
    public void KeepsTrapAvoidChoice_WhenTheObtainableRouteAlsoTraps()
    {
        WithGraph(TrapObtainBypassTrappedRiverJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.EvaluateTrapAvoid(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);   // boat route is trapped too → trap-avoid still offered
            Assert.Equal(RouteChoiceKind.TrapAvoid, choice!.Kind);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    // ----- EvaluateAvoidOverride: route-through-avoided-rooms fork ----
    //
    // 1/1 ──E── 1/5 ──E── 1/9   the ONLY route to 1/9 runs through 1/5.
    private const string AvoidSoleJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A 2-hop route through avoided 1/5 vs a 4-hop avoid-free detour (saves 2).
    // 1/1 ──E── 1/5 ──N── 1/9   AND   1/1 ──N── 1/2 ──N── 1/3 ──E── 1/4 ──E── 1/9
    private const string AvoidTwoRouteJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Mid3",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/4",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A 2-hop route through avoided 1/5 vs a 3-hop avoid-free detour (saves only 1).
    // 1/1 ──E── 1/5 ──N── 1/9   AND   1/1 ──N── 1/2 ──E── 1/3 ──E── 1/9
    private const string AvoidTwoRouteSmallJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "0", "E": "0", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "1/3", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Goal",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void OffersSoleAvoidOverride_WhenAvoidWallsTheOnlyRoute()
    {
        WithGraph(AvoidSoleJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the only path room, marked avoid

            RouteChoice? choice = RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.AvoidOverride, choice!.Kind);
            Assert.False(choice.HasFreeRoute);          // no avoid-honouring route
            Assert.Empty(choice.FreePath);
            Assert.Equal(2, choice.GatedStepCount);
            Assert.Equal(1, choice.AvoidedRoomCount);   // routes through 1 avoided room
            // Named, so the card can say which room it means.
            Assert.Equal(new[] { "Avoided (1/5)" }, choice.AvoidedRoomNames);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void ListAvoided_NamesTheFirstFew_AndCountsTheRest()
    {
        Assert.Equal("", RouteChoicePlanner.ListAvoided(null));
        Assert.Equal("A (1/1), B (1/2)", RouteChoicePlanner.ListAvoided(new[] { "A (1/1)", "B (1/2)" }));
        Assert.Equal("A, B and 2 more", RouteChoicePlanner.ListAvoided(new[] { "A", "B", "C", "D" }, show: 2));
    }

    [Fact]
    public void OffersTwoRouteAvoidOverride_WhenAvoidRouteIsMuchShorter()
    {
        WithGraph(AvoidTwoRouteJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));

            RouteChoice? choice = RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(RouteChoiceKind.AvoidOverride, choice!.Kind);
            Assert.True(choice.HasFreeRoute);           // an avoid-honouring route exists
            Assert.Equal(4, choice.FreeStepCount);      // the avoid-free detour
            Assert.Equal(2, choice.GatedStepCount);     // the shorter route through 1/5
            Assert.Equal(1, choice.AvoidedRoomCount);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                choice.GatedPath);
        });
    }

    [Fact]
    public void NoAvoidOverride_WhenAvoidRouteSavesTooLittle()
    {
        WithGraph(AvoidTwoRouteSmallJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));

            // Through-avoid saves only 1 room over the avoid-free route — the user's
            // deliberate avoid stands, no override offered.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));
        });
    }

    [Fact]
    public void AvoidOverride_OnAOneStepSaving_WhenTheWalkAsksOverEveryAvoid()
    {
        WithGraph(AvoidTwoRouteSmallJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));

            // A Sell Tour walk asks whenever an avoided room is on the shortest route,
            // so the same one-room saving now shows the cards.
            RouteChoice? choice = RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9), minSavings: 1);

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.FreeStepCount);
            Assert.Equal(2, choice.GatedStepCount);
        });
    }

    [Fact]
    public void NoAvoidOverride_WhenRouteTouchesNoAvoidedRoom()
    {
        WithGraph(AvoidTwoRouteJson, (bfs, graph, filter) =>
            // Nothing marked avoid → the shortest route touches no avoided room, so
            // there's no override to offer.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }

    // Report paradigm-20260907-212758: the ONLY avoid-respecting route crosses a
    // hazard room (a river you'd cross with a bought raft); the only avoid-FREE
    // route runs through a marked-avoid room. The picker must NOT claim "no route
    // respects your avoids" — an obtainable counter opens an avoid-respecting route,
    // so EvaluateAvoidOverride defers and Evaluate surfaces the obtain/cross options.
    //   avoid-respecting: 1/1 ──N── 1/2 (River, Spell 700, counter 42) ──N── 1/9
    //   avoid-crossing:   1/1 ──E── 1/5 (Avoided) ──E── 1/9
    private const string AvoidWithHazardBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void AvoidOverride_DefersWhenAnAcquirableCounterOpensAnAvoidRespectingRoute()
    {
        WithGraph(AvoidWithHazardBypassJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the only avoid-FREE route runs through here

            // The avoid-override must NOT fire: a raft (item 42) opens the river route,
            // which respects the avoid. Deferred so the item-gate fork handles it.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            // And Evaluate surfaces that river crossing as a sole hazard route
            // (obtain / cross-unprotected), respecting the avoid.
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) },
                choice.GatedPath);   // the avoid-respecting river route, not through 1/5

            // The avoid-crossing route (through 1/5) is available as the EXTRA card:
            // it needs no counter, so it's a real alternative to the raft crossing.
            var alt = RouteChoicePlanner.AvoidAlternative(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(alt);
            Assert.Equal(1, alt!.Value.AvoidedCount);
            Assert.Single(alt.Value.AvoidedNames);
            Assert.EndsWith("(1/5)", alt.Value.AvoidedNames[0]);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 9) },
                alt.Value.Path);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no raft carried
        });
    }

    // Report paradigm-20260915-182554 (the TWO-ROUTE twin of the sole case above):
    // an avoid-honouring route EXISTS (a long detour), and a shorter route through a
    // marked-avoid room exists — but OBTAINING a boat opens an even shorter avoid-
    // respecting river crossing. The two-route avoid-override must defer so Evaluate
    // offers "buy a boat and sail" instead of nagging about the avoid.
    //   detour (avoid + gate honoured, 5 hops): 1/1-N-1/2-N-1/3-N-1/4-N-1/6-N-1/9
    //   avoid-crossing  (3 hops):               1/1-E-1/5(Avoided)-E-1/7-E-1/9
    //   boat (hazard, 2 hops, gate-suspended):  1/1-S-1/8(River 700)-S-1/9
    private const string AvoidTwoRouteWithHazardBypassJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/8", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "D1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "D2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D3", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/6", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "D4", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/9", "S": "1/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Avoided", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/7", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "AvoidMid", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "River", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/9", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/8", "S": "1/6", "E": "0", "W": "1/7",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void NoTwoRouteAvoidOverride_WhenAnAcquirableCounterOpensAShorterAvoidRespectingRoute()
    {
        WithGraph(AvoidTwoRouteWithHazardBypassJson, (bfs, graph, filter) =>
        {
            filter.MarkAvoided(new RoomKey(1, 5));   // the avoid-crossing shortcut runs through here

            // Two-route avoid-override would otherwise fire (5-hop detour vs 3-hop
            // through-avoid, saves 2) — but a boat opens a 2-hop avoid-respecting river
            // route, so it must defer to Evaluate instead of offering the override.
            Assert.Null(RouteChoicePlanner.EvaluateAvoidOverride(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9)));

            // Evaluate surfaces the river crossing (obtain boat / cross), avoid-respecting.
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));
            Assert.NotNull(choice);
            RouteRequirement req = Assert.Single(choice!.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);   // the river route, not through avoided 1/5
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 8) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;   // no boat carried
        });
    }

    [Fact]
    public void AvoidAlternative_IsNull_WhenNothingIsMarkedAvoid()
    {
        WithGraph(AvoidWithHazardBypassJson, (bfs, graph, filter) =>
            // Nothing avoided → the ignore-avoids route touches no marked room, so
            // there's no extra avoid-crossing card to offer.
            Assert.Null(RouteChoicePlanner.AvoidAlternative(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9))));
    }
    // ----- Sourceable gate items (what an "obtain then cross" pick arms) -------
    //
    // Report paradigm-20260911-095404: the pick force-obtained only hazard
    // counters, so an item-gated route armed nothing whenever Settings → Other →
    // "search rooms if item needed" was off — the walk then crossed the Lower
    // Caverns gate having made no arrangements, rubbed an orb it did not carry,
    // and bonked.

    private static readonly Func<int, bool> NoSummons = _ => false;
    private static readonly Func<int, bool> AllSummonable = _ => true;

    [Fact]
    public void SourceableGateItems_IncludesCarryItemsAndTickets()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
            new RouteRequirement(RouteRequirementKind.Ticket, new[] { 9 }),
        };
        Assert.Equal(new[] { 807, 9 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    // The picker resolves hazard counters itself (it may grab one off the current
    // floor instead of detouring) and forces them on its own path.
    [Fact]
    public void SourceableGateItems_ExcludesHazardCounters()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42, 43 }),
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
        };
        Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    // A door key is only worth arming for when a room command can summon a
    // guaranteed dropper; any other key has no source, so forcing it would switch
    // on a per-room search that can never succeed.
    [Fact]
    public void SourceableGateItems_AdmitsDoorKeyOnlyWhenSummonable()
    {
        var reqs = new[] { new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 806 }) };
        Assert.Empty(RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
        Assert.Equal(new[] { 806 }, RouteChoicePlanner.SourceableGateItems(reqs, AllSummonable));
    }

    // The reported route: the orb gate AND the gate key on one sole route.
    [Fact]
    public void SourceableGateItems_MixedRoute_KeepsOrbAndSummonableKey()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
            new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 806 }),
        };
        Assert.Equal(
            new[] { 807, 806 },
            RouteChoicePlanner.SourceableGateItems(reqs, id => id == 806));
    }

    [Fact]
    public void SourceableGateItems_DedupesAndDropsNonPositiveIds()
    {
        var reqs = new[]
        {
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807, 0, 807 }),
            new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 807 }),
        };
        Assert.Equal(new[] { 807 }, RouteChoicePlanner.SourceableGateItems(reqs, NoSummons));
    }

    [Fact]
    public void SourceableGateItems_NoRequirements_IsEmpty()
        => Assert.Empty(RouteChoicePlanner.SourceableGateItems(
            Array.Empty<RouteRequirement>(), NoSummons));

    // 1/5 (host) ──E (opens from 1/2)── 1/9.  The lever room 1/2 is reached from the
    // host only through hazard room 1/7 (Spell 700); 1/2 ──E→ 1/5 leads back.
    private const string LeverBehindHazardRoomsJson = """
        [
          { "Map Number": 1, "Room Number": 5, "Name": "Host", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "1/7", "S": "0", "E": "1/9 (Hidden/Needs 1 Actions, specific order)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Hazard", "Spell": 700, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "1/2", "S": "1/5", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lever", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "1/5", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "Action#1 [on the E exit of room 1/5]: pull lever" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0, "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Report paradigm-20261003-132224: the route was one clear hop, so no fork was
    // offered, and the walk then failed because the lever trip crossed a hazard.
    [Fact]
    public void LeverDetourThroughHazard_IsOfferedAsTheSoleRoute()
    {
        WithGraph(LeverBehindHazardRoomsJson, (bfs, graph, filter) =>
        {
            RoomKey host = new(1, 5), vault = new(1, 9);
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, host, vault));

            RouteChoice? choice = RouteChoicePlanner.EvaluateLeverDetour(bfs, filter, graph, host, vault);

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.HazardProtection, req.Kind);
            Assert.Equal(new[] { 42 }, req.ItemIds);
            Assert.Equal(
                new[] { host, new RoomKey(1, 7), new RoomKey(1, 2), host, vault },
                choice.GatedPath);
            Assert.Equal(4, choice.GatedStepCount);
            Assert.Contains(choice.GatedWalk!, s => s is CommandStep { Command: "pull lever" });
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 7) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
        });
    }

    [Fact]
    public void LeverDetourThroughHazard_NoOfferWhenTheCounterIsCarried()
    {
        WithGraph(LeverBehindHazardRoomsJson, (bfs, graph, filter) =>
        {
            Assert.Null(RouteChoicePlanner.EvaluateLeverDetour(
                bfs, filter, graph, new RoomKey(1, 5), new RoomKey(1, 9)));
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: (index, filter) =>
        {
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 7) ? 700 : 0;
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = id => id == 42;
        });
    }

    // Reports paradigm-20261009-135049 / paradigm-20261009-135314: the only way to
    // 1/9 is a keyed door out of 1/8, and 1/8 is reached through hazard room 1/5 or
    // round it. Planned with every gate stood down, the route took 1/5 as open
    // ground and the card asked for the hazard's counter as well as the key.
    // Through: 1/1 ─E─ 1/5 (Spell 700) ─E─ 1/8 ─E (Key: 7)─ 1/9          (3 hops).
    // Round:   1/1 ─N─ 1/2 ─N─ 1/3 ─E─ 1/8 ─E (Key: 7)─ 1/9             (4 hops).
    private const string KeyedDoorPastAHazardJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/5", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/8", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Mid1", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Mid2", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/8", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "Door", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Key: 7)", "W": "1/5",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Vault", "Spell": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/8",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static void WireHazard700(RoomHazardIndex index, MovementFilter filter)
    {
        filter.Hazards = index;
        filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = _ => false;
    }

    // An ordinary hazard room (a counter can be had for it) is not gone round, however
    // far: the sole route takes the shorter way through it and asks for the counter
    // beside the key, as the all-open plan does. Going round it wherever a way
    // existed sent real walks 100+ steps out of their way.
    [Fact]
    public void SoleKeyRoute_TakesTheShorterWayThroughAnOrdinaryHazardRoom_AndAsksForItsCounter()
    {
        WithGraph(KeyedDoorPastAHazardJson, (bfs, graph, filter) =>
        {
            RoomKey from = new(1, 1), to = new(1, 9);
            IReadOnlyList<Direction>? allOpen;
            using (filter.SuspendAcquirableGates())
                allOpen = bfs.FindPath(from, to, filter);
            Assert.NotNull(allOpen);

            RouteChoice? choice = RouteChoicePlanner.Evaluate(bfs, filter, graph, from, to);

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Equal(allOpen!.Count, choice.GatedStepCount);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice.GatedPath);
            Assert.Equal(
                new[] { RouteRequirementKind.HazardProtection, RouteRequirementKind.DoorKey },
                choice.Requirements.Select(r => r.Kind));
            Assert.Equal(new[] { 42 }, choice.Requirements[0].ItemIds);
            Assert.False(choice.Requirements[0].NoProtection);
            Assert.Equal(new[] { 7 }, choice.Requirements[1].ItemIds);
            Assert.Null(choice.UnprotectedRoomNames);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: WireHazard700);
    }

    // Holding the counter, the hazard room is ordinary ground and the shorter way.
    [Fact]
    public void SoleKeyRoute_CrossesTheHazardRoom_WithItsCounterInHand()
    {
        WithGraph(KeyedDoorPastAHazardJson, (bfs, graph, filter) =>
        {
            filter.ItemCarriedProbe = id => id == 42;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(3, choice!.GatedStepCount);
            Assert.Contains(new RoomKey(1, 5), choice.GatedPath);
            Assert.Equal(RouteRequirementKind.DoorKey, Assert.Single(choice.Requirements).Kind);
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: WireHazard700);
    }

    // With no way round, the route crosses the hazard room as it always has, and
    // lists its counter beside the key.
    [Fact]
    public void SoleKeyRoute_StillCrossesAHazardRoom_ThereIsNoWayRound()
    {
        string noWayRound = KeyedDoorPastAHazardJson.Replace("\"E\": \"1/8\", \"W\": \"0\"", "\"E\": \"0\", \"W\": \"0\"");
        WithGraph(noWayRound, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.Equal(
                new[] { new RoomKey(1, 1), new RoomKey(1, 5), new RoomKey(1, 8), new RoomKey(1, 9) },
                choice!.GatedPath);
            Assert.Equal(
                new[] { RouteRequirementKind.HazardProtection, RouteRequirementKind.DoorKey },
                choice.Requirements.Select(r => r.Kind));
            Assert.Equal(new[] { new RoomKey(1, 5) }, RouteChoicePlanner.UncounteredHazardRooms(filter, choice.GatedPath));
        },
        spellsJson: HazardSpellsJson,
        itemsJson: HazardItemsJson,
        wireHazards: WireHazard700);
    }

    // Crystal Lake: rooms whose spell teleports a boat's holder as well as everyone
    // else (RoomHazardIndexTests.LakeTbInfoJson). No route is planned into them, for
    // any character (user, 2026-10-09). The fixtures' Spell 700 rooms become sea
    // rooms; every case runs at level 25 and 58, with no boat and with a raft, since
    // none of that may make a difference.
    private const string LakeSpellsJson =
        """[ { "Number": 1076, "Abil-0": 115, "AbilVal-0": 66, "Abil-1": 148, "AbilVal-1": 9358 } ]""";

    private static void WithLake(string roomsJson, Action<BfsMapper, RoomGraphManager, MovementFilter> body)
        => WithLake(roomsJson, (bfs, graph, filter, _) => body(bfs, graph, filter));

    // The room spell of the lake room that teleports on to the Bloodwood Weald; like
    // the sea spells it teleports on a roll, which is all the filter is told of it.
    private const int WealdSpell = 5258;

    // meetsTerms: level 50 or over and a boat in the pack, both together, which is
    // what the few crossings the client makes ask (one case in the four).
    // commandTbInfo: one more TBInfo entry, for a fixture whose rooms carry a CMD
    // (a teleport behind a command) beside the lake's own spell chains.
    private static void WithLake(
        string roomsJson, Action<BfsMapper, RoomGraphManager, MovementFilter, bool> body, string? commandTbInfo = null)
    {
        string tbInfo = commandTbInfo is null
            ? RoomHazardIndexTests.LakeTbInfoJson
            : RoomHazardIndexTests.LakeTbInfoJson.Replace("[ {", $"[ {commandTbInfo}, {{");
        foreach (int level in new[] { 25, 58 })
            foreach (bool boat in new[] { false, true })
                WithGraph(roomsJson.Replace("\"Spell\": 700", "\"Spell\": 1076"),
                    (bfs, graph, filter) =>
                    {
                        filter.RoomEntrySpellProbe = key => graph.GetRoom(key)?.Spell ?? 0;
                        body(bfs, graph, filter, level >= 50 && boat);
                    },
                    spellsJson: LakeSpellsJson,
                    wireHazards: (index, filter) =>
                    {
                        filter.Hazards = index;
                        filter.InventoryReadyProbe = () => true;
                        filter.ItemCarriedProbe = id => boat && id == 690;
                        filter.LevelProvider = () => level;
                        filter.SpellTeleportsAtRandomProbe = spell => spell is 1076 or WealdSpell;
                    },
                    tbInfoJson: tbInfo);
    }

    // Report paradigm-20261009-123349: the walk had a free route round the lake and
    // was offered "Direct — acquire then go" through its teleport rooms to save two
    // steps, with a boat to fetch. The free route is now taken with no card, and a
    // boat already in the pack no longer sends the route across.
    [Fact]
    public void Lake_IsWalkedRound_WithNoCardAndNoBoatOffered()
    {
        WithLake(HazardShortcutRoomsJson, (bfs, graph, filter) =>
        {
            RoomKey from = new(1, 1), to = new(1, 9);
            Assert.True(filter.IsClosedToRoutes(new RoomKey(1, 5)));
            Assert.Equal(3, bfs.FindPath(from, to, filter)!.Count);
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, from, to));
            // The plans an automatic trip makes, and the one that asks what
            // obtaining something would open, go round it too.
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.Equal(3, bfs.FindPath(from, to, filter)!.Count);
            using (filter.SuspendAcquirableGatesButUnprotectableHazards())
                Assert.Equal(3, bfs.FindPath(from, to, filter)!.Count);
        });
    }

    // Reports paradigm-20261009-135049 / paradigm-20261009-135314 on the lake itself:
    // one card, the way round, asking for the key and no boat.
    [Fact]
    public void SoleKeyRoute_PastTheLake_GoesRoundIt_AndAsksForTheKeyAlone()
    {
        WithLake(KeyedDoorPastAHazardJson, (bfs, graph, filter) =>
        {
            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            Assert.NotNull(choice);
            Assert.False(choice!.HasFreeRoute);
            Assert.Equal(4, choice.GatedStepCount);
            Assert.DoesNotContain(new RoomKey(1, 5), choice.GatedPath);
            RouteRequirement req = Assert.Single(choice.Requirements);
            Assert.Equal(RouteRequirementKind.DoorKey, req.Kind);
            Assert.Null(choice.UnprotectedRoomNames);
            Assert.Null(choice.ShortcutPath);   // no "get a raft, saves a step" card either
        });
    }

    // "only typed moves should enter the lake" (user, 2026-10-10): a sea room is
    // never a walk's destination, for a walk the user starts or one the client
    // does, whatever the level and with or without a boat. No route, and no card.
    [Fact]
    public void DestinationInTheLake_HasNoRouteAndNoCard()
    {
        WithLake(LakeCrossingJson, (bfs, graph, filter) =>
        {
            RoomKey from = new(1, 1), inTheLake = new(1, 3);
            Assert.Null(bfs.FindPath(from, inTheLake, filter));
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.Null(bfs.FindPath(from, inTheLake, filter));
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, from, inTheLake));
            Assert.Null(bfs.FindCrossing(from, inTheLake, filter));
        });
    }

    // Stock's White Forest: a place the map reaches across the lake and no other
    // way (here the island, 1/10). A walk is taken across to it and back out of it,
    // one the client starts as much as one the user does, with no card; but only at
    // level 50 or over with a boat in the pack, both together, and by the crossing
    // with the fewest sea rooms rather than the fewest steps.
    [Fact]
    public void PlaceReachedOnlyAcrossTheLake_IsCrossedTo_WithLevelAndBoat_ByTheFewestSeaRooms()
    {
        WithLake(LakeCrossingJson, (bfs, graph, filter, meetsTerms) =>
        {
            RoomKey shore = new(1, 1), island = new(1, 10), bank = new(1, 9);
            Assert.True(bfs.IsCutOffByClosedRooms(shore, island, filter));
            Assert.Equal(meetsTerms, filter.MayCrossClosedRooms());

            IReadOnlyList<Direction>? there = bfs.FindPath(shore, island, filter);
            IReadOnlyList<Direction>? back = bfs.FindPath(island, bank, filter);
            // The plan an automatic trip makes through gates is the same.
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.Equal(there?.Count, bfs.FindPath(shore, island, filter)?.Count);
            // Either way there is nothing for a card to offer.
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, shore, island));

            if (!meetsTerms)
            {
                Assert.Null(there);
                Assert.Null(back);
                return;
            }
            Assert.Equal(new[] { shore, new RoomKey(1, 2), island }, RouteChoicePlanner.BuildKeyPath(graph, shore, there!));
            // Out by the one sea room to the west shore and round by the path (7
            // steps), not east across three of them (5 steps).
            Assert.Equal(
                new[] { island, new RoomKey(1, 2), shore, new RoomKey(1, 6), new RoomKey(1, 7), new RoomKey(1, 8),
                        new RoomKey(1, 5), bank },
                RouteChoicePlanner.BuildKeyPath(graph, island, back!));
        });
    }

    // The Isle of Bones: the lake surrounds it too, but there is another way in (a
    // keyed door here), so it is never reached across the lake, whoever asks: the
    // route card is the key's.
    [Fact]
    public void PlaceWithAnotherWayIn_IsNeverReachedAcrossTheLake()
    {
        // The island's row is the one whose north is 1/2, the bank's the one whose
        // west is 1/5.
        string isle = LakeCrossingJson
            .Replace("\"N\": \"1/2\", \"S\": \"0\", \"E\": \"0\"", "\"N\": \"1/2\", \"S\": \"0\", \"E\": \"1/9 (Key: 7)\"")
            .Replace("\"N\": \"0\", \"S\": \"0\", \"E\": \"0\", \"W\": \"1/5\"",
                     "\"N\": \"0\", \"S\": \"1/10 (Key: 7)\", \"E\": \"0\", \"W\": \"1/5\"");
        Assert.Contains("\"S\": \"1/10 (Key: 7)\"", isle);
        Assert.Contains("\"E\": \"1/9 (Key: 7)\"", isle);
        WithLake(isle, (bfs, graph, filter) =>
        {
            RoomKey shore = new(1, 1), island = new(1, 10);
            Assert.False(bfs.IsCutOffByClosedRooms(shore, island, filter));
            Assert.Null(bfs.FindPath(shore, island, filter));
            Assert.Null(bfs.FindCrossing(shore, island, filter));

            RouteChoice? choice = RouteChoicePlanner.Evaluate(bfs, filter, graph, shore, island);
            Assert.NotNull(choice);
            Assert.Equal(RouteRequirementKind.DoorKey, Assert.Single(choice!.Requirements).Kind);
            Assert.DoesNotContain(choice.GatedPath, filter.IsClosedToRoutes);
            Assert.Null(choice.UnprotectedRoomNames);
        });
    }

    // The Isle of Bones is reached by a command or greet teleport on dry land (here
    // from 1/11, behind a keyed door off the west shore), and the lake merely
    // surrounds it. That teleport counts as a way in: the island is never crossed to,
    // and the route is the land one, asking for the key. Take the teleport out of
    // the map and the island is the lake's alone, crossed to on the terms.
    [Fact]
    public void IsleReachedByATeleportOnLand_IsNeverCrossedTo_ButIsOnceTheTeleportIsGone()
    {
        string withGatehouse = LakeCrossingJson
            .Replace("\"N\": \"1/6\", \"S\": \"0\", \"E\": \"1/2\", \"W\": \"0\"",
                     "\"N\": \"1/6\", \"S\": \"0\", \"E\": \"1/2\", \"W\": \"1/11 (Key: 7)\"")
            .Replace("[\n", """
                [
                  { "Map Number": 1, "Room Number": 11, "Name": "Gatehouse", "Spell": 0, "CMD": 5,
                    "N": "0", "S": "0", "E": "1/10 (Item: 5)", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

                """, StringComparison.Ordinal);
        Assert.Contains("\"W\": \"1/11 (Key: 7)\"", withGatehouse);
        Assert.Contains("\"Name\": \"Gatehouse\"", withGatehouse);
        const string teleportTo10 = """{ "Number": 5, "Action": "go arch:teleport 10 1\n" }""";
        RoomKey shore = new(1, 1), gatehouse = new(1, 11), island = new(1, 10);

        WithLake(withGatehouse, (bfs, graph, filter, _) =>
        {
            Assert.Equal(RoomExitHint.Teleport, graph.GetRoom(gatehouse)!.Exits[Direction.E].Hint);
            Assert.False(bfs.IsCutOffByClosedRooms(shore, island, filter));
            Assert.Null(bfs.FindPath(shore, island, filter));
            Assert.Null(bfs.FindCrossing(shore, island, filter));

            RouteChoice? card = RouteChoicePlanner.Evaluate(bfs, filter, graph, shore, island);
            Assert.NotNull(card);
            Assert.Equal(new[] { shore, gatehouse, island }, card!.GatedPath);
            Assert.Contains(card.Requirements, r => r.Kind == RouteRequirementKind.DoorKey && r.ItemIds.Contains(7));
            Assert.DoesNotContain(card.GatedPath, filter.IsClosedToRoutes);
            Assert.Null(card.UnprotectedRoomNames);
        }, teleportTo10);

        // The same map with no teleport out of the gatehouse: only the lake is left.
        string withoutTeleport = withGatehouse
            .Replace("\"E\": \"1/10 (Item: 5)\"", "\"E\": \"0\"")
            .Replace("\"CMD\": 5", "\"CMD\": 0");
        Assert.DoesNotContain("1/10 (Item: 5)", withoutTeleport);
        Assert.DoesNotContain("\"CMD\": 5", withoutTeleport);
        WithLake(withoutTeleport, (bfs, graph, filter, meetsTerms) =>
        {
            Assert.True(bfs.IsCutOffByClosedRooms(shore, island, filter));
            IReadOnlyList<Direction>? there = bfs.FindPath(shore, island, filter);
            if (!meetsTerms)
            {
                Assert.Null(there);
                return;
            }
            Assert.Equal(
                new[] { shore, new RoomKey(1, 2), island }, RouteChoicePlanner.BuildKeyPath(graph, shore, there!));
        }, teleportTo10);
    }

    // The sole card for a place only the lake reaches, when another gate stands on
    // the way to the lake (an item at the gatehouse door): the card names the rooms
    // it crosses, as the card for the room that teleports on does.
    [Fact]
    public void PlaceOnlyTheLakeReaches_WithAnotherGateOnTheWay_NamesTheRoomsItCrosses()
    {
        string gated = LakeCrossingJson
            .Replace("\"N\": \"1/6\", \"S\": \"0\", \"E\": \"1/2\", \"W\": \"0\"",
                     "\"N\": \"1/6\", \"S\": \"0\", \"E\": \"1/2\", \"W\": \"1/12\"")
            .Replace("[\n", """
                [
                  { "Map Number": 1, "Room Number": 12, "Name": "Gatehouse", "Spell": 0,
                    "N": "0", "S": "0", "E": "1/1 (Item: 5)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },

                """, StringComparison.Ordinal);
        Assert.Contains("\"Name\": \"Gatehouse\"", gated);
        WithLake(gated, (bfs, graph, filter, meetsTerms) =>
        {
            RoomKey from = new(1, 12), island = new(1, 10);
            RouteChoice? card = RouteChoicePlanner.Evaluate(bfs, filter, graph, from, island);
            if (!meetsTerms)
            {
                Assert.Null(card);
                return;
            }
            Assert.NotNull(card);
            Assert.False(card!.HasFreeRoute);
            Assert.Contains(card.Requirements, r => r.NoProtection);
            Assert.Contains(card.Requirements, r => !r.NoProtection);
            Assert.Equal(new[] { "Lake (1/2)" }, card.UnprotectedRoomNames);
        });
    }

    // The walk takes a place only the lake reaches across the lake, and says so once:
    // how many teleporting rooms, which, and why it was allowed.
    [Fact]
    public void WalkAcrossTheLake_LogsTheCrossingOnce_AndAWalkRoundItLogsNothing()
    {
        WithLake(LakeCrossingJson, (bfs, graph, filter, meetsTerms) =>
        {
            List<LogEntry> lines = new();
            LogService log = new();
            log.EntryAdded += lines.Add;
            AutoWalkManager Walker()
            {
                RoomTracker tracker = new(graph);
                tracker.SetLocated(new RoomKey(1, 1));
                AutoWalkManager walker = new(graph, bfs, tracker, new MovementCoordinator(), filter, log);
                walker.SetItemNameResolver(id => id == 690 ? "log raft" : "wooden skiff");
                walker.SetWireSender(_ => { });
                return walker;
            }

            Assert.True(Walker().WalkTo(new RoomKey(1, 9)));   // round by the path
            Assert.DoesNotContain(lines, l => l.Message.Contains("allowed because no other way", StringComparison.Ordinal));

            bool walks = Walker().WalkTo(new RoomKey(1, 10));
            Assert.Equal(meetsTerms, walks);
            LogEntry[] said = lines.Where(l => l.Message.Contains("allowed because no other way", StringComparison.Ordinal)).ToArray();
            if (!meetsTerms)
            {
                Assert.Empty(said);
                return;
            }
            LogEntry line = Assert.Single(said);
            Assert.Equal(LogSeverity.Info, line.Severity);
            Assert.Equal("Walker", line.Source);
            Assert.Equal(
                "walk to 1/10: crosses 1 teleporting room(s), 1/2 to 1/2; allowed because no other way there exists "
                + "and the character is level 50+ with log raft or wooden skiff in the pack",
                line.Message);
        });
    }

    // The one card that crosses the lake: a walk the user starts to the room that
    // teleports on to the Bloodwood Weald (here the island, given that spell), at
    // level 50 or over with a boat, "both ... together" (user, 2026-10-10). With
    // either missing there is no card and no route; and no walk the client starts
    // is ever planned there, terms met or not.
    [Fact]
    public void RoomThatTeleportsOn_IsOfferedOnACard_OnlyWithLevelAndBoat_AndNeverToAnAutomaticWalk()
    {
        string weald = LakeCrossingJson.Replace("\"Name\": \"Island\", \"Spell\": 0", $"\"Name\": \"Island\", \"Spell\": {WealdSpell}");
        WithLake(weald, (bfs, graph, filter, meetsTerms) =>
        {
            RoomKey shore = new(1, 1), room = new(1, 10);
            Assert.True(filter.TeleportsOnArrival(room));
            Assert.False(filter.IsClosedToRoutes(room));

            // What any walk plans for itself, the user's plain walk included: nothing.
            Assert.Null(bfs.FindPath(shore, room, filter));
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.Null(bfs.FindPath(shore, room, filter));

            RouteChoice? card = RouteChoicePlanner.Evaluate(bfs, filter, graph, shore, room);
            if (!meetsTerms)
            {
                Assert.Null(card);
                return;
            }
            Assert.NotNull(card);
            Assert.False(card!.HasFreeRoute);
            Assert.Equal(new[] { shore, new RoomKey(1, 2), room }, card.GatedPath);
            RouteRequirement req = Assert.Single(card.Requirements);
            Assert.True(req.NoProtection);
            Assert.Empty(RouteChoicePlanner.SourceableGateItems(card.Requirements, NoSummons));
            Assert.Equal(new[] { "Lake (1/2)" }, card.UnprotectedRoomNames);
            // Picking it agrees to the sea room on its route, and the walk it commits
            // opens that room and no other.
            Assert.Equal(new[] { new RoomKey(1, 2) }, RouteChoicePlanner.UncounteredHazardRooms(filter, card.GatedPath));
            using (filter.SuspendAcquirableGatesExcept(
                Array.Empty<int>(), keepUncounteredHazards: true, openHazardRooms: new[] { new RoomKey(1, 2) }))
                Assert.Equal(2, bfs.FindPath(shore, room, filter)!.Count);
        });
    }

    // What the walk itself says when it has no route for the lake, in one line: only
    // typed moves go into the sea rooms; and for a place only the lake reaches, what
    // the crossing takes and which of it is missing.
    [Fact]
    public void WalkWithNoRouteForTheLake_SaysWhy()
    {
        string weald = LakeCrossingJson.Replace("\"Name\": \"Island\", \"Spell\": 0", $"\"Name\": \"Island\", \"Spell\": {WealdSpell}");
        foreach (string rooms in new[] { LakeCrossingJson, weald })
            WithLake(rooms, (bfs, graph, filter, meetsTerms) =>
            {
                string? Refusal(RoomKey destination)
                {
                    RoomTracker tracker = new(graph);
                    tracker.SetLocated(new RoomKey(1, 1));
                    AutoWalkManager walker = new(graph, bfs, tracker, new MovementCoordinator(), filter);
                    walker.SetItemNameResolver(id => id == 690 ? "log raft" : "wooden skiff");
                    walker.SetWireSender(_ => { });
                    string? detail = null;
                    walker.Event += e => { if (e.Kind == WalkEventKind.Failed) detail = e.Detail; };
                    return walker.WalkTo(destination) ? null : detail;
                }

                Assert.Equal(
                    "no route: the way there crosses 2 teleporting room(s), from 1/2 (Lake) on, and only typed moves go into those",
                    Refusal(new RoomKey(1, 3)));

                bool teleportsOn = filter.TeleportsOnArrival(new RoomKey(1, 10));
                string? island = Refusal(new RoomKey(1, 10));
                if (meetsTerms && !teleportsOn)
                    Assert.Null(island);   // the White Forest case: it walks
                else if (meetsTerms)
                    Assert.Equal(
                        "no route: the only way there is across the teleporting rooms from 1/2 (Lake) on, a crossing "
                        + "offered only on the route card of a walk you start yourself",
                        island);
                else
                {
                    Assert.StartsWith(
                        "no route: the only way there is across the teleporting rooms from 1/2 (Lake) on, which takes "
                        + "level 50 and log raft or wooden skiff in your pack (missing: ", island);
                    Assert.Equal(filter.LevelProvider!() < 50, island!.Contains("missing: level 50"));
                    Assert.Equal(!filter.ItemCarriedProbe!(690), island.Contains("log raft or wooden skiff)"));
                }
            });
    }

    // A lever trip is planned like the rest: where the lever sits beyond a sea room,
    // no card offers that crossing.
    [Fact]
    public void LeverBeyondTheLake_IsNeverOfferedOnACard()
    {
        WithLake(LeverBehindHazardRoomsJson, (bfs, graph, filter) =>
            Assert.Null(RouteChoicePlanner.EvaluateLeverDetour(bfs, filter, graph, new RoomKey(1, 5), new RoomKey(1, 9))));
    }

    // West shore 1/1, three sea rooms 1/2–1/4, east shore 1/5, destination 1/9 beyond
    // it; the way round the lake is 1/1 → 1/6 → 1/7 → 1/8 → 1/5. 1/10 is an island:
    // out of the spell area, with no way off but back into it.
    private const string LakeCrossingJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "West Shore", "Spell": 0,
            "N": "1/6", "S": "0", "E": "1/2", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lake", "Spell": 700,
            "N": "0", "S": "1/10", "E": "1/3", "W": "1/1", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Lake", "Spell": 700,
            "N": "0", "S": "0", "E": "1/4", "W": "1/2", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake", "Spell": 700,
            "N": "0", "S": "0", "E": "1/5", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "East Shore", "Spell": 0,
            "N": "1/8", "S": "0", "E": "1/9", "W": "1/4", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Path", "Spell": 0,
            "N": "0", "S": "1/1", "E": "1/7", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 7, "Name": "Path", "Spell": 0,
            "N": "0", "S": "0", "E": "1/8", "W": "1/6", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 8, "Name": "Path", "Spell": 0,
            "N": "0", "S": "1/5", "E": "0", "W": "1/7", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Bank", "Spell": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/5", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Island", "Spell": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Teleported onto the lake, or part-way across it, a character is planned out by
    // the nearest shore that leads on to where they are going, and not across the
    // rest of the lake though that is fewer steps (4, against 7 round).
    [Fact]
    public void StandingInTheLake_IsPlannedOutByTheNearestWay_NotAcross()
    {
        WithLake(LakeCrossingJson, (bfs, graph, filter) =>
        {
            RoomKey inTheLake = new(1, 2), bank = new(1, 9);
            IReadOnlyList<Direction>? path = bfs.FindPath(inTheLake, bank, filter);

            Assert.NotNull(path);
            Assert.Equal(
                new[] { new RoomKey(1, 2), new RoomKey(1, 1), new RoomKey(1, 6), new RoomKey(1, 7),
                        new RoomKey(1, 8), new RoomKey(1, 5), new RoomKey(1, 9) },
                RouteChoicePlanner.BuildKeyPath(graph, inTheLake, path!));
            // No card for it: the way out is a plain walk.
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, inTheLake, bank));

            // From the middle, two rooms from either shore, it is still a way out and
            // never a way through to somewhere in the lake.
            Assert.Equal(2, bfs.FindPath(new RoomKey(1, 3), new RoomKey(1, 1), filter)!.Count);
            Assert.Null(bfs.FindPath(new RoomKey(1, 2), new RoomKey(1, 4), filter));

            // The island is out of the spell area and a step away: a way out like any
            // other, though it leads no further for a crosser who can't be taken
            // back across.
            Assert.Single(bfs.FindPath(inTheLake, new RoomKey(1, 10), filter)!);
        });
    }

    // A loop has no boat exception (user, 2026-10-10: nobody loops the lake's teleport
    // rooms). Its legs are planned like any route: shore to shore they go round, and
    // a leg to a waypoint in a sea room doesn't expand, raft or no raft.
    [Fact]
    public void LoopLegs_GoRoundTheLake_AndNeverIntoIt_BoatOrNoBoat()
    {
        WithLake(LakeCrossingJson, (bfs, graph, filter) =>
        {
            LoopWaypoint[] shoreToShore = { new() { Room = "1/1" }, new() { Room = "1/5" } };
            (IReadOnlyList<LoopStep> steps, IReadOnlyList<(RoomKey From, RoomKey To)> unreachable) =
                LoopExpander.Expand(shoreToShore, bfs, filter);
            Assert.Empty(unreachable);
            Assert.Equal(8, steps.Count);   // four by the path each way, not four across
            Assert.Equal(Direction.N, Assert.IsType<MoveLoopStep>(steps[0]).Direction);
            Assert.DoesNotContain(
                LoopExpander.ResolveCycleRoomKeys(shoreToShore, bfs, graph, filter), filter.IsClosedToRoutes);

            LoopWaypoint[] intoTheLake = { new() { Room = "1/1" }, new() { Room = "1/4" } };
            (_, unreachable) = LoopExpander.Expand(intoTheLake, bfs, filter);
            Assert.Contains((new RoomKey(1, 1), new RoomKey(1, 4)), unreachable);
        });
    }

    // The lake's rooms that carry no teleporting spell are ordinary ground whatever
    // they are called: no raft is asked for, no card is shown and they are not
    // avoided (user, 2026-10-10). Which room is which comes from its spell alone.
    [Fact]
    public void LakeRoomsWithoutTheSpell_AreOrdinaryGround_NeedingNoRaft()
    {
        string lane = LakeCrossingJson.Replace("\"Name\": \"Path\"", "\"Name\": \"Crystal Lake\"");
        WithLake(lane, (bfs, graph, filter) =>
        {
            RoomKey shore = new(1, 1), bank = new(1, 9);
            IReadOnlyList<Direction>? path = bfs.FindPath(shore, bank, filter);

            Assert.Equal(
                new[] { shore, new RoomKey(1, 6), new RoomKey(1, 7), new RoomKey(1, 8), new RoomKey(1, 5), bank },
                RouteChoicePlanner.BuildKeyPath(graph, shore, path!));
            Assert.Equal("Crystal Lake", graph.GetRoom(new RoomKey(1, 7))!.Name);
            Assert.False(filter.IsClosedToRoutes(new RoomKey(1, 7)));
            Assert.False(filter.IsUncounteredHazardRoom(new RoomKey(1, 7)));
            Assert.Null(RouteChoicePlanner.Evaluate(bfs, filter, graph, shore, bank));
        });
    }

    // The sea rooms are closed before the inventory has been read as well: nothing
    // carried would open them, so there is nothing to wait for.
    [Fact]
    public void Lake_IsClosed_BeforeTheInventoryIsRead()
    {
        WithLake(HazardShortcutRoomsJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => false;
            Assert.True(filter.IsClosedToRoutes(new RoomKey(1, 5)));
            Assert.Equal(3, bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 9), filter)!.Count);
        });
    }

    // The desert is a hazard that teleports on a roll as well, and it routes exactly
    // as it did: its waterskin is a counter, carried it opens the room, and without
    // it the route card offers it.
    [Fact]
    public void Desert_StillRoutesByItsCounter()
    {
        const string desertSpells = """
            [ { "Number": 700, "Abil-0": 148, "AbilVal-0": 50 }, { "Number": 300, "Dur": 600 } ]
            """;
        const string desertItems = """ [ { "Number": 60, "Abil-0": 43, "AbilVal-0": 300 } ] """;
        const string desertTbInfo = """
            [ { "Number": 50, "Action": "failspell 300 49:random 51" },
              { "Number": 51, "Action": "86:cast 713\n100:random 52" },
              { "Number": 52, "Action": "30:failitem 99:teleport 9 1" } ]
            """;
        foreach (bool waterskin in new[] { false, true })
            WithGraph(HazardShortcutRoomsJson, (bfs, graph, filter) =>
            {
                RoomKey from = new(1, 1), to = new(1, 9);
                Assert.False(filter.IsClosedToRoutes(new RoomKey(1, 5)));
                Assert.Equal(waterskin ? 2 : 3, bfs.FindPath(from, to, filter)!.Count);

                RouteChoice? choice = RouteChoicePlanner.Evaluate(bfs, filter, graph, from, to);
                if (waterskin)
                {
                    Assert.Null(choice);
                    return;
                }
                Assert.Equal(2, choice!.GatedStepCount);
                RouteRequirement req = Assert.Single(choice.Requirements);
                Assert.Equal(new[] { 60, 99 }, req.ItemIds.OrderBy(i => i));
                Assert.False(req.NoProtection);
            },
            spellsJson: desertSpells,
            itemsJson: desertItems,
            wireHazards: (index, filter) =>
            {
                filter.Hazards = index;
                filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 5) ? 700 : 0;
                filter.InventoryReadyProbe = () => true;
                filter.ItemCarriedProbe = id => waterskin && id == 60;
            },
            tbInfoJson: desertTbInfo);
    }
}
