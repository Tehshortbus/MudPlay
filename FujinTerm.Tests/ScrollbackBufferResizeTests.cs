using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ScrollbackBufferResizeTests
{
    private static Cell[] Row(char c) => new[] { new Cell(c, default) };

    [Fact]
    public void SetCapacity_Grow_KeepsAllRowsRoomForMore()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        buf.Append(Row('A'));
        buf.Append(Row('B'));
        buf.Append(Row('C'));

        buf.SetCapacity(10);

        Assert.Equal(10, buf.Capacity);
        Assert.Equal(3,  buf.Count);
        Assert.Equal('A', buf[0].Cells[0].Char);
        Assert.Equal('B', buf[1].Cells[0].Char);
        Assert.Equal('C', buf[2].Cells[0].Char);

        buf.Append(Row('D'));
        Assert.Equal(4, buf.Count);
        Assert.Equal('D', buf[3].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_Shrink_DropsOldestRowsFirst()
    {
        ScrollbackBuffer buf = new(capacity: 5);
        buf.Append(Row('A'));   // oldest
        buf.Append(Row('B'));
        buf.Append(Row('C'));
        buf.Append(Row('D'));
        buf.Append(Row('E'));   // newest

        buf.SetCapacity(3);

        Assert.Equal(3, buf.Capacity);
        Assert.Equal(3, buf.Count);
        // Oldest two (A, B) dropped; newest three remain in order.
        Assert.Equal('C', buf[0].Cells[0].Char);
        Assert.Equal('D', buf[1].Cells[0].Char);
        Assert.Equal('E', buf[2].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_Shrink_BelowCount_ClampsToNewCapacity()
    {
        ScrollbackBuffer buf = new(capacity: 8);
        for (int i = 0; i < 8; i++) buf.Append(Row((char)('A' + i)));   // A..H

        buf.SetCapacity(3);

        Assert.Equal(3, buf.Count);
        Assert.Equal('F', buf[0].Cells[0].Char);
        Assert.Equal('G', buf[1].Cells[0].Char);
        Assert.Equal('H', buf[2].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_AfterWrapAround_PreservesNewestInsertionOrder()
    {
        // Fill past capacity so the ring head has wrapped.
        ScrollbackBuffer buf = new(capacity: 3);
        buf.Append(Row('A'));   // drops on wrap
        buf.Append(Row('B'));   // drops on wrap
        buf.Append(Row('C'));   // drops on wrap
        buf.Append(Row('D'));
        buf.Append(Row('E'));
        buf.Append(Row('F'));
        // Live rows are now D, E, F in oldest→newest order.

        buf.SetCapacity(5);

        Assert.Equal(5, buf.Capacity);
        Assert.Equal(3, buf.Count);
        Assert.Equal('D', buf[0].Cells[0].Char);
        Assert.Equal('E', buf[1].Cells[0].Char);
        Assert.Equal('F', buf[2].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_AfterResize_AppendsStillWork()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        buf.Append(Row('A'));
        buf.Append(Row('B'));

        buf.SetCapacity(2);   // shrink — A drops, B remains.

        buf.Append(Row('C'));
        buf.Append(Row('D'));   // B drops on wrap.

        Assert.Equal(2, buf.Count);
        Assert.Equal('C', buf[0].Cells[0].Char);
        Assert.Equal('D', buf[1].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_SameValue_NoOp()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        buf.Append(Row('A'));

        int events = 0;
        buf.CapacityChanged += () => events++;

        buf.SetCapacity(4);

        Assert.Equal(4, buf.Capacity);
        Assert.Equal(1, buf.Count);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SetCapacity_FiresCapacityChanged()
    {
        ScrollbackBuffer buf = new(capacity: 4);

        int events = 0;
        buf.CapacityChanged += () => events++;

        buf.SetCapacity(8);
        Assert.Equal(1, events);
    }

    [Fact]
    public void SetCapacity_ZeroOrNegative_Throws()
    {
        ScrollbackBuffer buf = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.SetCapacity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.SetCapacity(-5));
    }
}
