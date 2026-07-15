using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the blanket train-stats automation lockout: while the trainer / creation
/// form owns the keyboard, <see cref="TrainerScreenGate"/> raises an
/// <see cref="EngineSendGate"/> hold that no-ops every wrapped engine — even on a
/// cursor-positioned realm whose "Point Cost Chart" marker never confirms — and
/// releases it when the form closes. A raw (un-wrapped) sender still pierces the
/// hold, mirroring how the auto-trainer's CP replay + user input stay live.
/// </summary>
public sealed class TrainerScreenGateTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    private static (TrainerMenuTracker tracker, TrainerScreenGate gate, EngineSendGate send, MessageRouter router) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState state = new();
        TrainerMenuTracker tracker = new(router, state) { NowProvider = () => Now };
        EngineSendGate send = new();
        TrainerScreenGate gate = new(tracker, send);
        return (tracker, gate, send, router);
    }

    private static void Dispatch(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            Text: text,
            Attributes: Array.Empty<CellAttributes>(),
            Timestamp: Now,
            IsPromptLine: false));

    [Fact]
    public void TrainStats_WithoutMarker_HoldsTheGate()
    {
        // The Paradigm case: the cursor-positioned stat box never emits its
        // "Point Cost Chart" marker inline, so IsInTrainerMenu stays false — but
        // the form still owns the keyboard, so the send gate must lock anyway.
        var (tracker, gate, send, _) = Setup();
        Assert.False(send.IsLocked);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));

        Assert.True(gate.IsHeld);
        Assert.True(send.IsLocked);
        Assert.False(tracker.IsInTrainerMenu); // marker never confirmed
    }

    [Fact]
    public void WrappedEngineSend_NoOpsWhileHeld_ResumesAfter()
    {
        var (tracker, _, send, router) = Setup();
        List<byte[]> wire = new();
        Action<byte[]> engine = send.WrapEngineSender(wire.Add);

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        engine(Encoding.Latin1.GetBytes("par\r")); // would leak into Family Name
        Assert.Empty(wire);

        // Exit the form: command echo swallowed, next prompt is the real exit.
        Dispatch(router, "[HP=33]:");
        Dispatch(router, "[HP=33]:");

        engine(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(wire);
    }

    [Fact]
    public void RawSender_PiercesHold()
    {
        // The auto-trainer's CP replay + the user's manual input ride the raw
        // sender, not the wrapped one, so they must still send while held.
        var (tracker, _, send, _) = Setup();
        List<byte[]> wire = new();
        Action<byte[]> raw = wire.Add; // un-wrapped, like SendUserInput

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        raw(Encoding.Latin1.GetBytes("1\r"));
        Assert.Single(wire);
    }

    [Fact]
    public void MarkerConfirmedMenu_HoldsTheGate()
    {
        // Character creation / stock realm: confirmed via the marker with no
        // command-armed input session. MenuOwnsKeyboard folds it in, so the hold
        // must rise on this path too.
        var (tracker, gate, send, router) = Setup();
        Dispatch(router, "  MAJOR MUD Character Creation      Point Cost Chart");

        Assert.True(tracker.IsInTrainerMenu);
        Assert.True(gate.IsHeld);
        Assert.True(send.IsLocked);
    }

    [Fact]
    public void FormExit_ReleasesTheGate()
    {
        var (tracker, gate, send, router) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        Dispatch(router, "[HP=33]:"); // command echo — swallowed
        Dispatch(router, "[HP=33]:"); // exit prompt — form left

        Assert.False(gate.IsHeld);
        Assert.False(send.IsLocked);
    }

    [Fact]
    public void ComposesWithOtherHolds_StaysLockedUntilBothClear()
    {
        // The trainer hold is named, so it stacks with (say) the suicide-password
        // hold rather than clobbering it — sends stay gated until every hold lifts.
        var (tracker, _, send, router) = Setup();
        send.Hold("password");

        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Dispatch(router, "[HP=33]:");
        Dispatch(router, "[HP=33]:"); // trainer hold released here

        Assert.True(send.IsLocked); // password hold still up
        send.Release("password");
        Assert.False(send.IsLocked);
    }

    [Fact]
    public void Dispose_ReleasesStuckHold()
    {
        var (tracker, gate, send, _) = Setup();
        tracker.ObserveOutbound(Encoding.Latin1.GetBytes("train stats\r"));
        Assert.True(send.IsLocked);

        gate.Dispose();
        Assert.False(send.IsLocked);
    }
}
