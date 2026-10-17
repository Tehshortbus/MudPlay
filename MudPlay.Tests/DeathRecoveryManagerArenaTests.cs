using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// An arena death takes no item, coin or key (GAME_MECHANICS "Death threshold &
// consequences"): it goes in the death history with nothing lost, and nothing sets
// out to recover a pile that isn't there. Stock tells one by the engine's colliseum
// line, Paradigm by the room. Every other death keeps its pile.
public sealed partial class DeathRecoveryManagerTests
{
    // Two of the listed arena rooms (ArenaDeathRooms: map 11, rooms 1-34).
    private const string ArenaGraphJson = """
        [
          { "Map Number": 11, "Room Number": 1, "Name": "Arena Floor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "11/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 2, "Name": "Arena Gate",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "11/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string ColliseumLine = "But, because you were in a colliseum, you have been saved.";
    private const string MiracleLine = "But, due to a miracle, you have been saved.";

    private static RoomObservation ArenaFloor() => new("Arena Floor", new HashSet<Direction> { Direction.N });

    // Worn, carried and coin in hand: what an ordinary death would put on the pile.
    private static InventorySnapshot GearAndCoin() => new(
        new CurrencyHoldings(0, 0, 100, 1, 0, 20_000), EncumbranceReading.Empty,
        new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" }, DateTimeOffset.UtcNow);

    // The death's own lines, in the order the game prints them, read by the real
    // detector over the harness's tracker.
    private static DeathDetector DieBy(GraphHarness h, bool paradigm, string savedLine, int lives)
    {
        h.Paradigm = paradigm;
        h.Snapshot = GearAndCoin();
        DeathDetector deaths = new(h.Tracker) { IsParadigm = () => paradigm };
        deaths.AttachLineExtractor(h.Feed.Lines);
        h.Feed.Line("You have been killed!");
        h.Feed.Line(savedLine);
        h.Feed.Line($"You have {lives} lives left.");
        h.Sent.Clear();
        return deaths;
    }

    private static void AssertNothingLost(GraphHarness h, RoomKey diedIn, int lives)
    {
        DeathRecord record = Assert.Single(h.Recovery.Records);
        Assert.True(record.NothingLost);
        Assert.Equal(DeathRecoveryStatus.Recovered, record.Status);
        Assert.Equal(RoomTracker.ArenaDeathNote, record.RecoveryMessage);
        Assert.Equal(diedIn, new RoomKey(record.Room!.Map, record.Room.Room));
        Assert.Equal(lives, record.LivesRemaining);
        Assert.Null(record.EquippedAtDeath);
        Assert.Null(record.LostItems);
        Assert.Null(record.CoinsAtDeath);
        Assert.DoesNotContain("None recorded", record.EquippedAtDeathText);
        Assert.DoesNotContain("None recorded", record.LostItemsText);
        Assert.True(h.Tracker.LastDeathTookNothing);
        // A death all the same for where the character is: off to the graveyard.
        Assert.Equal(RoomConfidence.PendingRespawn, h.Tracker.State.Confidence);
    }

    [Fact]
    public void Stock_TheColliseumLine_RecordsTheDeath_WithNoPile_AndArmsNothing()
    {
        using GraphHarness h = new(graphJson: ArenaGraphJson);
        h.Tracker.NoteRoomObserved(ArenaFloor());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        DieBy(h, paradigm: false, ColliseumLine, lives: 6);

        AssertNothingLost(h, new RoomKey(11, 1), lives: 6);

        // Back in the room with the same things on its floor: they are someone
        // else's, and nothing of ours is waited for.
        h.FeedSurvey("a rusty dagger, a torch");
        h.Tracker.NoteRoomObserved(ArenaFloor());
        h.Feed.Line("Obvious exits: north");
        for (int i = 0; i < 3; i++) h.Heartbeat();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    // Arenas switched off on the board: the engine prints its ordinary lines in an
    // arena room, and that death drops a pile like any other.
    [Fact]
    public void Stock_AnArenaRoom_WithoutTheColliseumLine_IsAnOrdinaryDeath_WithItsPile()
    {
        using GraphHarness h = new(graphJson: ArenaGraphJson);
        h.Tracker.NoteRoomObserved(ArenaFloor());
        h.Recovery.AutoRecover = true;

        DieBy(h, paradigm: false, MiracleLine, lives: 5);

        DeathRecord record = Assert.Single(h.Recovery.Records);
        Assert.False(record.NothingLost);
        Assert.Equal(DeathRecoveryStatus.Active, record.Status);
        Assert.Null(record.RecoveryMessage);
        Assert.Equal("rusty dagger", Assert.Single(record.EquippedAtDeath!).Name);
        Assert.Contains(record.LostItems!, i => i.Name == "torch");
        Assert.Equal(20_000, record.CoinsAtDeath!.Value.TotalCopperValue);
        Assert.False(h.Tracker.LastDeathTookNothing);

        h.FeedSurvey("a torch");
        h.Tracker.NoteRoomObserved(ArenaFloor());
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Paradigm_AnArenaRoom_RecordsTheDeath_WithNoPile_AndArmsNothing()
    {
        using GraphHarness h = new(graphJson: ArenaGraphJson);
        h.Tracker.NoteRoomObserved(ArenaFloor());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;

        DieBy(h, paradigm: true, MiracleLine, lives: 6);

        AssertNothingLost(h, new RoomKey(11, 1), lives: 6);

        // Even a corpse of that name on the floor is not asked for: no pile is open.
        h.FeedSurvey("corpse of Ermias");
        h.Tracker.NoteRoomObserved(ArenaFloor());
        for (int i = 0; i < 3; i++) h.Heartbeat();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Paradigm_AnOrdinaryRoom_IsUnchanged_ThePileIsRecorded_AndRecovered()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Recovery.AutoRecover = true;

        DieBy(h, paradigm: true, MiracleLine, lives: 5);

        DeathRecord record = Assert.Single(h.Recovery.Records);
        Assert.False(record.NothingLost);
        Assert.Equal(DeathRecoveryStatus.Active, record.Status);
        Assert.Equal("rusty dagger", Assert.Single(record.EquippedAtDeath!).Name);
        Assert.Contains(record.LostItems!, i => i.Name == "torch");
        Assert.False(h.Tracker.LastDeathTookNothing);

        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    // The flag is the death's, not the detector's: the next death, an ordinary one,
    // is not read as an arena death because the last one was.
    [Fact]
    public void AnOrdinaryDeath_AfterAnArenaDeath_HasItsPile()
    {
        using GraphHarness h = new(graphJson: ArenaGraphJson);
        h.Tracker.NoteRoomObserved(ArenaFloor());
        DeathDetector deaths = DieBy(h, paradigm: false, ColliseumLine, lives: 6);

        h.Tracker.NoteRoomObserved(ArenaFloor());
        h.Feed.Line("You have been killed!");
        h.Feed.Line(MiracleLine);
        h.Feed.Line("You have 5 lives left.");

        Assert.Equal(2, h.Recovery.Records.Count);
        Assert.True(h.Recovery.Records[0].NothingLost);
        Assert.False(h.Latest.NothingLost);
        Assert.Equal(DeathRecoveryStatus.Active, h.Latest.Status);
        Assert.False(h.Tracker.LastDeathTookNothing);
    }

    // Gear held to go back on once Auto-All is on again is still in the pack after
    // an arena death, so it is still put on. An ordinary death drops it with the
    // rest (AutoEquip_HeldForAutoAll_IsDroppedByANewDeath).
    [Fact]
    public void AutoEquip_HeldForAutoAll_IsKeptThroughAnArenaDeath()
    {
        using GraphHarness h = new();
        bool autoOn = false;
        h.Recovery.SetAutoEnabledProbe(() => autoOn);
        Die(h, new[] { new EquippedItem("plate mail", "Torso") }, Array.Empty<string>());
        h.Recovery.AutoEquip = true;
        h.EnterGates();
        h.Feed.Line("You have recovered the corpse of Ermias.");
        Assert.Equal(1, h.Recovery.HeldReequipCount);

        h.Tracker.NoteDeath(2, "You have 2 lives left.", tookNothing: true);

        Assert.Equal(1, h.Recovery.HeldReequipCount);
    }

    // The record survives a save and a load as an arena death.
    [Fact]
    public void TheArenaDeathRecord_KeepsItsFlag_ThroughJson()
    {
        DeathRecord record = new(DateTimeOffset.UtcNow, new RoomRef(11, 1), 6, "You have 6 lives left.")
        {
            Status = DeathRecoveryStatus.Recovered,
            NothingLost = true,
        };

        string json = System.Text.Json.JsonSerializer.Serialize(record);
        DeathRecord back = System.Text.Json.JsonSerializer.Deserialize<DeathRecord>(json)!;

        Assert.True(back.NothingLost);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<DeathRecord>("{}")!.NothingLost);
    }
}
