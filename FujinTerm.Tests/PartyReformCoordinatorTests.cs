using System;
using System.Collections.Generic;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

// Leader-side reconnect party reform — the mirror of PartyRejoinCoordinator.
// Drives the coordinator through the real router lines + PartyManager so the whole
// snapshot -> reform wiring is exercised: dropping while leading captures the
// followers, a reconnect (Arm + first in-game room) rebases the grace window so
// each follower's @comeback re-authorises and fires MemberDisconnected so the
// movement gate would hold, and a solo/follower drop reforms nothing.
public sealed class PartyReformCoordinatorTests
{
    private sealed class Fixture
    {
        public required MessageRouter Router { get; init; }
        public required PartyManager Manager { get; init; }
        public required PartyReformCoordinator Coord { get; init; }
        // Given names fired through MemberDisconnected — what
        // PartyDisconnectMovementGate rides to hold the loop.
        public required List<string> Held { get; init; }
    }

    private static Fixture Setup(Func<bool>? isAutoEnabled = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);
        PartyReformCoordinator coord = new(router, mgr, isAutoEnabled);

        List<string> held = new();
        mgr.MemberDisconnected += given => held.Add(given);

        return new Fixture { Router = router, Manager = mgr, Coord = coord, Held = held };
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static void LeadFollower(Fixture f, string follower) =>
        f.Router.Dispatch(Line($"{follower} started to follow you."));

    // ----- Disconnect snapshot ------------------------------------------

    [Fact]
    public void NoteDisconnected_WhileLeading_SnapshotsFollowers()
    {
        var f = Setup();
        LeadFollower(f, "Helper");
        Assert.True(f.Manager.State.SelfIsLeader);

        f.Coord.NoteDisconnected();

        Assert.Equal(new[] { "Helper" }, f.Coord.PendingReform);
    }

    [Fact]
    public void NoteDisconnected_Solo_SnapshotsNothing()
    {
        var f = Setup();
        f.Coord.NoteDisconnected();
        Assert.Empty(f.Coord.PendingReform);
    }

    [Fact]
    public void NoteDisconnected_AsFollower_SnapshotsNothing()
    {
        // We follow someone else — there's no party of ours to reform.
        var f = Setup();
        f.Router.Dispatch(Line("You are now following Fujin."));
        Assert.False(f.Manager.State.SelfIsLeader);

        f.Coord.NoteDisconnected();

        Assert.Empty(f.Coord.PendingReform);
    }

    // ----- Reconnect reform fire ----------------------------------------

    [Fact]
    public void ArmThenRoom_ReformsAndRebasesGrace()
    {
        var f = Setup();
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();

        f.Coord.Arm();
        f.Router.Dispatch(Line("Obvious exits: north, south"));

        // Roster reset to solo, grace re-stamped so @comeback re-authorises, and
        // the movement gate held via MemberDisconnected.
        Assert.False(f.Manager.State.IsInParty);
        Assert.False(f.Manager.State.SelfIsLeader);
        Assert.True(f.Manager.WasRecentlyPartied("Helper"));
        Assert.Equal(new[] { "Helper" }, f.Held);
        Assert.Empty(f.Coord.PendingReform); // consumed
    }

    [Fact]
    public void RoomWithoutArm_DoesNothing()
    {
        var f = Setup();
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Held);
        Assert.Equal(new[] { "Helper" }, f.Coord.PendingReform); // still pending
    }

    [Fact]
    public void ArmThenRoom_NothingSnapshotted_StaysQuiet()
    {
        var f = Setup();
        f.Coord.Arm();
        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Held);
    }

    [Fact]
    public void Reform_IsOneShotPerConnect()
    {
        var f = Setup();
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));
        f.Router.Dispatch(Line("Obvious exits: east, west"));

        Assert.Single(f.Held); // latch consumed on the first room
    }

    [Fact]
    public void KillSwitch_SuppressesReform()
    {
        var f = Setup(isAutoEnabled: () => false);
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Held);
        Assert.Empty(f.Coord.PendingReform); // still cleared so it can't re-fire
    }

    [Fact]
    public void FailedRedial_KeepsPriorSnapshot()
    {
        // The real drop snapshots Helper; a later solo NoteDisconnected (e.g. the
        // caller mis-fires) must not silently wipe it — but the caller gates on
        // wasConnected, so in practice only a real leading-drop snapshots. Here we
        // prove the snapshot the reform needs survives an Arm without a room.
        var f = Setup();
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();

        f.Coord.Arm(); // reconnect latch opens, but no room yet
        Assert.Equal(new[] { "Helper" }, f.Coord.PendingReform);
    }

    // ----- Lifecycle ----------------------------------------------------

    [Fact]
    public void Dispose_StopsReactingToRooms()
    {
        var f = Setup();
        LeadFollower(f, "Helper");
        f.Coord.NoteDisconnected();
        f.Coord.Arm();

        f.Coord.Dispose();
        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Held);
    }
}
