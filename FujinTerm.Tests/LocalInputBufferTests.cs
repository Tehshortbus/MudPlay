using System.Text;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the client-side line buffer contract: 254-char cap, drop-on-
/// overflow, Latin-1 + CR flush, and the Changed event fires on every
/// mutation so the TerminalControl overlay can repaint.
/// </summary>
public sealed class LocalInputBufferTests
{
    [Fact]
    public void Append_AccumulatesText()
    {
        LocalInputBuffer b = new();
        b.Append("hello");
        b.Append(" ");
        b.Append("world");
        Assert.Equal("hello world", b.Text);
        Assert.Equal(11, b.Length);
        Assert.False(b.IsFull);
    }

    [Fact]
    public void Append_ClampsToMaxLength_AndDropsOverflow()
    {
        // Cap = 254 (MajorMUD wire-level limit). Past 254, further
        // chars are silently dropped — the overlay's caret colour
        // shifts to OrangeRed so the user sees the buffer is full,
        // but no exception bubbles up.
        LocalInputBuffer b = new();
        b.Append(new string('a', 200));
        Assert.False(b.IsFull);
        int taken = b.Append(new string('b', 100));
        Assert.Equal(54, taken);   // only 54 more fit (254 - 200)
        Assert.True(b.IsFull);
        Assert.Equal(254, b.Length);

        // Subsequent appends are full no-ops.
        Assert.Equal(0, b.Append("more"));
        Assert.Equal(254, b.Length);
    }

    [Fact]
    public void Append_EmptyOrNull_NoOp()
    {
        LocalInputBuffer b = new();
        Assert.Equal(0, b.Append(string.Empty));
        Assert.Equal(0, b.Append(null!));
        Assert.Equal(0, b.Length);
    }

    [Fact]
    public void Backspace_RemovesLastChar()
    {
        LocalInputBuffer b = new();
        b.Append("hello");
        Assert.True(b.Backspace());
        Assert.Equal("hell", b.Text);
    }

    [Fact]
    public void Backspace_OnEmpty_ReturnsFalse_NoEvent()
    {
        // Per user direction: backspace just erases the buffer. When
        // the buffer is empty, the press is a no-op — no \b byte goes
        // to the wire. Caller (TerminalControl) consumes the event
        // regardless so server-echoed text can't be backed over.
        LocalInputBuffer b = new();
        int events = 0;
        b.Changed += () => events++;
        Assert.False(b.Backspace());
        Assert.Equal(0, events);
    }

    [Fact]
    public void FlushBytes_ReturnsLatin1PlusCr_AndClears()
    {
        LocalInputBuffer b = new();
        b.Append("look here");
        byte[] bytes = b.FlushBytes();
        Assert.Equal("look here\r", Encoding.Latin1.GetString(bytes));
        Assert.Equal(string.Empty, b.Text);
    }

    [Fact]
    public void FlushBytes_OnEmpty_ReturnsLoneCr()
    {
        // Pressing Enter on a blank line is itself a meaningful command
        // at most MUD prompts (refresh / no-op move on); we send the
        // CR so the server gets the keystroke even with no payload.
        LocalInputBuffer b = new();
        byte[] bytes = b.FlushBytes();
        Assert.Single(bytes);
        Assert.Equal(0x0D, bytes[0]);
    }

    [Fact]
    public void Clear_DropsBuffer_WithoutWireSide()
    {
        // Clear is for connection-swap / test housekeeping — Enter
        // uses FlushBytes which produces the wire payload.
        LocalInputBuffer b = new();
        b.Append("abc");
        b.Clear();
        Assert.Equal(string.Empty, b.Text);
    }

    [Fact]
    public void Set_ReplacesWholeBuffer()
    {
        // Recall swaps the live line for a previously-sent command wholesale.
        LocalInputBuffer b = new();
        b.Append("half-typed");
        b.Set("north");
        Assert.Equal("north", b.Text);
    }

    [Fact]
    public void Set_ClampsToMaxLength()
    {
        LocalInputBuffer b = new();
        b.Set(new string('z', LocalInputBuffer.MaxLength + 50));
        Assert.Equal(LocalInputBuffer.MaxLength, b.Length);
    }

    [Fact]
    public void Set_EmptyOrNull_ClearsAndFiresChanged()
    {
        // Down past the newest entry recalls an empty line — the overlay
        // must repaint to erase what was shown, so Changed fires even when
        // the result is empty.
        LocalInputBuffer b = new();
        b.Append("stale");
        int events = 0;
        b.Changed += () => events++;
        b.Set(null);
        Assert.Equal(string.Empty, b.Text);
        Assert.Equal(1, events);
    }

    [Fact]
    public void Changed_Fires_OnEveryMutation()
    {
        LocalInputBuffer b = new();
        int events = 0;
        b.Changed += () => events++;
        b.Append("a");          // +1
        b.Append("b");          // +1
        b.Backspace();          // +1
        b.FlushBytes();         // +1 (buffer was non-empty → fires Clear branch)
        // Empty flush: Changed only fires when state actually changed.
        // The empty-CR fast-path doesn't mutate _buf so no event.
        b.FlushBytes();
        Assert.Equal(4, events);
    }
}
