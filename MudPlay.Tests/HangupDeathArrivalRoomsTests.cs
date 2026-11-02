using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Where the dead of a room arrive after a death by a hang-up, and what the first
// room confirmed on the way back in says of one. The rooms are the Stock map
// file's: Darkwood Forest (1/109) names no room for its dead, so they arrive in
// the Temple, Halls of the Dead (1/2189); map 16 names its own (16/542).
public sealed class HangupDeathArrivalRoomsTests
{
    private const HangupArrivalListTrust Vanilla = HangupArrivalListTrust.VanillaStock;
    private const HangupArrivalListTrust Paradigm = HangupArrivalListTrust.Paradigm;
    private const HangupArrivalListTrust Edited = HangupArrivalListTrust.Unverified;

    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey Temple = new(1, 2189);
    private static readonly RoomKey EarthenTomb = new(1, 142);

    private static bool? Says(RoomKey? hungUpIn, RoomKey? enteredIn, HangupArrivalListTrust trust) =>
        HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, trust).Says;

    [Fact]
    public void ARoomThatNamesNone_SendsItsDeadToTheTemple_OrTheEarthenTomb()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(Darkwood));
        // Any map: the two default rooms are on map 1.
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(2, 100)));
    }

    // 17/3250 names 17/3247 for its dead, and also for a character leaving the
    // game in it. A hang-up puts the character in that room first, and 17/3247
    // names none, so the dead of a hang-up there arrive in the Temple.
    [Fact]
    public void AHangUpGoesThroughTheRoomsLeaveTheGameRoomFirst()
    {
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.For(new RoomKey(17, 3250)));
        Assert.True(Says(new RoomKey(17, 3250), Temple, Vanilla));
        Assert.False(Says(new RoomKey(17, 3250), new RoomKey(17, 3247), Vanilla));
    }

    [Theory]
    [InlineData(16, 311, 16, 542)]     // map 16's own graveyard
    [InlineData(16, 2892, 16, 542)]
    [InlineData(17, 2415, 17, 2415)]   // the open grave is its own
    [InlineData(1, 2140, 1, 2190)]
    [InlineData(17, 3303, 17, 3247)]
    public void ARoomThatNamesOne_SendsItsDeadThere(int map, int room, int toMap, int toRoom) =>
        Assert.Equal(new[] { new RoomKey(toMap, toRoom) }, HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    [Theory]
    [InlineData(18, 1)]       // a map Stock doesn't have
    [InlineData(1, 3464)]     // past the last room of Stock's map 1
    [InlineData(0, 5)]
    [InlineData(1, 0)]
    public void ARoomOutsideStocksWorld_IsNotCovered(int map, int room) =>
        Assert.Null(HangupDeathArrivalRooms.For(new RoomKey(map, room)));

    // The owner's example: hung up in Darkwood Forest, back in the Halls of the
    // Dead. A "yes" is said on any game data.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void BackWhereItsDeadArrive_SaysADeath_OnAnyGameData(HangupArrivalListTrust trust)
    {
        Assert.True(Says(Darkwood, Temple, trust));
        Assert.True(Says(Darkwood, EarthenTomb, trust));
        Assert.True(Says(new RoomKey(16, 700), new RoomKey(16, 542), trust));
        Assert.Null(HangupDeathArrivalRooms.Read(Darkwood, Temple, trust).TakenFor);
    }

    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void BackInTheRoomItLeft_SaysNoDeath_OnAnyGameData(HangupArrivalListTrust trust) =>
        Assert.False(Says(Darkwood, Darkwood, trust));

    // "That is the wrong room" rests on the list being the world's own. It is on an
    // unmodified Stock set, and on Paradigm for the rooms it shares with Stock
    // (the owner, 2026-10-10: "for the same zones that are shared between stock
    // and paradigm yes"). A Stock board whose sysop may have edited its rooms can
    // have moved its graveyards: there the list never says "no" to another room.
    [Fact]
    public void BackAnywhereElse_SaysNoDeath_OnStockAndParadigm_NotOnAnEditedBoard()
    {
        RoomKey elsewhere = new(1, 110);
        Assert.False(Says(Darkwood, elsewhere, Vanilla));
        Assert.False(Says(Darkwood, elsewhere, Paradigm));
        Assert.Null(Says(Darkwood, elsewhere, Edited));
        // Another map's graveyard is not this room's.
        Assert.False(Says(Darkwood, new RoomKey(16, 542), Vanilla));
        Assert.False(Says(Darkwood, new RoomKey(16, 542), Paradigm));
        Assert.Null(Says(Darkwood, new RoomKey(16, 542), Edited));
    }

    // ----- Paradigm's own rooms ----------------------------------------------
    //
    // The Stock list speaks for the zones Paradigm shares with Stock, which is not
    // the same as the room numbers Stock has: Paradigm re-used some for other
    // places. For its own rooms one thing is known: the dead of Albion arrive in
    // 14/1077, the Burial Vault, and Albion is "the rooms that can be reached from
    // 14/1077 without taking the boat" (the owner, 2026-10-10). The walk itself is
    // WalkReach's; here it is given.

    private static readonly RoomKey Vault = new(14, 1077);
    private static readonly RoomKey BankOfAlbion = new(14, 1);        // a Stock number, Albion on Paradigm
    private static readonly RoomKey Kingsport = new(14, 5000);        // past Stock's last room of map 14
    private static readonly RoomKey PortBlackwater = new(14, 9);      // a Stock number, not Albion
    private static readonly RoomKey Shadowmere = new(5, 3);           // Stock's Tournament Arena
    private static readonly RoomKey TerraIsta = new(14, 1813);

    private static bool InAlbion(RoomKey room) => room == Vault || room == BankOfAlbion || room == Kingsport;

    private static HangupEntryRoomReading OnParadigm(RoomKey hungUpIn, RoomKey enteredIn) =>
        HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, Paradigm, couldBeTakenFor: null, InAlbion);

    // 14/1 is `Waterfall Path` on Stock and `Bank of Albion` on Paradigm, where
    // every Stock room of map 14 is another place. Read by its number, the Stock
    // list sent its dead to the Temple and called the Burial Vault the wrong room.
    [Fact]
    public void AStockNumberThatIsAlbionOnParadigm_BackInTheBurialVault_SaysADeath()
    {
        Assert.Equal(new RoomKey(14, 1077), HangupDeathArrivalRooms.ParadigmAlbionVault);
        Assert.False(HangupDeathArrivalRooms.ListCovers(BankOfAlbion, Paradigm));

        HangupEntryRoomReading reading = OnParadigm(BankOfAlbion, Vault);

        Assert.True(reading.Says);
        Assert.True(reading.Albion);
        // On Stock data the same number is Stock's own room, and goes by the list.
        Assert.True(HangupDeathArrivalRooms.ListCovers(BankOfAlbion, Vanilla));
        Assert.False(Says(BankOfAlbion, Vault, Vanilla));
        Assert.True(Says(BankOfAlbion, Temple, Vanilla));
    }

    // An Albion room says yes in the Burial Vault and nothing anywhere else: a
    // hang-up goes through the room's leave-the-game room first, and Paradigm's
    // aren't in the data, so "wrong room" and "never moved" can't be said.
    [Theory]
    [InlineData(14, 1)]
    [InlineData(14, 5000)]
    public void AnAlbionRoom_SaysYesInTheBurialVault_AndNothingElsewhere(int map, int room)
    {
        RoomKey left = new(map, room);

        Assert.True(OnParadigm(left, Vault).Says);
        Assert.Null(OnParadigm(left, left).Says);
        Assert.Null(OnParadigm(left, new RoomKey(14, 5001)).Says);
        Assert.Null(OnParadigm(left, Temple).Says);
        // Hung up in the vault itself: nothing.
        Assert.Null(OnParadigm(Vault, Vault).Says);
        // Albion is Paradigm's. On Stock data, or with no walk to go by, nothing.
        Assert.Null(HangupDeathArrivalRooms.Read(Kingsport, Vault, Vanilla, null, InAlbion).Says);
        Assert.Null(HangupDeathArrivalRooms.Read(Kingsport, Vault, Edited, null, InAlbion).Says);
        Assert.Null(Says(Kingsport, Vault, Paradigm));
    }

    // Paradigm's own rooms outside Albion have no known arrival room: Port
    // Blackwater on Stock's map-14 numbers, Shadowmere on Stock's map 5, Terra
    // Ista across the water, map 18. The Burial Vault says nothing for them, and
    // neither does the Temple, the room they left, or any other.
    [Theory]
    [InlineData(14, 9)]
    [InlineData(5, 3)]
    [InlineData(14, 1813)]
    [InlineData(18, 40)]
    public void AParadigmRoomOutsideAlbion_SaysNothing_WhereverItComesBack(int map, int room)
    {
        RoomKey left = new(map, room);
        Assert.False(HangupDeathArrivalRooms.ListCovers(left, Paradigm));

        Assert.Null(OnParadigm(left, Vault).Says);
        Assert.Null(OnParadigm(left, Temple).Says);
        Assert.Null(OnParadigm(left, left).Says);
        Assert.Null(OnParadigm(left, new RoomKey(1, 110)).Says);
    }

    // Which Stock numbers are another place on Paradigm: maps 5 and 14 whole, and
    // four rooms with another name and no neighbour in common.
    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 25)]
    [InlineData(14, 1)]
    [InlineData(14, 28)]
    [InlineData(4, 2)]
    [InlineData(6, 1338)]
    [InlineData(12, 2383)]
    [InlineData(16, 2272)]
    public void AStockNumberThatIsAnotherPlaceOnParadigm_IsNotCoveredThere(int map, int room)
    {
        RoomKey key = new(map, room);

        Assert.False(HangupDeathArrivalRooms.ListCovers(key, Paradigm));
        Assert.True(HangupDeathArrivalRooms.ListCovers(key, Vanilla));
        Assert.True(HangupDeathArrivalRooms.ListCovers(key, Edited));
    }

    // A room Paradigm only renamed is the same room, in a zone it shares with
    // Stock: 15/1530 is `Old Mine Tunnel` on Stock and `Webbed Mine Tunnel` on
    // Paradigm with the same exits, 15/851 a ganghouse room Stock ships unnamed,
    // 1/2182 the `Park` that sits among map 1's placeholders. The Stock list
    // holds for them, "wrong room" included.
    [Theory]
    [InlineData(15, 1530)]
    [InlineData(15, 851)]
    [InlineData(1, 2182)]
    [InlineData(1, 109)]
    public void ARoomParadigmOnlyRenamed_IsStillSharedWithStock(int map, int room)
    {
        RoomKey left = new(map, room);
        Assert.True(HangupDeathArrivalRooms.ListCovers(left, Paradigm));

        Assert.True(OnParadigm(left, Temple).Says);
        Assert.False(OnParadigm(left, Temple).Albion);
        Assert.False(OnParadigm(left, left).Says);
        Assert.False(OnParadigm(left, Vault).Says);
    }

    // Sixteen rooms of map 1 are `Blank` placeholders on Stock, with no graveyard
    // named, and a dungeon on Paradigm, joined to rooms whose dead arrive in
    // 1/2190, Newhaven, Healer. Theirs arrive there too on Paradigm (the owner,
    // 2026-10-11), and that is trusted like the list is for a shared zone: the
    // Temple, where the Stock file's empty field would send them, is the wrong
    // room. On Stock data the file's own entry stands.
    [Theory]
    [InlineData(2171)]
    [InlineData(2174)]
    [InlineData(2176)]
    [InlineData(2181)]
    [InlineData(2183)]
    [InlineData(2188)]
    public void TheDungeonRoomsOfMap1_ArriveAtTheNewhavenHealer_OnParadigm(int room)
    {
        RoomKey left = new(1, room);
        RoomKey healer = new(1, 2190);
        Assert.Equal(new[] { healer }, HangupDeathArrivalRooms.ListedOn(left, Paradigm));

        Assert.True(OnParadigm(left, healer).Says);
        Assert.False(OnParadigm(left, healer).Learned);
        Assert.False(OnParadigm(left, Temple).Says);
        Assert.False(OnParadigm(left, EarthenTomb).Says);
        Assert.False(OnParadigm(left, left).Says);
        Assert.False(OnParadigm(left, Vault).Says);

        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.ListedOn(left, Vanilla));
        Assert.True(Says(left, Temple, Vanilla));
        Assert.False(Says(left, healer, Vanilla));
    }

    // The placeholders between them that Paradigm didn't make rooms of are not
    // among the sixteen.
    [Theory]
    [InlineData(2175)]
    [InlineData(2182)]
    public void TheNumbersBetweenThem_StillGoByTheStockFile(int room) =>
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.ListedOn(new RoomKey(1, room), Paradigm));

    // Where a death that is seen ends, by the list: the room's own graveyard. For
    // the 101 rooms with a leave-the-game room that changes it, that is not where
    // a hang-up's dead arrive.
    [Fact]
    public void ADeathSeen_EndsInTheRoomsOwnGraveyard_NotWhereAHangUpsDeadArrive()
    {
        RoomKey room = new(17, 3250);
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.ListedOn(room, Vanilla));
        Assert.Equal(new[] { new RoomKey(17, 3247) }, HangupDeathArrivalRooms.DeathArrivalsOn(room, Vanilla));
        Assert.Equal(new[] { new RoomKey(12, 1) }, HangupDeathArrivalRooms.DeathArrivalsOn(new RoomKey(12, 2381), Vanilla));
        Assert.Equal(new[] { new RoomKey(17, 2514) }, HangupDeathArrivalRooms.DeathArrivalsOn(new RoomKey(17, 2020), Paradigm));
        // 17/3253 sits among them with a graveyard of its own and no leave room.
        Assert.Equal(new[] { new RoomKey(17, 35) }, HangupDeathArrivalRooms.DeathArrivalsOn(new RoomKey(17, 3253), Vanilla));
        // Everywhere else the two are the same.
        Assert.Equal(new[] { Temple, EarthenTomb }, HangupDeathArrivalRooms.DeathArrivalsOn(Darkwood, Vanilla));
        Assert.Equal(new[] { new RoomKey(1, 2190) }, HangupDeathArrivalRooms.DeathArrivalsOn(new RoomKey(1, 2171), Paradigm));
        Assert.Null(HangupDeathArrivalRooms.DeathArrivalsOn(Kingsport, Paradigm));
    }

    // What the bug report and the judgement both go by.
    [Fact]
    public void ArrivalsOn_SaysWhichRoomsAndWhatTheyRestOn()
    {
        Assert.Equal((true, false), Rests(HangupDeathArrivalRooms.ArrivalsOn(Darkwood, Paradigm, InAlbion)));
        (IReadOnlyList<RoomKey>? rooms, bool allKnown, bool albion) =
            HangupDeathArrivalRooms.ArrivalsOn(BankOfAlbion, Paradigm, InAlbion);
        Assert.Equal(new[] { Vault }, rooms);
        Assert.Equal((false, true), (allKnown, albion));
        Assert.Null(HangupDeathArrivalRooms.ArrivalsOn(PortBlackwater, Paradigm, InAlbion).Rooms);
        Assert.Null(HangupDeathArrivalRooms.ArrivalsOn(Shadowmere, Paradigm, InAlbion).Rooms);
        Assert.Null(HangupDeathArrivalRooms.ArrivalsOn(TerraIsta, Paradigm, InAlbion).Rooms);
        Assert.Null(HangupDeathArrivalRooms.ArrivalsOn(Kingsport, Vanilla, InAlbion).Rooms);

        static (bool, bool) Rests((IReadOnlyList<RoomKey>? Rooms, bool AllKnown, bool Albion) on) => (on.AllKnown, on.Albion);
    }

    // ----- Rooms whose arrival room was learned -------------------------------
    //
    // For a room no list covers, what the client saw is all there is: a death in
    // Terra Ista (14/1813) ended in 14/1900, and the set remembers it. That is a
    // room to say yes in, and nothing more.

    private static readonly RoomKey IsleShrine = new(14, 1900);

    // A death seen in each of these rooms ended in the shrine.
    private static IReadOnlyList<LearnedArrival>? Learned(RoomKey room) =>
        room == TerraIsta || room == Darkwood || room == Kingsport ? new[] { new LearnedArrival(IsleShrine, room) } : null;

    private static HangupEntryRoomReading WithLearned(RoomKey hungUpIn, RoomKey enteredIn, HangupArrivalListTrust trust) =>
        HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, trust, couldBeTakenFor: null, InAlbion, Learned);

    [Fact]
    public void AParadigmRoomOutsideAlbion_SaysYesInItsLearnedRoom_AndNothingElsewhere()
    {
        HangupEntryRoomReading reading = WithLearned(TerraIsta, IsleShrine, Paradigm);

        Assert.True(reading.Says);
        Assert.Equal(TerraIsta, reading.LearnedFrom);
        Assert.False(reading.Albion);
        // A hang-up leaves through the room's leave-the-game room first, which the
        // data doesn't carry for these rooms: nothing is the wrong room, the room
        // it left and Albion's vault included.
        Assert.Null(WithLearned(TerraIsta, TerraIsta, Paradigm).Says);
        Assert.Null(WithLearned(TerraIsta, new RoomKey(14, 1950), Paradigm).Says);
        Assert.Null(WithLearned(TerraIsta, Vault, Paradigm).Says);
        Assert.Null(WithLearned(TerraIsta, Temple, Paradigm).Says);
        // A room nothing is known for, by its own reading or its land's, says nothing.
        Assert.Null(WithLearned(new RoomKey(14, 1814), IsleShrine, Paradigm).Says);
    }

    // A room with no reading of its own takes its land's: the room a death seen
    // elsewhere in the land ended in, and the reading says which room that was.
    [Fact]
    public void ARoomOfTheSameLand_TakesTheReadingOfTheRoomADeathWasSeenIn()
    {
        RoomKey path = new(14, 1814);
        IReadOnlyList<LearnedArrival>? Land(RoomKey room) =>
            room == path || room == TerraIsta ? new[] { new LearnedArrival(IsleShrine, TerraIsta) } : null;

        HangupEntryRoomReading reading = HangupDeathArrivalRooms.Read(path, IsleShrine, Paradigm, null, InAlbion, Land);

        Assert.True(reading.Says);
        Assert.Equal(TerraIsta, reading.LearnedFrom);
        Assert.Null(HangupDeathArrivalRooms.Read(path, path, Paradigm, null, InAlbion, Land).Says);
        Assert.Null(HangupDeathArrivalRooms.Read(path, Temple, Paradigm, null, InAlbion, Land).Says);
    }

    // Where the list is the world's own, a pair on file is not read at all.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    public void WhatWasLearned_IsNeverSetAgainstTheList_WhereTheListIsTheWorldsOwn(HangupArrivalListTrust trust)
    {
        Assert.Null(HangupDeathArrivalRooms.LearnedFor(Darkwood, trust, InAlbion, Learned));
        Assert.False(WithLearned(Darkwood, IsleShrine, trust).Says);
        Assert.True(WithLearned(Darkwood, Temple, trust).Says);
        Assert.False(WithLearned(Darkwood, Temple, trust).Learned);
        Assert.Equal(new[] { Temple, EarthenTomb },
            HangupDeathArrivalRooms.ArrivalsOn(Darkwood, trust, InAlbion, Learned).Rooms);
    }

    // A Stock board whose rooms may have been edited: the list is Stock's word,
    // and the room a death was seen to end in is one more to say yes in. The
    // list's own rooms still say yes, and "it never moved" still says no.
    [Fact]
    public void OnAnEditedBoard_TheLearnedRoom_IsOneMoreRoomToSayYesIn()
    {
        Assert.Equal(new[] { new LearnedArrival(IsleShrine, Darkwood) },
            HangupDeathArrivalRooms.LearnedFor(Darkwood, Edited, InAlbion, Learned));
        HangupEntryRoomReading reading = WithLearned(Darkwood, IsleShrine, Edited);
        Assert.True(reading.Says);
        Assert.True(reading.Learned);
        Assert.True(WithLearned(Darkwood, Temple, Edited).Says);
        Assert.False(WithLearned(Darkwood, Temple, Edited).Learned);
        Assert.False(WithLearned(Darkwood, Darkwood, Edited).Says);
        Assert.Null(WithLearned(Darkwood, new RoomKey(1, 110), Edited).Says);
    }

    // A room's own reading always wins for that room, Albion included: an Albion
    // room seen to send its dead somewhere else than the Burial Vault says yes
    // there and no longer in the vault. What some other room of its land showed
    // doesn't stand against Albion's known room.
    [Fact]
    public void AnAlbionRoom_GoesByItsOwnReading_AndNotByItsLands()
    {
        Assert.Equal(new[] { IsleShrine }, HangupDeathArrivalRooms.ArrivalsOn(Kingsport, Paradigm, InAlbion, Learned).Rooms);
        HangupEntryRoomReading own = WithLearned(Kingsport, IsleShrine, Paradigm);
        Assert.Equal((true, false, Kingsport), (own.Says == true, own.Albion, own.LearnedFrom!.Value));
        Assert.Null(WithLearned(Kingsport, Vault, Paradigm).Says);
        Assert.Null(WithLearned(Kingsport, Kingsport, Paradigm).Says);

        // The Bank of Albion has no reading of its own; its land has one.
        IReadOnlyList<LearnedArrival>? Land(RoomKey room) => new[] { new LearnedArrival(IsleShrine, Kingsport) };
        Assert.Equal(new[] { Vault }, HangupDeathArrivalRooms.ArrivalsOn(BankOfAlbion, Paradigm, InAlbion, Land).Rooms);
        HangupEntryRoomReading vault = HangupDeathArrivalRooms.Read(BankOfAlbion, Vault, Paradigm, null, InAlbion, Land);
        Assert.Equal((true, true, false), (vault.Says == true, vault.Albion, vault.Learned));
        Assert.Null(HangupDeathArrivalRooms.Read(BankOfAlbion, IsleShrine, Paradigm, null, InAlbion, Land).Says);
    }

    // A room seen to send its dead to itself: being there says nothing either way.
    [Fact]
    public void ARoomLearnedAsItsOwnArrivalRoom_SaysNothing()
    {
        static IReadOnlyList<LearnedArrival>? Own(RoomKey room) => new[] { new LearnedArrival(room, room) };

        Assert.Null(HangupDeathArrivalRooms.Read(TerraIsta, TerraIsta, Paradigm, null, InAlbion, Own).Says);
        // On an edited board that takes "it never moved" away too.
        Assert.Null(HangupDeathArrivalRooms.Read(Darkwood, Darkwood, Edited, null, InAlbion, Own).Says);
    }

    // Hung up in the very room its dead arrive in: being there says nothing.
    [Theory]
    [InlineData(1, 2189)]
    [InlineData(1, 142)]
    [InlineData(17, 2415)]
    public void HungUpInItsOwnArrivalRoom_SaysNothing(int map, int room)
    {
        RoomKey left = new(map, room);
        Assert.Null(Says(left, left, Vanilla));
        Assert.Null(Says(left, new RoomKey(1, 110), Vanilla));
    }

    // 17/785 and 17/1773 put a character that leaves the game in them in 17/2415,
    // the room their dead arrive in too: it comes back there dead or alive.
    [Theory]
    [InlineData(785)]
    [InlineData(1773)]
    public void ARoomLeftThroughItsOwnArrivalRoom_SaysNothing(int room) =>
        Assert.Null(Says(new RoomKey(17, room), new RoomKey(17, 2415), Vanilla));

    [Fact]
    public void AnUnknownRoom_OrOneTheListDoesNotCover_SaysNothing()
    {
        Assert.Null(Says(null, Temple, Vanilla));
        Assert.Null(Says(Darkwood, null, Vanilla));
        Assert.Null(Says(new RoomKey(18, 40), Temple, Edited));
        Assert.Null(Says(new RoomKey(18, 40), new RoomKey(18, 40), Edited));
    }

    // An arena room's dead are moved like any other room's, whether the death was
    // the arena's free one or an ordinary one. The Training Grounds' arrive in
    // their own Halls of the Dead (16/542), itself an arena room.
    [Fact]
    public void AnArenaRoom_IsARoomLikeAnyOther()
    {
        RoomKey trainingGrounds = new(16, 500);
        Assert.True(Says(trainingGrounds, new RoomKey(16, 542), Vanilla));
        Assert.False(Says(trainingGrounds, trainingGrounds, Vanilla));
        Assert.Null(Says(new RoomKey(16, 542), new RoomKey(16, 542), Vanilla));
        Assert.True(Says(new RoomKey(1, 206), new RoomKey(1, 205), Edited));
    }

    // Which game data is the unmodified Stock world the list was read from: its
    // Info row says Legit 1 and Custom "Default", as the 1.11p export does. A
    // Stock board whose sysop edited it exports with its own name and no such
    // mark, and Paradigm is Legit 2.
    [Theory]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Default", "Legit": 1 }]""", true, false)]
    [InlineData("""[{ "Dat File Version": "1.9.1", "Custom": "Paradigm", "Legit": 2 }]""", false, true)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Some Board", "Legit": 0 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Some Board", "Legit": 1 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p", "Custom": "Default", "Legit": 0 }]""", false, false)]
    [InlineData("""[{ "Dat File Version": "v1.11p" }]""", false, false)]
    [InlineData("[]", false, false)]
    public void TheInfoRow_SaysWhetherTheSetIsTheUnmodifiedStockWorld(string info, bool vanilla, bool paradigm)
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-arrivaltrust-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        try
        {
            File.WriteAllText(Path.Combine(root, "alpha", "Info.json"), info);
            GameDataCache cache = new(root);
            Assert.False(cache.ActiveSetIsVanillaStock);   // no set active
            cache.SwitchSet("alpha");

            Assert.Equal(vanilla, cache.ActiveSetIsVanillaStock);
            Assert.Equal(paradigm, cache.ActiveRealm == MudPlay.Game.RealmType.ParaMud);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ----- An arrival room the map takes for its look-alike -----------------
    //
    // The map knows a room by its name and the exits it prints. These are the
    // rows of the Stock 1.11p Rooms table (the Paradigm 1.9.1 rows are the same):
    // the Portal Room the dead of map 11's arena arrive in prints only its south
    // exit, its three portals being text exits, and so shows exactly as the other
    // Portal Room; the two Dark Caves both print south.

    private const string RealRooms = """
        [
          { "Map Number": 11, "Room Number": 5, "Name": "Crimson Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "11/6", "W": "0",
            "NE": "11/4", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 35, "Name": "Portal Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "11/1 (Text: go crimson, enter crimson, go crimson portal)", "S": "1/488",
            "E": "11/34 (Text: go golden, enter golden, go golden portal)",
            "W": "11/26 (Text: go ebony, enter ebony, go ebony portal)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 37, "Name": "Portal Room",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "11/35", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 11, "Room Number": 44, "Name": "Ebony Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "11/19", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 2056, "Name": "Dark Cave",
            "Light": -300, "Shop": 0, "Lair": "", "Delay": 2,
            "N": "0", "S": "16/2672", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 2057, "Name": "Dark Cave",
            "Light": -300, "Shop": 0, "Lair": "", "Delay": 2,
            "N": "0", "S": "15/2056", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 109, "Name": "Darkwood Forest",
            "Light": -150, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/116", "E": "0", "W": "0",
            "NE": "1/110", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2189, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/527", "D": "0" }
        ]
        """;

    private static readonly RoomKey CrimsonPassage = new(11, 5);
    private static readonly RoomKey PortalRoom = new(11, 35);
    private static readonly RoomKey OtherPortalRoom = new(11, 37);
    private static readonly RoomKey DarkCave = new(15, 2057);
    private static readonly RoomKey DarkCaveGraveyard = new(15, 2056);
    private static readonly HashSet<Direction> SouthOnly = new() { Direction.S };

    private sealed class RealGraph : IDisposable
    {
        private readonly string _root;
        public RoomGraphManager Graph { get; }

        public RealGraph(string rooms = RealRooms)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-arrivalrooms-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            Graph = new RoomGraphManager(cache);
            Graph.OnActiveSetChanged("alpha");
        }

        // The room the hang-up recheck takes a character it last had in `last` to
        // come back in, shown a display of this name and these exits: the one the map
        // confirms, or with look-alikes it can't tell apart, the one standing in.
        public RoomKey? EnteredAfter(RoomKey last, string name, HashSet<Direction> exits)
        {
            RoomTracker tracker = new(Graph);
            tracker.SetLocated(last);
            tracker.NoteRoomObserved(new RoomObservation(name, exits));
            return tracker.State.Confidence == RoomConfidence.Confirmed
                ? tracker.State.CurrentRoom?.Key : tracker.DisplayLookAlike;
        }

        public HangupEntryRoomReading Read(RoomKey hungUpIn, RoomKey? enteredIn, HangupArrivalListTrust trust) =>
            HangupDeathArrivalRooms.Read(hungUpIn, enteredIn, trust, Graph.CouldBeTakenFor);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void TheGraph_SaysWhichRoomsAnArrivalRoomsDisplayFits()
    {
        using RealGraph g = new();

        // The Portal Room's display (south only) fits the other Portal Room.
        Assert.True(g.Graph.CouldBeTakenFor(PortalRoom, OtherPortalRoom));
        Assert.True(g.Graph.CouldBeTakenFor(DarkCaveGraveyard, DarkCave));
        // Another name, the room itself, a room the graph doesn't have.
        Assert.False(g.Graph.CouldBeTakenFor(PortalRoom, CrimsonPassage));
        Assert.False(g.Graph.CouldBeTakenFor(PortalRoom, PortalRoom));
        Assert.False(g.Graph.CouldBeTakenFor(Temple, new RoomKey(1, 1)));
        Assert.False(g.Graph.CouldBeTakenFor(new RoomKey(9, 9), PortalRoom));
    }

    // A hidden exit prints on no exits line, whichever kind it is: one a plain step
    // crosses (`Hidden/Passable`, which the walker treats as an ordinary exit), one
    // that has to be searched for, one opened by an action elsewhere. A room whose
    // only other exits are hidden shows as its plainer namesake does.
    [Theory]
    [InlineData("(Hidden/Passable)")]
    [InlineData("(Hidden/Searchable)")]
    [InlineData("(Text: go crack, enter crack)")]
    public void AnExitThatPrintsOnNoExitsLine_DoesNotTellTwoRoomsApart(string hint)
    {
        using RealGraph g = new(TwoCellars(hint));

        // 9/1 prints south only, which is all 9/2 has.
        Assert.True(g.Graph.CouldBeTakenFor(new RoomKey(9, 1), new RoomKey(9, 2)));
        // The other way round too: what 9/2 prints is an exit of 9/1.
        Assert.True(g.Graph.CouldBeTakenFor(new RoomKey(9, 2), new RoomKey(9, 1)));
    }

    // An exit that does print tells them apart: the room that shows it can't be
    // taken for one that lacks it, though the plainer one still fits the other.
    [Theory]
    [InlineData("")]
    [InlineData("(Door)")]
    public void AnExitThatPrints_TellsTwoRoomsApart(string hint)
    {
        using RealGraph g = new(TwoCellars(hint));

        Assert.False(g.Graph.CouldBeTakenFor(new RoomKey(9, 1), new RoomKey(9, 2)));
        Assert.True(g.Graph.CouldBeTakenFor(new RoomKey(9, 2), new RoomKey(9, 1)));
    }

    // Two rooms of one name: 9/1 has a north exit of the given kind beside the
    // south exit that is all 9/2 has.
    private static string TwoCellars(string northHint) => """
        [
          { "Map Number": 9, "Room Number": 1, "Name": "Cellar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "9/3 HINT", "S": "9/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 2, "Name": "Cellar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "9/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 9, "Room Number": 3, "Name": "Passage",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "9/1", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """.Replace(" HINT", northHint.Length == 0 ? "" : " " + northHint);

    // Killed by a hang-up in the Crimson Passage, the character stands in 11/35.
    // The map, shown `Portal Room` with a south exit, can't say whether it is 11/35
    // or 11/37, whose exits are exactly that: one of them stands in. Coming back in
    // either is coming back where the dead arrive, and for the twin the wording says
    // which room that really is.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void ThePortalRoom_TakenForItsTwin_StillSaysADeath(HangupArrivalListTrust trust)
    {
        using RealGraph g = new();
        Assert.Equal(new[] { PortalRoom }, HangupDeathArrivalRooms.For(CrimsonPassage));
        RoomKey? entered = g.EnteredAfter(CrimsonPassage, "Portal Room", SouthOnly);
        Assert.Contains(entered, new RoomKey?[] { PortalRoom, OtherPortalRoom });
        Assert.True(g.Read(CrimsonPassage, PortalRoom, trust).Says);
        HangupEntryRoomReading reading = g.Read(CrimsonPassage, OtherPortalRoom, trust);

        Assert.True(reading.Says);
        Assert.Equal(PortalRoom, reading.TakenFor);
        // An ordinary hang-up death in 11/44 (not an arena room) reads the same.
        Assert.True(g.Read(new RoomKey(11, 44), OtherPortalRoom, trust).Says);
        // Without the graph's word the twin is "some other room", which where the
        // list is trusted would say "no death": the regression this pins.
        Assert.False(HangupDeathArrivalRooms.Read(CrimsonPassage, OtherPortalRoom, Vanilla).Says);
    }

    // 15/2057's dead arrive in 15/2056, and both are `Dark Cave` with a south
    // exit: the display after a death fits the room the map already holds, so it
    // stays there. That is not "back where it left", and it says nothing.
    [Theory]
    [InlineData(Vanilla)]
    [InlineData(Paradigm)]
    [InlineData(Edited)]
    public void ARoomThatLooksLikeItsOwnArrivalRoom_SaysNothing_WhereverTheMapPutsIt(HangupArrivalListTrust trust)
    {
        using RealGraph g = new();
        Assert.Equal(new[] { DarkCaveGraveyard }, HangupDeathArrivalRooms.For(DarkCave));
        RoomKey? confirmed = g.EnteredAfter(DarkCave, "Dark Cave", SouthOnly);
        Assert.Equal(DarkCave, confirmed);

        Assert.Null(g.Read(DarkCave, confirmed, trust).Says);
        // Without the graph's word it reads "the room it left": no death.
        Assert.False(HangupDeathArrivalRooms.Read(DarkCave, confirmed, trust).Says);
    }

    // The look-alike only stands for the arrival room of the room hung up in: a
    // hang-up in Darkwood Forest, whose dead arrive in the Temple, is not helped
    // by coming back in a Portal Room.
    [Fact]
    public void ALookAlikeOfSomeOtherRoomsArrivalRoom_IsJustAnotherRoom()
    {
        using RealGraph g = new();

        Assert.False(g.Read(Darkwood, OtherPortalRoom, Vanilla).Says);
        Assert.Null(g.Read(Darkwood, OtherPortalRoom, Edited).Says);
        Assert.True(g.Read(Darkwood, Temple, Vanilla).Says);
        Assert.False(g.Read(Darkwood, Darkwood, Vanilla).Says);
    }
}
