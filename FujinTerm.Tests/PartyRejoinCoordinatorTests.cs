using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

// Follower-side reconnect auto-rejoin. Drives the coordinator through the real
// router lines + PartyState so the whole PartyManager -> coordinator wiring is
// exercised: becoming a follower stamps the crash-survivable leader memory,
// leaving clears it, and a reconnect (Arm + first in-game room display) telepaths
// @comeback to the remembered leader. There's no follower-side wait — the leader
// owns the pickup once @comeback is on the wire — so this only covers the memory
// bookkeeping, the one-shot fire, the remembered-leader force-accept predicate,
// and the @forget-decline forget path.
public sealed class PartyRejoinCoordinatorTests : IDisposable
{
    // Single room 1/1 so the confirmed-room @comeback variant has a real key to
    // attach via RoomTracker.SetLocated.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private readonly string _root;

    public PartyRejoinCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-rejoin-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private sealed class Fixture
    {
        public required MessageRouter Router { get; init; }
        public required PartyManager Manager { get; init; }
        public required RoomTracker Tracker { get; init; }
        public required PartyRejoinCoordinator Coord { get; init; }
        // Every value handed to PersistLeader, in order — mirrors the disk
        // write-through the crash-survivable memory relies on.
        public required List<string?> Persisted { get; init; }

        // Outbound wire buffers decoded back to strings (Latin1 + trailing \r).
        public IEnumerable<string> Sent() =>
            Coord.LastSentForTests.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public string? LastPersisted => Persisted.Count == 0 ? null : Persisted[^1];
    }

    private Fixture Setup(Func<bool>? isAutoEnabled = null)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        PartyManager mgr = new(router, state);

        PartyRejoinCoordinator coord = new(router, state, tracker, isAutoEnabled);
        List<string?> persisted = new();
        coord.PersistLeader = leader => persisted.Add(leader);

        return new Fixture
        {
            Router = router, Manager = mgr, Tracker = tracker,
            Coord = coord, Persisted = persisted,
        };
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // ----- Follower-membership memory -----------------------------------

    [Fact]
    public void FreshSession_NoLeaderRemembered()
    {
        var f = Setup();
        Assert.Null(f.Coord.RememberedLeader);
    }

    [Fact]
    public void BecomingFollower_RemembersLeaderAndWritesThrough()
    {
        var f = Setup();
        f.Router.Dispatch(Line("You are now following Fujin."));

        Assert.Equal("Fujin", f.Coord.RememberedLeader);
        Assert.Equal("Fujin", f.LastPersisted);
    }

    [Fact]
    public void LeavingParty_ForgetsLeaderAndWritesThroughNull()
    {
        var f = Setup();
        f.Router.Dispatch(Line("You are now following Fujin."));
        Assert.Equal("Fujin", f.Coord.RememberedLeader);

        f.Router.Dispatch(Line("You are no longer following Fujin."));

        Assert.Null(f.Coord.RememberedLeader);
        Assert.Null(f.LastPersisted);
    }

    [Fact]
    public void LeadingParty_NeverRemembersAnyone()
    {
        // Someone follows US — we're the leader, so there's nothing to rejoin.
        var f = Setup();
        f.Router.Dispatch(Line("Helper started to follow you."));

        Assert.True(f.Manager.State.SelfIsLeader);
        Assert.Null(f.Coord.RememberedLeader);
        Assert.Empty(f.Persisted);
    }

    // ----- Reconnect @comeback fire -------------------------------------

    [Fact]
    public void ArmThenRoom_NoLeaderRemembered_StaysQuiet()
    {
        var f = Setup();
        f.Coord.Arm();
        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Coord.LastSentForTests);
    }

    [Fact]
    public void HydratedLeader_ArmThenFirstRoom_SendsBareComeback()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Equal("/Fujin @comeback", f.Sent().Single());
    }

    [Fact]
    public void ConfirmedRoom_SendsComebackWithRoomKey()
    {
        var f = Setup();
        f.Tracker.SetLocated(new RoomKey(1, 1));
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Equal("/Fujin @comeback 1/1", f.Sent().Single());
    }

    [Fact]
    public void RoomWithoutArm_DoesNothing()
    {
        // No Arm() — a room display outside a fresh connect must not fire.
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Coord.LastSentForTests);
    }

    [Fact]
    public void Comeback_IsOneShotPerConnect()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));
        f.Router.Dispatch(Line("Obvious exits: east, west"));

        Assert.Single(f.Coord.LastSentForTests); // latch consumed on first room
    }

    [Fact]
    public void KillSwitch_SuppressesComeback()
    {
        var f = Setup(isAutoEnabled: () => false);
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Empty(f.Coord.LastSentForTests);
    }

    // ----- Remembered-leader force-accept predicate ---------------------

    [Fact]
    public void IsRememberedLeader_MatchesGivenName()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");

        Assert.True(f.Coord.IsRememberedLeader("Fujin"));
        // Room-entry / invite lines carry the family suffix; the remembered
        // leader is a bare given name.
        Assert.True(f.Coord.IsRememberedLeader("Fujin WuzHere"));
        Assert.False(f.Coord.IsRememberedLeader("Stranger"));
    }

    [Fact]
    public void IsRememberedLeader_FalseWhenNothingRemembered()
    {
        var f = Setup();
        Assert.False(f.Coord.IsRememberedLeader("Fujin"));
    }

    // ----- @forget decline forgets the leader ---------------------------

    [Fact]
    public void ForgetRememberedLeader_ClearsMemoryAndWritesThroughNull()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");

        f.Coord.ForgetRememberedLeader("Fujin WuzHere");

        Assert.Null(f.Coord.RememberedLeader);
        Assert.Null(f.LastPersisted);          // write-through cleared the slot
    }

    [Fact]
    public void ForgetRememberedLeader_DifferentLeader_IsNoOp()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");

        f.Coord.ForgetRememberedLeader("Stranger");

        Assert.Equal("Fujin", f.Coord.RememberedLeader);
        Assert.Empty(f.Persisted);             // no write-through for a mismatch
    }

    // ----- Lifecycle ----------------------------------------------------

    [Fact]
    public void Hydrate_ResetsArm()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        // A profile swap re-hydrates — the stale arm must not survive, so the
        // first room after the swap fires nothing until Arm() is called again.
        f.Coord.HydrateRememberedLeader("Helper");
        f.Router.Dispatch(Line("Obvious exits: north, south"));

        Assert.Equal("Helper", f.Coord.RememberedLeader);
        Assert.Empty(f.Coord.LastSentForTests);
        // No write-through — the value came straight off disk.
        Assert.Empty(f.Persisted);
    }

    [Fact]
    public void Dispose_StopsReactingToLines()
    {
        var f = Setup();
        f.Coord.HydrateRememberedLeader("Fujin");
        f.Coord.Arm();

        f.Coord.Dispose();
        f.Router.Dispatch(Line("Obvious exits: north, south"));
        f.Router.Dispatch(Line("You are now following Helper."));

        Assert.Empty(f.Coord.LastSentForTests);
    }
}
