using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ChatHistoryStoreTests
{
    /// <summary>Construct a line with an explicit timestamp so day-rollover tests are deterministic.</summary>
    private static LineExtractor.EmittedLine Line(string text, DateTimeOffset timestamp) =>
        new(text, new CellAttributes[text.Length], timestamp, IsPromptLine: false);

    private static (MessageRouter router, ChatRouter chat, ChatHistoryStore history) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        ChatHistoryStore history = new(chat);
        return (router, chat, history);
    }

    [Fact]
    public void Entries_StreamInChronologicalOrder()
    {
        var (router, _, history) = Setup();
        DateTimeOffset t0 = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        router.Dispatch(Line("Forged gossips: one", t0));
        router.Dispatch(Line("Forged gossips: two", t0.AddSeconds(1)));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal("one", history.Entries[0].Message);
        Assert.Equal("two", history.Entries[1].Message);
    }

    [Fact]
    public void DateRollover_InsertsDaySeparatorBeforeNewDayEntry()
    {
        var (router, _, history) = Setup();
        // Use a multi-day gap with the local timezone offset so the test
        // is independent of where the build runs — comparing local dates
        // means a UTC midnight crossing can collapse to a single local day
        // on some machines and produce a false negative.
        TimeSpan tz = TimeZoneInfo.Local.BaseUtcOffset;
        DateTimeOffset dayOne = new(2026, 1, 2, 12, 0, 0, tz);
        DateTimeOffset dayTwo = new(2026, 1, 3, 12, 0, 0, tz);

        router.Dispatch(Line("Forged gossips: tonight",  dayOne));
        router.Dispatch(Line("Forged gossips: tomorrow", dayTwo));

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal(ChatChannel.Gossip,       history.Entries[0].Channel);
        Assert.Equal(ChatChannel.DaySeparator, history.Entries[1].Channel);
        Assert.Equal("2026-01-03",             history.Entries[1].Message);
        Assert.Equal(ChatChannel.Gossip,       history.Entries[2].Channel);
        Assert.Equal("tomorrow",               history.Entries[2].Message);
    }

    [Fact]
    public void FirstEntry_DoesNotEmitDaySeparator()
    {
        var (router, _, history) = Setup();
        router.Dispatch(Line("Forged gossips: hi", new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));
        Assert.Single(history.Entries);
        Assert.NotEqual(ChatChannel.DaySeparator, history.Entries[0].Channel);
    }

    [Fact]
    public void Clear_WipesAllEntriesAndResetsDayAnchor()
    {
        var (router, _, history) = Setup();
        router.Dispatch(Line("Forged gossips: hi",
            new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));

        history.Clear();
        Assert.Empty(history.Entries);

        // After Clear, the next entry should NOT trigger a separator
        // (the day anchor was reset to default).
        router.Dispatch(Line("Forged gossips: again",
            new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)));
        Assert.Single(history.Entries);
        Assert.NotEqual(ChatChannel.DaySeparator, history.Entries[0].Channel);
    }

    [Fact]
    public async Task ExportAsync_FullHistory_WritesEveryEntry()
    {
        var (router, _, history) = Setup();
        DateTimeOffset t = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        router.Dispatch(Line("Forged gossips: hello",    t));
        router.Dispatch(Line(@"Forged says ""hi""",       t.AddSeconds(1)));

        using MemoryStream stream = new();
        await history.ExportAsync(stream);
        string text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("Gossip Forged: hello", text);
        Assert.Contains("Local Forged: hi",     text);
    }

    [Fact]
    public async Task ExportAsync_WithFilter_OnlyKeepsAllowedChannels()
    {
        var (router, _, history) = Setup();
        DateTimeOffset t = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        router.Dispatch(Line("Forged gossips: keep",   t));
        router.Dispatch(Line(@"Forged says ""drop""",  t.AddSeconds(1)));

        using MemoryStream stream = new();
        await history.ExportAsync(stream, channelFilter: new HashSet<ChatChannel> { ChatChannel.Gossip });
        string text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("keep", text);
        Assert.DoesNotContain("drop", text);
    }
}
