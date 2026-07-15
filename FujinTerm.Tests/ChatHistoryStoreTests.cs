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
    public void Seed_InsertsHistoricalEntriesAtFrontInOrder()
    {
        var (router, _, history) = Setup();
        TimeSpan tz = TimeZoneInfo.Local.BaseUtcOffset;
        // Live entry already in the store; seeded history should land ahead of it.
        router.Dispatch(Line("Forged gossips: live", new DateTimeOffset(2026, 1, 5, 12, 0, 0, tz)));

        ChatLogEntry old1 = new(new DateTimeOffset(2026, 1, 4, 9, 0, 0, tz),
            ChatChannel.Gossip, "Forged", "old one", "old one");
        ChatLogEntry old2 = new(new DateTimeOffset(2026, 1, 4, 9, 1, 0, tz),
            ChatChannel.Local, "Bob", "old two", "old two");
        history.Seed(new[] { old1, old2 });

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal("old one", history.Entries[0].Message);
        Assert.Equal("old two", history.Entries[1].Message);
        Assert.Equal("live",    history.Entries[2].Message);
    }

    [Fact]
    public void Seed_AnchorsDayClock_SoNextLiveEntryDrawsSeparatorOnRollover()
    {
        var (router, _, history) = Setup();
        TimeSpan tz = TimeZoneInfo.Local.BaseUtcOffset;

        history.Seed(new[]
        {
            new ChatLogEntry(new DateTimeOffset(2026, 1, 4, 9, 0, 0, tz),
                ChatChannel.Gossip, "Forged", "yesterday", "yesterday"),
        });

        // A live entry on the next day must insert a separator after the seed.
        router.Dispatch(Line("Forged gossips: today", new DateTimeOffset(2026, 1, 5, 9, 0, 0, tz)));

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal("yesterday",              history.Entries[0].Message);
        Assert.Equal(ChatChannel.DaySeparator, history.Entries[1].Channel);
        Assert.Equal("today",                  history.Entries[2].Message);
    }

    [Fact]
    public void Seed_Empty_IsNoOp()
    {
        var (_, _, history) = Setup();
        history.Seed(Array.Empty<ChatLogEntry>());
        Assert.Empty(history.Entries);
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
}
