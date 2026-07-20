using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Drives the move-free recovery look-sweep inline (no wall-clock timer) to
/// pin its sequencing: one `look <dir>` per exit in enum order, each sent only
/// after the previous neighbour renders, a timed-out arm skipped, and the
/// collected neighbours handed to the completion callback.
/// </summary>
public sealed class RecoveryLookSweepTests
{
    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    private static RecoveryLookSweep NewSweep(out List<string> sent)
    {
        var captured = new List<string>();
        sent = captured;
        var sweep = new RecoveryLookSweep(log: null, useTimer: false);
        sweep.SetWireSender(b => captured.Add(Encoding.Latin1.GetString(b)));
        return sweep;
    }

    [Fact]
    public void Begin_peeks_each_exit_in_order_and_collects_neighbours()
    {
        RecoveryLookSweep sweep = NewSweep(out List<string> sent);
        IReadOnlyDictionary<Direction, RoomObservation>? result = null;

        bool started = sweep.Begin(
            new HashSet<Direction> { Direction.E, Direction.N },   // insertion order shouldn't matter
            r => result = r);

        Assert.True(started);
        Assert.True(sweep.Active);
        Assert.Equal("look north\r", sent[0]);                     // N before E (enum order)

        sweep.OnRoomObserved(Obs("Northern Glade", Direction.S));
        Assert.Equal("look east\r", sent[1]);
        Assert.Null(result);                                        // not done until every exit peeked

        sweep.OnRoomObserved(Obs("Eastern Path", Direction.W));

        Assert.NotNull(result);
        Assert.False(sweep.Active);
        Assert.Equal(2, result!.Count);
        Assert.Equal("Northern Glade", result[Direction.N].Name);
        Assert.Equal("Eastern Path", result[Direction.E].Name);
    }

    [Fact]
    public void Begin_returns_false_without_a_wire_sender()
    {
        var sweep = new RecoveryLookSweep(log: null, useTimer: false);

        bool started = sweep.Begin(new HashSet<Direction> { Direction.N }, _ => { });

        Assert.False(started);
        Assert.False(sweep.Active);
    }

    [Fact]
    public void Begin_returns_false_when_room_has_no_exits()
    {
        RecoveryLookSweep sweep = NewSweep(out _);

        bool started = sweep.Begin(new HashSet<Direction>(), _ => { });

        Assert.False(started);
    }

    [Fact]
    public void Timed_out_arm_is_skipped_and_the_sweep_continues()
    {
        RecoveryLookSweep sweep = NewSweep(out List<string> sent);
        IReadOnlyDictionary<Direction, RoomObservation>? result = null;

        sweep.Begin(new HashSet<Direction> { Direction.N, Direction.E }, r => result = r);
        Assert.Equal("look north\r", sent[0]);

        // North never renders — its per-look timeout fires, skipping the arm.
        sweep.FireLookTimeoutForTests();
        Assert.Equal("look east\r", sent[1]);

        sweep.OnRoomObserved(Obs("Eastern Path", Direction.W));

        Assert.NotNull(result);
        Assert.Single(result!);                                     // only E resolved; N skipped
        Assert.True(result!.ContainsKey(Direction.E));
        Assert.False(result!.ContainsKey(Direction.N));
    }

    [Fact]
    public void Cancel_drops_the_sweep_without_invoking_the_callback()
    {
        RecoveryLookSweep sweep = NewSweep(out _);
        bool fired = false;

        sweep.Begin(new HashSet<Direction> { Direction.N, Direction.E }, _ => fired = true);
        sweep.Cancel();

        Assert.False(sweep.Active);
        // A late render after cancel is ignored — no callback, no throw.
        sweep.OnRoomObserved(Obs("Stray", Direction.S));
        Assert.False(fired);
    }
}
