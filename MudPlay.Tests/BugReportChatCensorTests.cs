using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The report-only chat censor. The words here are made-up stand-ins added to an
// empty table; no real word from the shipped table appears in a test.
public sealed class BugReportChatCensorTests
{
    private static readonly BugReportChatCensor Censor =
        StandInCensor.Of("flibber", "zorptang");

    [Fact]
    public void Normalise_UndoesSubstitutionsPunctuationAndStretching()
    {
        Assert.Equal("flibber", BugReportChatCensor.Normalise("Fl1bb3r", collapseAll: false));
        Assert.Equal("flibber", BugReportChatCensor.Normalise("f.l-i_b*b e r", collapseAll: false));
        Assert.Equal("flibber", BugReportChatCensor.Normalise("flibbbbber", collapseAll: false));
        Assert.Equal("fliber", BugReportChatCensor.Normalise("flibbbber", collapseAll: true));
        Assert.Equal("as", BugReportChatCensor.Normalise("@$$", collapseAll: true));
    }

    [Fact]
    public void Hash_MatchesTheValuesTheTableWasGeneratedWith()
    {
        // Pinned against the generator, so a change to the salt or the hashing
        // can't silently empty the shipped table.
        Assert.Equal(0x054AC24ECD77F705UL, BugReportChatCensor.Hash("flibber", collapsedAll: false));
        Assert.Equal(0x70B5073F83F55115UL, BugReportChatCensor.Hash("fliber", collapsedAll: true));
        Assert.Equal(0xD4ED537F83FE2D9CUL, BugReportChatCensor.Hash("zorptang", collapsedAll: false));
    }

    [Theory]
    [InlineData("flibber")]
    [InlineData("FLIBBER")]
    [InlineData("fl1bb3r")]
    [InlineData("fliiiibber")]
    [InlineData("zorptang")]
    public void IsListed_TakesSpellingsOfAListedWord(string token) => Assert.True(Censor.IsListed(token));

    [Theory]
    [InlineData("flibberty")]      // contains a listed word: not a substring match
    [InlineData("unflibber")]
    [InlineData("fliber")]         // one b: a different word unless it was stretched
    [InlineData("zorp")]
    [InlineData("sword")]
    public void IsListed_LeavesOtherWords(string token) => Assert.False(Censor.IsListed(token));

    [Fact]
    public void CensorIfChat_MasksChatAndKeepsPunctuation()
    {
        Assert.Equal("Somebody gossips: you [censored]!",
            Censor.CensorIfChat("Somebody gossips: you flibber!"));
        Assert.Equal("Somebody telepaths: [censored], [censored]-head.",
            Censor.CensorIfChat("Somebody telepaths: Zorptang, flibber-head."));
        Assert.Equal("You say \"[censored]\"", Censor.CensorIfChat("You say \"FL1BB3R\""));
    }

    [Fact]
    public void CensorIfChat_NeverTouchesGameText()
    {
        // The same word in a room, item or combat line is the game's own.
        const string room = "You notice a flibber sword here.";
        const string combat = "The zorptang swings at you with a flibber!";
        Assert.Equal(room, Censor.CensorIfChat(room));
        Assert.Equal(combat, Censor.CensorIfChat(combat));
    }

    // A chat line the terminal wrapped is masked on the row it starts on and on
    // the row it carries on to; the game's rows either side are left.
    [Fact]
    public void CensorRows_AWrappedChatLine_IsMaskedOnBothItsRows()
    {
        List<string> rows =
        [
            "You notice a flibber here.",
            "Somebody gossips: zorptang " + new string('x', 53),   // all 80 columns
            "and then flibber to finish",
            "Obvious exits: flibber",
        ];

        Censor.CensorRows(rows, wrappedOntoNext: [false, true, false, false]);

        Assert.Equal("You notice a flibber here.", rows[0]);
        Assert.Equal("Somebody gossips: [censored] " + new string('x', 53), rows[1]);
        Assert.Equal("and then [censored] to finish", rows[2]);
        Assert.Equal("Obvious exits: flibber", rows[3]);
    }

    // A chat line that reaches the right margin and ends there is followed by the
    // game's own row: it is never masked. Only the terminal's record of a wrap
    // makes the next row a continuation.
    [Theory]
    [InlineData(79)]
    [InlineData(80)]
    public void CensorRows_AGameRowAfterAFullWidthChatRow_IsNeverAltered(int width)
    {
        string chat = "Bob gossips: " + new string('a', width - "Bob gossips: ".Length);
        List<string> rows =
        [
            chat,
            "You notice a zorptang here.",
            "Also here: flibber warrior.",
        ];

        Censor.CensorRows(rows, wrappedOntoNext: [false, false, false]);

        Assert.Equal(new[] { chat, "You notice a zorptang here.", "Also here: flibber warrior." }, rows);
    }

    [Fact]
    public void CensorRows_FollowsAChatLineOnlyOntoTheRowsTheTerminalWrappedItOnto()
    {
        string first = "Somebody gossips: " + new string('x', 62);   // all 80 columns
        string second = new string('y', 70) + " flibber ";
        List<string> rows =
        [
            "You notice a flibber here.",
            first,
            second,
            "zorptang to finish",
            "Obvious exits: flibber",
        ];

        Censor.CensorRows(rows, wrappedOntoNext: [false, true, true, false, false]);

        Assert.Equal("You notice a flibber here.", rows[0]);
        Assert.Equal(first, rows[1]);
        Assert.Equal(new string('y', 70) + " [censored] ", rows[2]);
        Assert.Equal("[censored] to finish", rows[3]);
        Assert.Equal("Obvious exits: flibber", rows[4]);
    }

    // A long line of the game's own wraps too: its continuation is the game's.
    [Fact]
    public void CensorRows_AWrappedGameLine_IsNotChatOnEitherRow()
    {
        List<string> rows =
        [
            "The room is strewn with the bones of a great " + new string('z', 35),
            "flibber, long dead.",
            "Somebody gossips: flibber",
        ];

        Censor.CensorRows(rows, wrappedOntoNext: [true, false, false]);

        Assert.Equal("flibber, long dead.", rows[1]);
        Assert.Equal("Somebody gossips: [censored]", rows[2]);
    }

    // End to end through the terminal: the wrap is what the emulator did, not
    // what the row's length suggests.
    [Fact]
    public void CensorRows_ThroughTheTerminal_OnlyARealWrapIsFollowed()
    {
        // Three rows of screen, so the wrapped row is in the backscroll by the end.
        MudPlay.Terminal.TerminalEmulator emulator = new(80, 3);
        static byte[] B(string s) => System.Text.Encoding.Latin1.GetBytes(s);

        // Exactly 80 columns, then a real line end: nothing wrapped.
        emulator.Feed(B("Bob gossips: " + new string('a', 67) + "\r\n"));
        emulator.Feed(B("You notice a zorptang here.\r\n"));
        // 100 columns: the terminal wraps it, and the tail is still Bob's.
        emulator.Feed(B("Bob gossips: " + new string('b', 67) + "and a flibber for you\r\n"));
        emulator.Feed(B("Also here: flibber warrior.\r\n"));
        emulator.Feed(B("[HP=100/MA=50]:"));

        IReadOnlyList<MudPlay.Terminal.TranscriptSnapshot.Line> lines =
            MudPlay.Terminal.TranscriptSnapshot.Tail(emulator, 0);
        List<string> rows = lines.Select(l => l.Text).ToList();
        Assert.Equal(3, emulator.Screen.Scrollback.Count);
        Assert.Equal(new[] { false, false, true, false, false, false }, lines.Select(l => l.SoftWrapped));

        Censor.CensorRows(rows, lines.Select(l => l.SoftWrapped).ToList());

        Assert.Equal("You notice a zorptang here.", rows[1]);
        Assert.Equal("and a [censored] for you", rows[3]);
        Assert.Equal("Also here: flibber warrior.", rows[4]);
    }

    [Theory]
    [InlineData("gos flibber", "gos [censored]")]
    [InlineData("/Somebody flibber you", "/Somebody [censored] you")]
    [InlineData(".flibber", ".[censored]")]
    [InlineData("say zorptang", "say [censored]")]
    [InlineData("bg flibber", "bg [censored]")]
    [InlineData("get flibber", "get flibber")]        // not a chat command
    [InlineData("a zorptang", "a zorptang")]
    public void CensorSentCommand_MasksOnlyChannelCommands(string command, string expected) =>
        Assert.Equal(expected, Censor.CensorSentCommand(command));

    // The telepath's word form is chat; a verb the game doesn't have is not.
    [Theory]
    [InlineData("whisper Somebody flibber", "whisper Somebody [censored]")]
    [InlineData("whi Somebody flibber", "whi Somebody [censored]")]
    [InlineData("tel Somebody flibber", "tel Somebody flibber")]
    [InlineData("wh flibber", "wh flibber")]
    public void CensorSentCommand_TakesTheTelepathsWordForm(string command, string expected) =>
        Assert.Equal(expected, Censor.CensorSentCommand(command));

    // Only a listed word changes: the marks that join the parts of a word stay.
    [Fact]
    public void CensorText_KeepsTheMarksBetweenThePartsOfAWord()
    {
        Assert.Equal("Somebody gossips: ok/[censored]_fine",
            Censor.CensorIfChat("Somebody gossips: ok/flibber_fine"));
        Assert.Equal("Somebody gossips: [censored]/[censored]-x_y",
            Censor.CensorIfChat("Somebody gossips: flibber/zorptang-x_y"));
        Assert.Equal("Somebody gossips: a/b_c-d", Censor.CensorIfChat("Somebody gossips: a/b_c-d"));
    }

    // A program-log message is the client's own text unless it quotes chat: a
    // file path is no telepath, and a quoted game command is no channel.
    [Theory]
    [InlineData("path: /flibber/file")]
    [InlineData("wrote /home/zorptang/.local/share/MudPlay/Logs/flibber.log")]
    [InlineData("AutoGet: 'get flibber'")]
    [InlineData("RoomClassifier: unknown entity: zorptang warrior")]
    public void CensorLogMessage_LeavesPathsAndGameCommandsAlone(string message) =>
        Assert.Equal(message, Censor.CensorLogMessage(message));

    [Fact]
    public void CensorLogMessage_StillMasksAQuotedTelepath() =>
        Assert.Equal("sent '/Somebody you [censored]'", Censor.CensorLogMessage("sent '/Somebody you flibber'"));

    [Fact]
    public void CensorRawWireLine_MasksBetweenColourCodes()
    {
        Assert.Equal("^[[0;35mSomebody gossips: ^[[1mso ^[[0m[censored]^M",
            Censor.CensorRawWireLine("^[[0;35mSomebody gossips: ^[[1mso ^[[0mflibber^M"));
        const string game = "^[[0;32mYou notice a flibber here.^M";
        Assert.Equal(game, Censor.CensorRawWireLine(game));
    }

    [Fact]
    public void CensorLogMessage_MasksOnlyAQuotedChatLineOrChatCommand()
    {
        Assert.Equal("'greet' matched: Somebody gossips: [censored]",
            Censor.CensorLogMessage("'greet' matched: Somebody gossips: flibber"));
        Assert.Equal("sent `gos [censored]`", Censor.CensorLogMessage("sent `gos flibber`"));
        const string own = "picked up a flibber: 2 held";
        Assert.Equal(own, Censor.CensorLogMessage(own));
    }

    [Fact]
    public void ShippedTable_IsNotEmptyAndLeavesOrdinaryWordsAlone()
    {
        BugReportChatCensor shipped = BugReportChatCensor.Default;
        const string line = "Somebody gossips: selling a bastard sword and a silver ring, assassin class only";
        Assert.Equal(line, shipped.CensorIfChat(line));
        Assert.True(BugReportCensorTable.Hashes.Length > 500);
    }
}
