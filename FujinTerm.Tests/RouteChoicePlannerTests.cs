using System;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

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

    private static void WithGraph(
        string roomsJson,
        Action<BfsMapper, RoomGraphManager, MovementFilter> body,
        string? spellsJson = null,
        string? itemsJson = null,
        Action<RoomHazardIndex, MovementFilter>? wireHazards = null)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "fujinterm-routechoice-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            if (spellsJson is not null) File.WriteAllText(Path.Combine(setDir, "Spells.json"), spellsJson);
            if (itemsJson is not null) File.WriteAllText(Path.Combine(setDir, "Items.json"), itemsJson);

            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = new();
            profile.LoadBlank();
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

    [Fact]
    public void NoChoice_WhenNoFreeRoute()
    {
        WithGraph(ItemOnlyJson, (bfs, graph, filter) =>
        {
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;

            RouteChoice? choice = RouteChoicePlanner.Evaluate(
                bfs, filter, graph, new RoomKey(1, 1), new RoomKey(1, 9));

            // The gate is the only way through — leave it to the plain walk,
            // which surfaces the gated-only failure.
            Assert.Null(choice);
        });
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
}
