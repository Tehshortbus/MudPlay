using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhSortQueueBuilderTests : IDisposable
{
    private readonly string _root;

    public GhSortQueueBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-ghsortqueue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ItemType 1=Weapon, 0=Armour (ArmourType 7=Chainmail, 8=Scalemail),
    // 9=Scroll (no matching labeled room in the fixtures below, so scrolls
    // exercise the "left in place" / catch-all paths).
    private const string ItemsJson = """
        [
          { "Number": 1, "Name": "war hammer",   "ItemType": 1 },
          { "Number": 2, "Name": "chain shirt",  "ItemType": 0 },
          { "Number": 3, "Name": "old scroll",   "ItemType": 9 },
          { "Number": 4, "Name": "gold emblem",  "ItemType": 0 },
          { "Number": 5, "Name": "chainmail vest", "ItemType": 0, "ArmourType": 7 },
          { "Number": 6, "Name": "scalemail vest", "ItemType": 0, "ArmourType": 8 },
          { "Number": 7, "Name": "platemail vest", "ItemType": 0, "ArmourType": 9 }
        ]
        """;

    private ItemNameStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    private static readonly RoomKey WeaponsRoom = new(1, 1);
    private static readonly RoomKey ArmourRoom = new(1, 2);

    private static GhRoomLabel MakeLabel(RoomKey key, bool isCatchAll, params GhCategoryRule[] rules) =>
        new(key.Map, key.Room) { Rules = new List<GhCategoryRule>(rules), IsCatchAll = isCatchAll };

    private static readonly GhRoomLabel[] Labels =
    {
        MakeLabel(WeaponsRoom, false, GhCategoryRule.ForItemType(1)),   // Weapons room
        MakeLabel(ArmourRoom, false, GhCategoryRule.ForItemType(0)),    // Armour room
    };

    [Fact]
    public void Build_MisplacedItem_QueuesMoveToCorrectRoom()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [ArmourRoom] = new List<string> { "a war hammer" },   // weapon sitting in the armour room
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);

        GhPendingMove move = Assert.Single(moves);
        Assert.Equal(ArmourRoom, move.From);
        Assert.Equal(WeaponsRoom, move.To);
        Assert.Equal("war hammer", move.ItemName);
        Assert.Empty(leftInPlace);
    }

    [Fact]
    public void Build_ItemAlreadyInCorrectRoom_QueuesNothing()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [WeaponsRoom] = new List<string> { "a war hammer" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);

        Assert.Empty(moves);
        Assert.Empty(leftInPlace);
    }

    [Fact]
    public void Build_NoMatchingLabeledRoom_ReportsLeftInPlaceAndQueuesNoMove()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [WeaponsRoom] = new List<string> { "an old scroll" },   // no Scrolls room labeled
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);

        Assert.Empty(moves);
        GhSweepItemFound found = Assert.Single(leftInPlace);
        Assert.Equal(WeaponsRoom, found.Room);
        Assert.Equal("an old scroll", found.ItemName);
    }

    [Fact]
    public void Build_UnlabeledObservedRoom_QueuesItemToLabeledDestination()
    {
        ItemNameStore names = NewStore();
        RoomKey unlabeled = new(1, 99);
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [unlabeled] = new List<string> { "a war hammer" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);

        GhPendingMove move = Assert.Single(moves);
        Assert.Equal(unlabeled, move.From);
        Assert.Equal(WeaponsRoom, move.To);
        Assert.Equal("war hammer", move.ItemName);
        Assert.Empty(leftInPlace);
    }

    [Fact]
    public void SurveyMerge_VisibleAndRepeatedHiddenResults_UsesUnionAndMaximumCount()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, List<string>>();

        GhSurveyMerger.Merge(observed, WeaponsRoom,
            new[] { "a war hammer", "a chain shirt" }, names);
        GhSurveyMerger.Merge(observed, WeaponsRoom,
            new[] { "2 old scrolls", "a chain shirt" }, names);
        GhSurveyMerger.Merge(observed, WeaponsRoom,
            new[] { "3 old scrolls" }, names);

        Assert.Contains("war hammer", observed[WeaponsRoom]);
        Assert.Contains("chain shirt", observed[WeaponsRoom]);
        Assert.Contains("3 old scroll", observed[WeaponsRoom]);
        Assert.Equal(3, observed[WeaponsRoom].Count);
    }

    // Gang-house guard emblems must never be swept as clutter, even when the item's own
    // ItemType happens to match a labeled room (GAME_MECHANICS.md "Gang houses & guard
    // emblems" — moving the required emblem away from the player risks the guards attacking).
    [Fact]
    public void Build_GuardEmblem_NeverQueuedEvenWhenCategoryMatchesALabeledRoom()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            // "gold emblem" classifies as ItemType 0 (Armour), which matches ArmourRoom —
            // ordinary category logic would want to move it there from the weapons room.
            [WeaponsRoom] = new List<string> { "a gold emblem" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);

        Assert.Empty(moves);
        GhSweepItemFound found = Assert.Single(leftInPlace);
        Assert.Equal(WeaponsRoom, found.Room);
        Assert.Equal("a gold emblem", found.ItemName);
    }

    // "Chain Scale" style room: two rules OR'd on one label. Both a Chainmail
    // item and a Scalemail item sitting elsewhere must route to the same room;
    // a Platemail item (matching neither rule) is left in place.
    [Fact]
    public void Build_MultiRuleRoom_AdmitsEitherRuleButNotAThirdCategory()
    {
        ItemNameStore names = NewStore();
        RoomKey junkRoom = new(1, 3);
        GhRoomLabel[] labels =
        {
            MakeLabel(junkRoom, false),   // holding pen, matches nothing itself
            MakeLabel(ArmourRoom, false,
                GhCategoryRule.ForItemType(0, armourType: 7),    // Chainmail
                GhCategoryRule.ForItemType(0, armourType: 8)),   // Scalemail
        };
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [junkRoom] = new List<string> { "a chainmail vest", "a scalemail vest", "a platemail vest" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, labels, names);

        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, m => m.ItemName == "chainmail vest" && m.To.Equals(ArmourRoom));
        Assert.Contains(moves, m => m.ItemName == "scalemail vest" && m.To.Equals(ArmourRoom));
        GhSweepItemFound found = Assert.Single(leftInPlace);
        Assert.Equal("a platemail vest", found.ItemName);
    }

    // Catch-all fallback: an item matching no explicit rule anywhere still
    // gets swept, to the designated catch-all room, instead of being
    // reported as left in place.
    [Fact]
    public void Build_CatchAllRoom_ReceivesItemsMatchingNoExplicitRule()
    {
        ItemNameStore names = NewStore();
        RoomKey miscRoom = new(1, 3);
        GhRoomLabel[] labels =
        {
            MakeLabel(WeaponsRoom, false, GhCategoryRule.ForItemType(1)),
            MakeLabel(miscRoom, true),   // catch-all, no explicit rules
        };
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [WeaponsRoom] = new List<string> { "an old scroll" },   // matches nothing explicit
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, labels, names);

        GhPendingMove move = Assert.Single(moves);
        Assert.Equal(WeaponsRoom, move.From);
        Assert.Equal(miscRoom, move.To);
        Assert.Empty(leftInPlace);
    }

    // An item already sitting in the catch-all room, matching nothing
    // explicit anywhere, is already correctly placed — no move queued.
    [Fact]
    public void Build_CatchAllRoom_ItemAlreadyThere_QueuesNothing()
    {
        ItemNameStore names = NewStore();
        RoomKey miscRoom = new(1, 3);
        GhRoomLabel[] labels =
        {
            MakeLabel(WeaponsRoom, false, GhCategoryRule.ForItemType(1)),
            MakeLabel(miscRoom, true),
        };
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [miscRoom] = new List<string> { "an old scroll" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, labels, names);

        Assert.Empty(moves);
        Assert.Empty(leftInPlace);
    }

    // Catch-all must never win over an explicit rule match elsewhere — an
    // item that matches a specific room's rule goes there, not to catch-all.
    [Fact]
    public void Build_CatchAllRoom_DoesNotOverrideAnExplicitRuleMatch()
    {
        ItemNameStore names = NewStore();
        RoomKey miscRoom = new(1, 3);
        GhRoomLabel[] labels =
        {
            MakeLabel(WeaponsRoom, false, GhCategoryRule.ForItemType(1)),
            MakeLabel(ArmourRoom, false, GhCategoryRule.ForItemType(0)),
            MakeLabel(miscRoom, true),
        };
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [miscRoom] = new List<string> { "a war hammer" },   // explicitly a Weapons-room item
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, labels, names);

        GhPendingMove move = Assert.Single(moves);
        Assert.Equal(miscRoom, move.From);
        Assert.Equal(WeaponsRoom, move.To);   // routed to the explicit match, not left in the catch-all
        Assert.Empty(leftInPlace);
    }

    // No catch-all designated at all: original "leave unmatched in place" behavior holds.
    [Fact]
    public void Build_NoCatchAllDesignated_UnmatchedItemLeftInPlace()
    {
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [WeaponsRoom] = new List<string> { "an old scroll" },
        };

        (var moves, var leftInPlace) = GhSortQueueBuilder.Build(observed, Labels, names);   // Labels has no catch-all

        Assert.Empty(moves);
        Assert.Single(leftInPlace);
    }

    [Fact]
    public void Build_SortsFlaggedItemsToo_NoDiscardSkip()
    {
        // The builder no longer excludes auto-discard-flagged items: the discard
        // engine is held off for the whole sweep (GhSweepManager.IsActive), so
        // Roomba sorts a flagged item to its labeled room like anything else
        // rather than leaving it for the engine to bin. Both the weapon and the
        // armour, misplaced in room 1/9, are queued to their rooms; nothing is
        // left behind.
        ItemNameStore names = NewStore();
        var observed = new Dictionary<RoomKey, IReadOnlyList<string>>
        {
            [new RoomKey(1, 9)] = new[] { "war hammer", "chain shirt" },
        };

        (IReadOnlyList<GhPendingMove> moves, IReadOnlyList<GhSweepItemFound> left) =
            GhSortQueueBuilder.Build(observed, Labels, names);

        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, m => m.ItemName == "war hammer" && m.To.Equals(WeaponsRoom));
        Assert.Contains(moves, m => m.ItemName == "chain shirt" && m.To.Equals(ArmourRoom));
        Assert.Empty(left);
    }
}
