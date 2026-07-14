using FujinTerm.Game;
using FujinTerm.Game.Cash;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins the write-then-reload contract SessionLogService relies on to restore
// prior-session history on reconnect: every line FormatChatLine / FormatTxnLine
// writes to disk must parse back into an equivalent entry. The tricky cases are
// a colon inside the message body, a channel with no speaker, and a stash
// detail with vs. without a trailing location.
public sealed class SessionLogRoundTripTests
{
    private static DateTimeOffset Local(int h, int m, int s) =>
        new(new DateTime(2026, 3, 4, h, m, s, DateTimeKind.Local));

    private static string Stamp(DateTimeOffset t) =>
        t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    // ----- chat --------------------------------------------------------

    [Fact]
    public void Chat_WithSpeaker_RoundTrips()
    {
        ChatLogEntry src = new(Local(13, 45, 7), ChatChannel.Gossip, "Forged", "hello there", "hello there");
        Assert.True(SessionLogService.TryParseChatLine(SessionLogService.FormatChatLine(src), out ChatLogEntry back));

        Assert.Equal(ChatChannel.Gossip, back.Channel);
        Assert.Equal("Forged", back.Speaker);
        Assert.Equal("hello there", back.Message);
        Assert.Equal(Stamp(src.Timestamp), Stamp(back.Timestamp));
    }

    [Fact]
    public void Chat_WithoutSpeaker_RoundTrips()
    {
        ChatLogEntry src = new(Local(1, 2, 3), ChatChannel.Local, null, "a room mutter", "a room mutter");
        Assert.True(SessionLogService.TryParseChatLine(SessionLogService.FormatChatLine(src), out ChatLogEntry back));

        Assert.Equal(ChatChannel.Local, back.Channel);
        Assert.Null(back.Speaker);
        Assert.Equal("a room mutter", back.Message);
    }

    [Fact]
    public void Chat_MessageWithColon_RoundTrips()
    {
        // The parser splits on the FIRST ": " so a colon in the body must survive.
        ChatLogEntry src = new(Local(9, 0, 0), ChatChannel.Gossip, "Bob", "note: watch the gate", "note: watch the gate");
        Assert.True(SessionLogService.TryParseChatLine(SessionLogService.FormatChatLine(src), out ChatLogEntry back));

        Assert.Equal("Bob", back.Speaker);
        Assert.Equal("note: watch the gate", back.Message);
    }

    [Fact]
    public void Chat_EmptyMessage_RoundTrips()
    {
        ChatLogEntry src = new(Local(9, 0, 0), ChatChannel.Yell, "Bob", string.Empty, string.Empty);
        Assert.True(SessionLogService.TryParseChatLine(SessionLogService.FormatChatLine(src), out ChatLogEntry back));

        Assert.Equal(ChatChannel.Yell, back.Channel);
        Assert.Equal("Bob", back.Speaker);
        Assert.Equal(string.Empty, back.Message);
    }

    [Fact]
    public void Chat_LegacyTimeOnlyLine_StillParses()
    {
        // Logs written before the date was added carry only "[HH:mm:ss]".
        Assert.True(SessionLogService.TryParseChatLine("[14:05:09] Gossip Forged: legacy", out ChatLogEntry back));
        Assert.Equal(ChatChannel.Gossip, back.Channel);
        Assert.Equal("Forged", back.Speaker);
        Assert.Equal("legacy", back.Message);
    }

    [Fact]
    public void Chat_GarbageLine_ReturnsFalse()
    {
        Assert.False(SessionLogService.TryParseChatLine("not a log line", out _));
        Assert.False(SessionLogService.TryParseChatLine("[13:45:07] NoColonHere", out _));
        Assert.False(SessionLogService.TryParseChatLine("[13:45:07] Bogus channel: x", out _));
    }

    // ----- transactions ------------------------------------------------

    [Fact]
    public void Txn_Deposit_NoLocation_RoundTrips()
    {
        TransactionEntry src = new(Local(10, 0, 0), TransactionKind.Bank, "Deposited 12,300 wealth", null);
        Assert.True(SessionLogService.TryParseTxnLine(SessionLogService.FormatTxnLine(src), out TransactionEntry back));

        Assert.Equal(TransactionKind.Bank, back.Kind);
        Assert.Equal("Deposited 12,300 wealth", back.Detail);
        Assert.Null(back.Location);
        Assert.Equal(Stamp(src.Time), Stamp(back.Time));
    }

    [Fact]
    public void Txn_Stash_WithLocation_RoundTrips()
    {
        TransactionEntry src = new(Local(10, 5, 0), TransactionKind.Stash, "Hid a torch ×3, 400 gold", "Hollow Stump (3/7)");
        Assert.True(SessionLogService.TryParseTxnLine(SessionLogService.FormatTxnLine(src), out TransactionEntry back));

        Assert.Equal(TransactionKind.Stash, back.Kind);
        Assert.Equal("Hid a torch ×3, 400 gold", back.Detail);
        Assert.Equal("Hollow Stump (3/7)", back.Location);
    }

    [Fact]
    public void Txn_GarbageLine_ReturnsFalse()
    {
        Assert.False(SessionLogService.TryParseTxnLine("[10:00:00] Bogus something", out _));
        Assert.False(SessionLogService.TryParseTxnLine("no brackets", out _));
    }
}
