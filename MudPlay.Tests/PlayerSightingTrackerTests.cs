using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

public sealed class PlayerSightingTrackerTests
{
    private static Room MakeRoom(int map, int room, string name) => new()
    {
        Key = new RoomKey(map, room),
        Name = name,
        Exits = RoomExitSet.Empty,
    };

    private static RoomEntitiesObservation AlsoHere(params string[] playerNames)
    {
        var entities = playerNames
            .Select(n => new RoomEntity(n, n, EntityKind.Player, null))
            .ToArray();
        return new RoomEntitiesObservation("Also here: ...", entities, DateTimeOffset.Now);
    }

    // Both an open walk-in and a perceived failed-sneak ("You notice <name>
    // sneaking in …") reach the tracker as the same Player-kinded ArrivalObserved
    // event, so one builder covers both cases.
    private static RoomEntryArrivalEvent Arrival(string name) =>
        new(name, EntityKind.Player, "north", DateTimeOffset.Now);

    // Sightings key on the given name, so a family rename ("Bob Smith" → "Bob
    // Jones") folds into the same row and just bumps the count.
    [Fact]
    public void AlsoHere_RecordsPlayer_KeyedByGivenName()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob Smith"));

        PlayerSighting row = Assert.Single(t.Snapshot());
        Assert.Equal("Bob", row.Name);
        Assert.Equal(1, row.TimesSeen);
        Assert.Equal(1, row.Map);
        Assert.Equal(100, row.Room);
        Assert.Equal("Town Square", row.RoomName);
    }

    // With no `who` run, a player beside us lists as unknown; a name that reads as a
    // player's is counted anyway, a monster's isn't, and the later re-read as a
    // Player doesn't count them twice (report paradigm-20261013-173152).
    [Fact]
    public void AlsoHere_UnknownNameThatReadsAsAPlayer_IsCountedOnce()
    {
        Room? current = MakeRoom(1, 2150, "Newhaven Arena");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(new RoomEntitiesObservation("Also here: ...", new[]
        {
            new RoomEntity("Thesifer", "Thesifer", EntityKind.Unknown, null),
            new RoomEntity("angry carrion beast", "angry carrion beast", EntityKind.Unknown, null),
        }, DateTimeOffset.Now));
        t.NoteAlsoHere(AlsoHere("Thesifer"));

        PlayerSighting row = Assert.Single(t.Snapshot());
        Assert.Equal("Thesifer", row.Name);
        Assert.Equal(1, row.TimesSeen);
    }

    // Standing still (re-pressing Enter re-fires the same "Also here:" line)
    // counts the occupant once per visit, not once per redisplay.
    [Fact]
    public void AlsoHere_SameRoomVisit_CountsOncePerPlayer()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob"));

        Assert.Equal(1, Assert.Single(t.Snapshot()).TimesSeen);
    }

    // Leaving and re-entering starts a fresh visit, so a genuine re-encounter
    // counts again.
    [Fact]
    public void AlsoHere_AfterRoomChange_CountsAgain()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteAlsoHere(AlsoHere("Bob"));
        current = MakeRoom(1, 101, "North Road");
        t.NoteAlsoHere(AlsoHere("Bob"));
        current = MakeRoom(1, 100, "Town Square");
        t.NoteAlsoHere(AlsoHere("Bob"));

        Assert.Equal(3, Assert.Single(t.Snapshot()).TimesSeen);
    }

    // A walk-in always counts, and marks the player counted for the visit so the
    // room redisplay that follows the arrival doesn't double-count.
    [Fact]
    public void Arrival_CountsOnce_AndSuppressesFollowingAlsoHere()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        t.NoteArrival(Arrival("Bob"));
        t.NoteAlsoHere(AlsoHere("Bob")); // the redisplay that follows the walk-in

        Assert.Equal(1, Assert.Single(t.Snapshot()).TimesSeen);
    }

    // A perceived failed sneak reaches the tracker via the same Player-kinded
    // ArrivalObserved event as a walk-in, so it records like any other arrival.
    [Fact]
    public void SneakArrival_RecordsPlayer()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        // "You notice Bob sneaking in from the north." — RoomEntryWatcher tags it
        // Player and fires the identical arrival event a walk-in would.
        t.NoteArrival(new RoomEntryArrivalEvent("Bob", EntityKind.Player, "north", DateTimeOffset.Now));

        PlayerSighting row = Assert.Single(t.Snapshot());
        Assert.Equal("Bob", row.Name);
        Assert.Equal(1, row.TimesSeen);
    }

    [Fact]
    public void Self_IsNeverRecorded()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me Family");

        t.NoteAlsoHere(AlsoHere("Me Different"));
        t.NoteArrival(Arrival("Me"));

        Assert.Empty(t.Snapshot());
    }

    // Party members are a given, not an encounter: skipped on both paths while
    // they're in the party, counted again once they've left it.
    [Fact]
    public void CurrentPartyMembers_AreNotRecorded()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var party = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ally" };
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me",
            isPartyMember: party.Contains);

        t.NoteAlsoHere(AlsoHere("Ally Family", "Bob"));
        t.NoteArrival(Arrival("Ally"));
        Assert.Equal("Bob", Assert.Single(t.Snapshot()).Name);

        party.Clear();
        current = MakeRoom(1, 101, "North Road");
        t.NoteArrival(Arrival("Ally"));
        Assert.Contains(t.Snapshot(), r => r.Name == "Ally");
    }

    [Fact]
    public void PartyStateHasMember_MatchesOnGivenName()
    {
        var state = new PartyState();
        state.Members.Add(new PartyMember { Name = "Ally Family" });
        Assert.True(state.HasMember("ally"));
        Assert.True(state.HasMember("Ally Renamed"));
        Assert.False(state.HasMember("Bob"));
        Assert.False(state.HasMember(""));
    }

    [Fact]
    public void NonPlayerEntities_AreIgnored()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        var obs = new RoomEntitiesObservation(
            "Also here: ...",
            new[]
            {
                new RoomEntity("giant rat", "giant rat", EntityKind.Monster, 42),
                new RoomEntity("something", "something", EntityKind.Unknown, null),
                new RoomEntity("Bob", "Bob", EntityKind.Player, null),
            },
            DateTimeOffset.Now);
        t.NoteAlsoHere(obs);

        Assert.Equal("Bob", Assert.Single(t.Snapshot()).Name);
    }

    // Only the real "Also here:" listing feeds the presence path — the synthetic
    // re-fires (arrival/death/departure/room-change) must not add a player.
    [Fact]
    public void AlsoHere_IgnoresNonAlsoHereSources()
    {
        Room? current = MakeRoom(1, 100, "Town Square");
        var t = new PlayerSightingTracker(() => current, profile: null, selfNameProvider: () => "Me");

        var refire = new RoomEntitiesObservation(
            "Also here: ...",
            new[] { new RoomEntity("Bob", "Bob", EntityKind.Player, null) },
            DateTimeOffset.Now,
            RoomObservationSource.Death);
        t.NoteAlsoHere(refire);

        Assert.Empty(t.Snapshot());
    }
}
