using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// An event that takes over from an Auto-Lair run puts the run's marks back when it
// is done. They carry the game data they were marked on and go back only while
// that set is the active one; marks read from a saved setup are the active set's.
public sealed class EventLairMarksGameDataTests : IDisposable
{
    private const string TestLoopSet = "test-event-lairmarks";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-event-lairmarks-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        try { Directory.Delete(AppPaths.GameDataSetDir(TestLoopSet), recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void TheSnapshotOfARun_CarriesTheGameDataItsMarksAreOf()
    {
        using Harness h = Harness.Create(_root, TestLoopSet);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        h.AutoLair.Mark(new RoomKey(1, 2));
        h.AutoLair.Mark(new RoomKey(1, 3));
        Assert.True(h.AutoLair.Start());

        EventManager.EventResumePlan.AutoLair plan =
            Assert.IsType<EventManager.EventResumePlan.AutoLair>(h.Events.SnapshotCurrentActivity());

        Assert.True(plan.OffARun);
        Assert.Equal("alpha", plan.MarkedOn);
    }

    [Fact]
    public void ResumingARunOnItsOwnGameData_PutsItsMarksBackAndStartsIt()
    {
        using Harness h = Harness.Create(_root, TestLoopSet);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Dictionary<RoomKey, int?> markers = new() { [new RoomKey(1, 2)] = null, [new RoomKey(1, 3)] = 120 };

        EventManager.EngineStart started = h.Events.ExecuteResume(
            new EventManager.EventResumePlan.AutoLair(markers, OffARun: true, MarkedOn: "ALPHA"));

        Assert.Equal(EventManager.EngineStart.Started, started);
        Assert.True(h.AutoLair.IsActive);
        Assert.Equal(2, h.AutoLair.Marked.Count);
        Assert.Equal(120, h.AutoLair.GetOverride(new RoomKey(1, 3)));
    }

    // The marks are another game data's by the time the event would put them back:
    // nothing is put back and Auto-Lair is not started on another set's rooms.
    [Fact]
    public void ResumingARunUnderAnotherGameData_PutsNothingBackAndStartsNothing()
    {
        using Harness h = Harness.Create(_root, TestLoopSet);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Dictionary<RoomKey, int?> markers = new() { [new RoomKey(1, 2)] = null, [new RoomKey(1, 3)] = 120 };

        EventManager.EngineStart started = h.Events.ExecuteResume(
            new EventManager.EventResumePlan.AutoLair(markers, OffARun: true, MarkedOn: "elsewhere"));

        Assert.Equal(EventManager.EngineStart.Refused, started);
        Assert.False(h.AutoLair.IsActive);
        Assert.Empty(h.AutoLair.Marked);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void StartingASetupsMarks_StampsTheActiveGameData()
    {
        using Harness h = Harness.Create(_root, TestLoopSet);
        h.Tracker.SetLocated(new RoomKey(1, 1));
        Dictionary<RoomKey, int?> markers = new() { [new RoomKey(1, 2)] = null, [new RoomKey(1, 3)] = null };

        h.Events.ExecuteResume(new EventManager.EventResumePlan.AutoLair(markers));

        Assert.True(h.AutoLair.IsActive);
        Assert.Equal("alpha", h.AutoLair.MarksGameData);
    }
}
