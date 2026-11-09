using System.Text.Json;
using MudPlay.Services;
using MudPlay.Services.Api;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// GET /scrollback?since=: the game's lines numbered as they complete, so a program
// polling for what the game printed gets each line once, however far the screen
// scrolled in between (issue #1198).
public sealed class LocalApiScrollbackSinceTests
{
    private const string Password = "Pa55w0rd!x";
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static BugReportScrub.Scrubber Scrubber() =>
        BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, Password)], ["Tester"], compiled: false);

    private static (ServerFeed Feed, LocalApiLines Lines) Attached()
    {
        ServerFeed feed = new(clock: () => T0);
        LocalApiLines lines = new();
        lines.AttachLineExtractor(feed.Lines);
        return (feed, lines);
    }

    private static JsonElement Poll(LocalApiLines lines, long since, int limit = 750, CaptureWindow? only = null) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            LocalApiState.ScrollbackSince(lines, only ?? new CaptureWindow(on: true), Scrubber(), since, limit),
            LocalApiJson.Options)).RootElement;

    private static string[] Texts(JsonElement page) =>
        page.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();

    [Fact]
    public void Polling_HandsEachLineOutOnce_AcrossAScroll()
    {
        (ServerFeed feed, LocalApiLines lines) = Attached();
        for (int i = 1; i <= 30; i++) feed.Line($"line {i}");

        JsonElement first = Poll(lines, since: 0);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"line {i}"), Texts(first));
        long cursor = first.GetProperty("nextSince").GetInt64();

        // The screen scrolls on: a diff of the screen tail would lose or double rows.
        feed.Line("Raijin telepaths: are you there?");
        feed.Line("A long line " + new string('x', 90) + " that the terminal wrapped.");
        feed.Prompt();

        JsonElement next = Poll(lines, cursor);
        Assert.Equal(new[]
        {
            "Raijin telepaths: are you there?",
            "A long line " + new string('x', 90) + " that the terminal wrapped.",
            "[HP=100/MA=50]:",
        }, Texts(next));
        JsonElement[] rows = next.GetProperty("lines").EnumerateArray().ToArray();
        Assert.True(rows[0].GetProperty("chat").GetBoolean());
        Assert.False(rows[1].GetProperty("chat").GetBoolean());
        Assert.Equal(cursor + 1, rows[0].GetProperty("seq").GetInt64());

        Assert.Empty(Texts(Poll(lines, next.GetProperty("nextSince").GetInt64())));
    }

    [Fact]
    public void AShortPage_StopsTheCursorAtItsLastLine_AndLostLinesAreCounted()
    {
        (ServerFeed feed, LocalApiLines lines) = Attached();
        for (int i = 1; i <= LocalApiLines.Capacity + 10; i++) feed.Line($"line {i}");

        JsonElement page = Poll(lines, since: 0, limit: 5);
        Assert.Equal(10, page.GetProperty("lost").GetInt64());
        Assert.Equal(new[] { "line 11", "line 12", "line 13", "line 14", "line 15" }, Texts(page));
        Assert.Equal(15, page.GetProperty("nextSince").GetInt64());

        Assert.Equal("line 16", Texts(Poll(lines, 15, limit: 1)).Single());
    }

    [Fact]
    public void LinesOutsideTheGame_AreSkippedButPassed_AndAccountDetailsMasked()
    {
        (ServerFeed feed, LocalApiLines lines) = Attached();
        CaptureWindow inGame = new(on: false);
        feed.Line("Enter your user-ID: handtyped", T0);
        inGame.Set(true, T0.AddSeconds(5));
        feed.Line($"You say \"{Password}\"", T0.AddSeconds(6));

        JsonElement page = Poll(lines, since: 0, only: inGame);
        Assert.Equal(new[] { "You say \"[redacted]\"" }, Texts(page));
        Assert.Equal(2, page.GetProperty("nextSince").GetInt64());
    }

    [Fact]
    public void ACursorFromBeforeARestart_GetsEverythingKept()
    {
        (ServerFeed feed, LocalApiLines lines) = Attached();
        feed.Line("Town Square");

        JsonElement page = Poll(lines, since: 900);
        Assert.True(page.GetProperty("restarted").GetBoolean());
        Assert.Equal(new[] { "Town Square" }, Texts(page));
        Assert.Equal(1, page.GetProperty("nextSince").GetInt64());
    }
}
