using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class ScrollbackBufferResizeTests
{
    private static Cell[] Row(char c) => new[] { new Cell(c, default) };

    [Fact]
    public void SetCapacity_Grow_KeepsAllRowsRoomForMore()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        buf.Append(Row('A'), DateTimeOffset.Now);
        buf.Append(Row('B'), DateTimeOffset.Now);
        buf.Append(Row('C'), DateTimeOffset.Now);

        buf.SetCapacity(10);

        Assert.Equal(10, buf.Capacity);
        Assert.Equal(3,  buf.Count);
        Assert.Equal('A', buf[0].Cells[0].Char);
        Assert.Equal('B', buf[1].Cells[0].Char);
        Assert.Equal('C', buf[2].Cells[0].Char);

        buf.Append(Row('D'), DateTimeOffset.Now);
        Assert.Equal(4, buf.Count);
        Assert.Equal('D', buf[3].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_Shrink_DropsOldestRowsFirst()
    {
        ScrollbackBuffer buf = new(capacity: 5);
        buf.Append(Row('A'), DateTimeOffset.Now);   // oldest
        buf.Append(Row('B'), DateTimeOffset.Now);
        buf.Append(Row('C'), DateTimeOffset.Now);
        buf.Append(Row('D'), DateTimeOffset.Now);
        buf.Append(Row('E'), DateTimeOffset.Now);   // newest

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
        for (int i = 0; i < 8; i++) buf.Append(Row((char)('A' + i)), DateTimeOffset.Now);   // A..H

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
        buf.Append(Row('A'), DateTimeOffset.Now);   // drops on wrap
        buf.Append(Row('B'), DateTimeOffset.Now);   // drops on wrap
        buf.Append(Row('C'), DateTimeOffset.Now);   // drops on wrap
        buf.Append(Row('D'), DateTimeOffset.Now);
        buf.Append(Row('E'), DateTimeOffset.Now);
        buf.Append(Row('F'), DateTimeOffset.Now);
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
        buf.Append(Row('A'), DateTimeOffset.Now);
        buf.Append(Row('B'), DateTimeOffset.Now);

        buf.SetCapacity(2);   // shrink — A drops, B remains.

        buf.Append(Row('C'), DateTimeOffset.Now);
        buf.Append(Row('D'), DateTimeOffset.Now);   // B drops on wrap.

        Assert.Equal(2, buf.Count);
        Assert.Equal('C', buf[0].Cells[0].Char);
        Assert.Equal('D', buf[1].Cells[0].Char);
    }

    [Fact]
    public void SetCapacity_SameValue_NoOp()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        buf.Append(Row('A'), DateTimeOffset.Now);

        buf.SetCapacity(4);

        Assert.Equal(4, buf.Capacity);
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void SetCapacity_ZeroOrNegative_Throws()
    {
        ScrollbackBuffer buf = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.SetCapacity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.SetCapacity(-5));
    }

    // A deep scrollback's memory goes on rows: a row is kept only as far as its last
    // cell that isn't a plain blank, so a coloured bar still reaches the margin.
    [Fact]
    public void Append_KeepsARowOnlyAsFarAsItsLastCellThatIsntAPlainBlank()
    {
        ScrollbackBuffer buf = new(capacity: 4);
        Cell[] text = new Cell[80];
        Array.Fill(text, Cell.Blank);
        text[0] = new Cell('h', CellAttributes.Default);
        text[1] = new Cell('i', CellAttributes.Default);
        Cell[] bar = new Cell[80];
        Array.Fill(bar, new Cell(' ', CellAttributes.Default.WithBackground(TerminalColor.Indexed(4))));
        Cell[] empty = new Cell[80];
        Array.Fill(empty, Cell.Blank);

        buf.Append(text, DateTimeOffset.Now);
        buf.Append(bar, DateTimeOffset.Now);
        buf.Append(empty, DateTimeOffset.Now);

        Assert.Equal("hi", new string(buf[0].Cells.Select(c => c.Char).ToArray()));
        Assert.Equal(80, buf[1].Cells.Length);
        Assert.Empty(buf[2].Cells);
    }

    // The ring's array grows as rows arrive (a million-row setting costs nothing
    // until it fills) and, once full, drops the oldest rows in order.
    [Fact]
    public void Append_PastEveryGrowthStep_KeepsTheNewestRowsInOrder()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = new ScrollbackBuffer(capacity: 1_000_000);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);

        ScrollbackBuffer buf = new(capacity: 3_000);
        for (int i = 0; i < 5_000; i++)
            buf.Append(new[] { new Cell((char)('0' + i % 10), CellAttributes.Default) }, DateTimeOffset.Now.AddTicks(i));

        Assert.Equal(3_000, buf.Count);
        Assert.Equal(Enumerable.Range(2_000, 3_000).Select(i => (char)('0' + i % 10)),
            buf.Enumerate().Select(r => r.Cells[0].Char));

        buf.SetCapacity(1_000_000);
        buf.Append(new[] { new Cell('x', CellAttributes.Default) }, DateTimeOffset.Now);
        Assert.Equal(3_001, buf.Count);
        Assert.Equal('0', buf[0].Cells[0].Char);
        Assert.Equal('x', buf[3_000].Cells[0].Char);
    }
}
