using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The sources a path item can be asked for, over a real graph, filter and item
// index: the report's own walk (paradigm-20261008-175938) from the Officer's
// Quarters to the Spiral Stairway inside the dark tower, whose door needs the
// glowing key the sleazy shopkeeper trades for an opal brooch.
//
//   8/530 Officer's Quarters ─W─ 8/524 Guild Hallway ─N─ 8/486 Musty Store
//                                      │W
//   8/557 Spiral Stairway ─D─ 8/532 ─E (door)─ 8/531 Dark Tower, Entrance
//                                      └─ 8/531 W is (Key: 808 [or 1000 picklocks])
public sealed class PathItemGiveSourcesTests
{
    private static readonly RoomKey Quarters = new(8, 530);
    private static readonly RoomKey Store = new(8, 486);
    private static readonly RoomKey Stairway = new(8, 557);

    private const int GlowingKey = 808;
    private const int OpalBrooch = 811;
    private const int SpareKey = 60;
    private const int RedGem = 62;

    private static string Room(int number, string name, string exits) =>
        $$"""{ "Map Number": 8, "Room Number": {{number}}, "Name": "{{name}}", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, {{exits}} }""";

    private static string Exits(
        string n = "0", string s = "0", string e = "0", string w = "0", string u = "0", string d = "0") =>
        string.Join(", ", new (string Dir, string To)[]
        {
            ("N", n), ("S", s), ("E", e), ("W", w), ("NE", "0"), ("NW", "0"), ("SE", "0"), ("SW", "0"), ("U", u), ("D", d),
        }.Select(x => $"\"{x.Dir}\": \"{x.To}\""));

    private static readonly string RoomsJson = "[" + string.Join(",\n",
        Room(530, "Officer's Quarters", Exits(w: "8/524")),
        Room(524, "Guild Hallway", Exits(n: "8/486", e: "8/530", w: "8/531")),
        Room(486, "Musty Store", Exits(s: "8/524")),
        Room(531, "Dark Tower, Entrance", Exits(e: "8/524", w: "8/532 (Key: 808 [or 1000 picklocks])")),
        Room(532, "Tower Antechamber", Exits(e: "8/531 (Door [41 picklocks/strength])", u: "8/557")),
        Room(557, "Spiral Stairway", Exits(d: "8/532"))) + "]";

    private const string ItemsJson = """
        [
          { "Number": 808, "Name": "glowing key", "ItemType": 7 },
          { "Number": 811, "Name": "opal brooch", "ItemType": 0 },
          { "Number": 60, "Name": "spare key", "ItemType": 7 },
          { "Number": 62, "Name": "red gem", "ItemType": 0 },
          { "Number": 63, "Name": "lucky charm", "ItemType": 0 }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 346, "Name": "captain of the guard", "Summoned By": "Room 8/530",
            "DropItem-0": 811, "DropItem%-0": 100 },
          { "Number": 348, "Name": "sleazy shopkeeper", "Summoned By": "Room 8/486" },
          { "Number": 349, "Name": "fence", "Summoned By": "Room 8/524" }
        ]
        """;

    // The shopkeeper's menu as the data has it, plus a key he both hands over for
    // nothing (`spare`) and trades for the brooch (`deal`), and a charm that isn't a
    // key (`charm`). A fence nearer the walk trades the same glowing key for a gem.
    private const string TBInfoJson = """
        [
          { "Number": 838, "LinkTo": 839, "Action": "help:840\nbrooch:841\nspare:850\ndeal:852\ncharm:854\n", "Called From": "Monster #348" },
          { "Number": 854, "LinkTo": 0, "Action": "takeitem 811:giveitem 63\n", "Called From": "Textblock #838" },
          { "Number": 870, "LinkTo": 0, "Action": "gem:871\n", "Called From": "Monster #349" },
          { "Number": 871, "LinkTo": 0, "Action": "takeitem 62:giveitem 808\n", "Called From": "Textblock #870" },
          { "Number": 841, "LinkTo": 842, "Action": "", "Called From": "Textblock #838" },
          { "Number": 842, "LinkTo": 0, "Action": "takeitem 811 1368:giveitem 808:text 843\n", "Called From": "Textblock #841" },
          { "Number": 850, "LinkTo": 0, "Action": "giveitem 60\n", "Called From": "Textblock #838" },
          { "Number": 852, "LinkTo": 0, "Action": "takeitem 811:giveitem 60\n", "Called From": "Textblock #838" }
        ]
        """;

    private sealed class World
    {
        public required PathItemGiveSources Sources { get; init; }
        public required BfsMapper Bfs { get; init; }
        public required MovementFilter Filter { get; init; }
        public readonly HashSet<int> Pack = new();
        public readonly HashSet<int> SoldOrSummoned = new();
    }

    private static void WithWorld(Action<World> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-givesources-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), RoomsJson);
            File.WriteAllText(Path.Combine(setDir, "Items.json"), ItemsJson);
            File.WriteAllText(Path.Combine(setDir, "Monsters.json"), MonstersJson);
            File.WriteAllText(Path.Combine(setDir, "TBInfo.json"), TBInfoJson);

            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            TBInfoStore tb = new(cache);
            tb.OnActiveSetChanged("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            MovementFilter filter = new(profile);
            // The report's character: the key isn't held and the door is far past
            // picking, so the tower is shut to the plan.
            filter.InventoryReadyProbe = () => true;
            filter.ItemCarriedProbe = _ => false;
            filter.StrengthProvider = () => 90;
            filter.PicklocksProvider = () => 119;
            filter.MaxBashableStrengthProvider = () => 200;

            World? world = null;
            world = new World
            {
                Bfs = bfs,
                Filter = filter,
                Sources = new PathItemGiveSources(
                    new ItemSourceIndex(cache, tb), bfs, filter,
                    unwornCount: id => world!.Pack.Contains(id) ? 1 : 0,
                    itemName: id => id switch
                    {
                        GlowingKey => "glowing key", OpalBrooch => "opal brooch", RedGem => "red gem", _ => null,
                    },
                    isKey: id => id is GlowingKey or SpareKey,
                    soldOrSummoned: id => world!.SoldOrSummoned.Contains(id),
                    alwaysDroppedBy: id => id == OpalBrooch ? "captain of the guard" : null),
            };
            body(world);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // The case the card got wrong: the destination is behind the door the key
    // opens, so on the live filter the shopkeeper-to-destination leg has no length
    // and no giver could be chosen. The card then said nothing of a trade the walk
    // went on to make.
    [Fact]
    public void Choose_DestinationBehindTheKeyDoor_StillNamesTheTrader()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);
            Assert.Null(w.Bfs.DistanceBetween(Store, Stairway, w.Filter));
            Assert.NotNull(w.Sources.DetourDistance(Store, Stairway));

            GiveSource? chosen = w.Sources.Choose(GlowingKey, Quarters, Stairway, offerTrades: true);

            Assert.True(chosen.HasValue);
            GiveSource source = chosen.Value;
            Assert.Equal(Store, source.Room);
            Assert.Equal("ask sleazy shopkeeper brooch", source.Command);
            Assert.Equal("sleazy shopkeeper, in trade for your opal brooch", source.GiverName);
            Assert.Equal(OpalBrooch, source.TakesItemId);
            // His refusal (message 1368) ends the wait at once instead of after its window.
            Assert.Equal(new[] { "When the brooch is not forthcoming, he spits in disgust and turns away!" },
                source.RefusalLines);
        });
    }

    // The same walk through the route card's own source plan: the pick that
    // fetches the key names the trade and lists it to agree to, and a pick that
    // only walks somewhere and stops promises nothing.
    [Fact]
    public void GatePick_KeyDoorWithTheBroochInThePack_NamesTheTradeItWillMake()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);
            var requirements = new[] { new RouteRequirement(RouteRequirementKind.DoorKey, new[] { GlowingKey }) };
            GatePickSources Plan(bool pickFetches) => GatePickSources.Build(
                requirements, pickFetches,
                keyHasOtherSource: _ => false,
                flaggedAutoObtain: _ => false,
                giver: (id, offerTrades) => w.Sources.Choose(id, Quarters, Stairway, offerTrades),
                buyPhrase: _ => null,
                dropper: _ => null,
                tradeNote: w.Sources.TradeNote);

            GatePickSources fetching = Plan(pickFetches: true);
            Assert.Equal("sleazy shopkeeper, in trade for your opal brooch", fetching.GiverName(GlowingKey));
            Assert.Equal(new[] { (GlowingKey, OpalBrooch) }, fetching.Trades);
            Assert.Null(fetching.BuyOrTradeNote(GlowingKey));

            GatePickSources stopping = Plan(pickFetches: false);
            Assert.Null(stopping.GiverName(GlowingKey));
            Assert.Empty(stopping.Trades);
            Assert.Equal("sleazy shopkeeper trades one for your opal brooch", stopping.BuyOrTradeNote(GlowingKey));
        });
    }

    // A trade is made only when it was agreed to.
    [Fact]
    public void ForRouter_TradeNotAgreed_OffersNothing()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);

            Assert.Empty(w.Sources.ForRouter(GlowingKey, agreedTakes: null));
            Assert.Null(w.Sources.Choose(GlowingKey, Quarters, Stairway, offerTrades: false));
            Assert.Single(w.Sources.ForRouter(GlowingKey, agreedTakes: OpalBrooch));
        });
    }

    // Without the brooch in the pack (worn counts as without) there is no trade
    // to make, and the note says where a brooch comes from.
    [Fact]
    public void Trades_ItemNotInThePack_NoneAndTheNoteSaysWhereItComesFrom()
    {
        WithWorld(w =>
        {
            Assert.Empty(w.Sources.Trades(GlowingKey));
            Assert.Empty(w.Sources.ForRouter(GlowingKey, agreedTakes: OpalBrooch));
            Assert.Equal(
                "sleazy shopkeeper trades one for opal brooch, which captain of the guard drops",
                w.Sources.TradeNote(GlowingKey));
        });
    }

    // A trade spends an item, so it never stands in for a purchase or a summon.
    [Fact]
    public void Trades_ItemAlsoSoldOrSummoned_TheTradeIsNotUsedOrMentioned()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);
            w.SoldOrSummoned.Add(GlowingKey);

            Assert.Empty(w.Sources.Trades(GlowingKey));
            Assert.Empty(w.Sources.ForRouter(GlowingKey, agreedTakes: OpalBrooch));
            Assert.Null(w.Sources.TradeNote(GlowingKey));
        });
    }

    // Nor for a free hand-over of the same item.
    [Fact]
    public void ForRouter_ItemAlsoGivenFree_AsksForTheFreeOne()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);

            GiveSource free = Assert.Single(w.Sources.ForRouter(SpareKey, agreedTakes: OpalBrooch));
            Assert.Equal("ask sleazy shopkeeper spare", free.Command);
            Assert.Equal(0, free.TakesItemId);
            Assert.Empty(w.Sources.Trades(SpareKey));
            Assert.Null(w.Sources.TradeNote(SpareKey));
        });
    }

    // Two traders sell the same key for different items and both items are in the
    // pack. The card names one trade; the walk may make that one and no other,
    // though the other trader is nearer.
    [Fact]
    public void ForRouter_TwoTradesForOneKey_OnlyTheAgreedItemIsHandedOver()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);
            w.Pack.Add(RedGem);
            Assert.Equal(2, w.Sources.Trades(GlowingKey).Count);

            GiveSource brooch = Assert.Single(w.Sources.ForRouter(GlowingKey, agreedTakes: OpalBrooch));
            Assert.Equal("ask sleazy shopkeeper brooch", brooch.Command);
            Assert.Equal(OpalBrooch, brooch.TakesItemId);

            GiveSource gem = Assert.Single(w.Sources.ForRouter(GlowingKey, agreedTakes: RedGem));
            Assert.Equal("ask fence gem", gem.Command);

            // An item nobody trades the key for buys nothing.
            Assert.Empty(w.Sources.ForRouter(GlowingKey, agreedTakes: SpareKey));
        });
    }

    // Only a key is ever traded for, whoever would make the swap.
    [Fact]
    public void Trades_ItemThatIsNotAKey_None()
    {
        WithWorld(w =>
        {
            w.Pack.Add(OpalBrooch);
            const int charm = 63;

            Assert.Empty(w.Sources.Trades(charm));
            Assert.Empty(w.Sources.ForRouter(charm, agreedTakes: OpalBrooch));
            Assert.Null(w.Sources.Choose(charm, Quarters, Stairway, offerTrades: true));
            Assert.Null(w.Sources.TradeNote(charm));
        });
    }
}
