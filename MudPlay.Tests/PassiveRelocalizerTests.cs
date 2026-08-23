using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PassiveRelocalizer recovers a room fix while no engine is attached — the
/// shape a dragged party follower is left in when the tracker goes Suspect
/// or Lost, since AutoWalkManager and LoopRunner both refuse to attach with
/// a null CurrentRoom. Covers the free footstep-replay tier, the party
/// guard on the walking tier, and that a genuinely Lost tracker gets acted
/// on instead of sitting idle.
/// </summary>
public sealed class PassiveRelocalizerTests : IDisposable
{
    private readonly string _root;

    public PassiveRelocalizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-passiverelocalizer-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), FixtureGraph);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Two name+exit-identical "Start" twins (1/1, 1/2), each with a self-loop
    // U exit (so a filler move can bump the tracker's move clock without
    // narrowing or breaking the replay). 1/1.N leads to "A" (1/10), whose own
    // E leads to "C" (1/20); 1/2.N leads to "B" (1/11), a dead end. Replaying
    // N then E survives only the 1/1 branch (1/11 has no E exit), converging
    // on C. Replaying N alone splits the twins by name ("A" vs "B"), the
    // walking tier's own splitting move. C also carries a self-loop S exit
    // that a bare "C" display never lists — RoomTracker's own exact (name,
    // full-mask) search misses it (0 candidates, stays Suspect), but the
    // relocalizer's own subset-based match still accepts a bare "C" render as
    // consistent with standing in C, the live-render check Finding 1 added.
    private const string FixtureGraph = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/10", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/11", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/2", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "1/20", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 20, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/20", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness
    {
        public readonly RoomGraphManager Graph;
        public readonly RoomTracker Tracker;
        public readonly RoomLocator Locator;
        public readonly EngineRecoveryGate Gate;
        public readonly MovementCoordinator Coordinator = new();
        public readonly List<byte[]> Sent = new();

        public Harness(string root)
        {
            var cache = new GameDataCache(root);
            cache.SwitchSet("alpha");
            Graph = new RoomGraphManager(cache);
            Graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(Graph);
            Locator = new RoomLocator(Graph);
            Gate = new EngineRecoveryGate(Graph, Tracker);
        }

        public PassiveRelocalizer NewRelocalizer(bool allowWalking)
        {
            var relocalizer = new PassiveRelocalizer(Tracker, Locator, Graph, Gate, Coordinator)
            {
                AllowWalking = allowWalking,
            };
            relocalizer.SetWireSender(Sent.Add);
            return relocalizer;
        }
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    // Stub engine used only to prove the relocalizer stays inert while one
    // is attached — none of its members are ever expected to fire.
    private sealed class InertEngine : IRecoverableEngine
    {
        public string Name => "Inert";
        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) { }
        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) { }
        public void AbortFromRecoveryFailure(string detail) { }
    }

    /// <summary>
    /// A dragged follower's two steps (N then E) replayed against the
    /// ambiguous "Start" twins converge on the single room reachable by
    /// both hops, with nothing sent to the wire — and the live render
    /// (fed to OnRoomObserved exactly as RoomDisplayParser.RoomParsed would,
    /// ahead of the tracker's own NoteRoomObserved call for the same render)
    /// agrees with that room, so the confirm actually fires.
    /// </summary>
    [Fact]
    public void Replaying_follow_drags_narrows_without_sending_anything()
    {
        var h = new Harness(_root);

        // Unknown -> ambiguous(2 "Start" twins) -> Suspect, CurrentRoom null.
        h.Tracker.NoteRoomObserved(Obs("Start", Direction.N, Direction.U));
        h.Tracker.NoteFollowMove(Direction.N);
        h.Tracker.NoteFollowMove(Direction.E);

        // Constructed here, right before the triggering transition, so its
        // subscription only sees the ONE StateChanged this test cares about.
        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        // A bare "C" display: RoomTracker's own exact (name, full-mask)
        // search misses it (C's real mask carries a self-loop S the display
        // never lists), so the tracker itself stays Suspect on this render —
        // exactly the shape that needs the relocalizer, not RoomTracker's own
        // 1-of-1 fast path, to resolve it. Fed to the relocalizer first, then
        // the tracker, mirroring production order (RoomParsed before
        // RoomTracker.NoteRoomObserved for the same render).
        RoomObservation liveRender = Obs("C");
        relocalizer.OnRoomObserved(liveRender);
        h.Tracker.NoteRoomObserved(liveRender);

        Assert.Empty(h.Sent);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 20), h.Tracker.State.CurrentRoom!.Key);

        relocalizer.Dispose();
    }

    /// <summary>
    /// Finding 1: Stage 1's replay is pure topology (FootprintMatcher.StepBlind
    /// never checks a landing against a display) — it must not assert a
    /// position that contradicts the room actually on screen. Same replay as
    /// the test above (converges on C), but the render fed to OnRoomObserved —
    /// and to the tracker, matching production order — names a room the graph
    /// doesn't even have, so it cannot possibly be C. SetLocated must not fire.
    /// </summary>
    [Fact]
    public void ConvergedReplay_ContradictingTheLiveRender_RefusesToLocate()
    {
        var h = new Harness(_root);

        h.Tracker.NoteRoomObserved(Obs("Start", Direction.N, Direction.U));
        h.Tracker.NoteFollowMove(Direction.N);
        h.Tracker.NoteFollowMove(Direction.E);

        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        RoomObservation contradicting = Obs("Nowhere", Direction.W);
        relocalizer.OnRoomObserved(contradicting);
        h.Tracker.NoteRoomObserved(contradicting);

        Assert.Empty(h.Sent);
        Assert.NotEqual(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Null(h.Tracker.State.CurrentRoom);

        relocalizer.Dispose();
    }

    /// <summary>
    /// Even with walking allowed and nothing to replay (so Stage 2 is the
    /// only path left), the party follower gate must block every send —
    /// marching a follower out of the leader's drag is the one unacceptable
    /// failure mode. Mutation-proven: see the report for the guard removed
    /// and this test observed to fail.
    /// </summary>
    [Fact]
    public void It_never_sends_a_move_while_the_follower_gate_is_asserted()
    {
        var h = new Harness(_root);
        h.Coordinator.AssertGate(MovementCoordinator.FollowerGate, "test", "party follower");

        h.Tracker.NoteRoomObserved(Obs("Start", Direction.N, Direction.U));

        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        // No steps recorded, so replay is a no-op and the twins are still
        // ambiguous — Stage 2 is exactly what would otherwise fire here.
        h.Tracker.NoteRoomObserved(Obs("Nowhere", Direction.W));

        Assert.Empty(h.Sent);

        relocalizer.Dispose();
    }

    /// <summary>
    /// The user's reported bug, as a test: a genuinely Lost tracker (null
    /// CurrentRoom) with a cached accepted observation must not just sit
    /// there — with walking allowed and no party gate asserted, it sends a
    /// real move rather than waiting for the user to click the map.
    /// </summary>
    [Fact]
    public void GenuinelyLostTracker_WithCachedObservation_StillActs()
    {
        var h = new Harness(_root);

        // Escalate through the strike limit into Lost. A filler self-loop
        // move (U) between each repeat of the same ambiguous "Start" render
        // bumps the tracker's move clock past IsRepeatRedisplayWithoutMove's
        // guard without narrowing or breaking anything the replay depends
        // on (U hops each twin back onto itself).
        RoomObservation start = Obs("Start", Direction.N, Direction.U);
        h.Tracker.NoteRoomObserved(start);                 // Unknown -> Suspect (ambiguous), strikes=0
        h.Tracker.NoteFollowMove(Direction.U);
        h.Tracker.NoteRoomObserved(start);                 // strikes=1
        h.Tracker.NoteFollowMove(Direction.U);
        h.Tracker.NoteRoomObserved(start);                 // strikes=2

        h.Tracker.NoteFollowMove(Direction.U);
        Assert.Equal("Start", h.Tracker.LastAcceptedObservation?.Name);   // still the anchor, not yet reassigned

        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        h.Tracker.NoteRoomObserved(start);                 // strikes=3 -> Lost, CurrentRoom null

        Assert.Equal(RoomConfidence.Lost, h.Tracker.State.Confidence);
        Assert.Null(h.Tracker.State.CurrentRoom);
        // The bug: this used to sit idle waiting for a manual locate. Now a
        // real move went out — the twins split on name via their N exit.
        Assert.NotEmpty(h.Sent);

        relocalizer.Dispose();
    }

    /// <summary>
    /// While an engine is attached, PassiveRelocalizer must never act — the
    /// gate's own tier-3 recovery owns that engine's recovery, and a second
    /// driver acting behind its back would fight it.
    /// </summary>
    [Fact]
    public void StaysInert_WhileAnEngineIsAttached()
    {
        var h = new Harness(_root);
        h.Gate.Attach(new InertEngine());

        h.Tracker.NoteRoomObserved(Obs("Start", Direction.N, Direction.U));
        h.Tracker.NoteFollowMove(Direction.N);
        h.Tracker.NoteFollowMove(Direction.E);

        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        h.Tracker.NoteRoomObserved(Obs("Nowhere", Direction.W));

        Assert.Empty(h.Sent);
        // Had the relocalizer acted, replay converges on C (1/20) exactly
        // like the first test — assert it did NOT, i.e. it stayed inert.
        Assert.Null(h.Tracker.State.CurrentRoom);
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);

        relocalizer.Dispose();
    }

    /// <summary>
    /// Finding 2: OnTrackerStateChanged only checks AttachedEngine once, at
    /// walk start — nothing re-checked it while the walk was pumping landings.
    /// An engine attaching mid-walk must abandon the walk rather than fold a
    /// landing into it: two uncoordinated drivers on the same wire, and the
    /// matcher would misattribute the OTHER engine's landing to this walk's
    /// own last-sent direction.
    /// </summary>
    [Fact]
    public void AnEngineAttachingMidWalk_AbandonsTheWalk()
    {
        var h = new Harness(_root);

        // First observation ever, so LastAcceptedObservation is null while
        // THIS call's own StateChanged is in flight (RoomTracker assigns it
        // only after the call returns) -- construct the relocalizer only
        // after this lands, and trigger Stage 2 off a second mismatch, same
        // as the other tests that need a real seed to work from.
        h.Tracker.NoteRoomObserved(Obs("Start", Direction.N, Direction.U));

        PassiveRelocalizer relocalizer = h.NewRelocalizer(allowWalking: true);

        // No steps to replay -> Stage 2 starts immediately and sends its own
        // splitting move (N: "A" vs "B" diverge by name; U ties, both twins
        // loop back to "Start").
        h.Tracker.NoteRoomObserved(Obs("Nowhere", Direction.W));
        Assert.Single(h.Sent);
        h.Sent.Clear();

        h.Gate.Attach(new InertEngine());

        // The move's own landing arrives — "A", which WOULD converge the walk
        // (only the 1/1 twin's north neighbour is named "A") and confirm the
        // player there if this landing were folded in.
        relocalizer.OnRoomObserved(Obs("A", Direction.E));

        Assert.Empty(h.Sent);
        Assert.Null(h.Tracker.State.CurrentRoom);

        relocalizer.Dispose();
    }
}
