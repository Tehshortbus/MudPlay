using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The account-detail scrub: the login name, login password and suicide password
// never survive into a rendered report, whichever section carried them.
public sealed class BugReportScrubTests
{
    private const string Login = "loginzed";
    private const string Password = "Pa55w0rd!x";
    private const string Suicide = "endgame77";

    private static BugReportScrub.Scrubber MakeScrubber(string? characterName = "Tester") =>
        BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, Login),
            new(BugReportScrub.LoginPasswordLabel, Password),
            new(BugReportScrub.SuicidePasswordLabel, Suicide),
        ],
        [characterName]);

    private static BugReportBuilder.BugReportCapture CaptureWith(BugReportScrub.Scrubber scrubber) => new(
        new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero),
        RealmType.Stock,
        [
            new BugReportBuilder.Section("Scrollback",
                $"```\n12:00:01 Enter your User-ID: {Login}\n12:00:02 [HP=10/MA=5]:{Suicide}\n```"),
            new BugReportBuilder.Section("Wire — raw",
                $"```\n^[[0m{Password}^M\n^[[1;37m{Login.ToUpperInvariant()}^M\n```"),
            new BugReportBuilder.Section("Program log",
                $"```\n12:00:03.120  [Info]  Telnet: sent {Password}\n```"),
            new BugReportBuilder.Section("Commands sent",
                $"```\n12:00:04.000  typed  sent  {Suicide}\n```"),
            new BugReportBuilder.Section("Triggers",
                $"- login helper: pattern `User-ID` -> `{Login}`"),
            new BugReportBuilder.Section("Settings overrides",
                $"```json\n{{ \"Note\": \"{Password}\" }}\n```"),
        ],
        scrubber);

    [Fact]
    public void Render_MasksAllThreeInEverySection()
    {
        string md = BugReportBuilder.Render(CaptureWith(MakeScrubber()), $"my password is {Password}");

        Assert.DoesNotContain(Login, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Suicide, md, StringComparison.OrdinalIgnoreCase);
        // Scrollback x2, wire x2, log, trail, trigger, settings, and the user's own text.
        Assert.Equal(9, CountOf(md, BugReportScrub.Mask));
        // What surrounds a masked value is untouched.
        Assert.Contains("12:00:01 Enter your User-ID: [redacted]", md);
        Assert.Contains("## Program log", md);
    }

    [Fact]
    public void RenderStateOnly_MasksToo()
    {
        string md = BugReportBuilder.RenderStateOnly(CaptureWith(MakeScrubber()));

        Assert.DoesNotContain(Login, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, md);
        Assert.DoesNotContain(Suicide, md);
    }

    [Fact]
    public void SectionTexts_AreMaskedTooAndCarryTheirEntries()
    {
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), RealmType.Stock,
            [new BugReportBuilder.Section("Program log (oldest first)", "2 entries.", ["12:00:01.000  [Info]  A: one", $"12:00:02.000  [Info]  A: {Suicide}"])],
            MakeScrubber());

        IReadOnlyDictionary<string, string> texts = BugReportBuilder.SectionTexts(capture);

        string log = texts["Program log (oldest first)"];
        Assert.Contains("A: one", log);
        Assert.Contains("A: " + BugReportScrub.Mask, log);
        Assert.DoesNotContain(Suicide, log);
    }

    [Fact]
    public void Apply_MasksAValueSplitByAWrappedRow()
    {
        // The terminal wrapped the row mid-word; the next row carries its own time.
        string text = $"12:00:01 Enter your User-ID: {Login[..4]}\n12:00:01 {Login[4..]} ok\n12:00:02 next";

        string scrubbed = MakeScrubber().Apply(text, out int replaced);

        Assert.Equal(1, replaced);
        Assert.DoesNotContain(Login[..4], scrubbed);
        Assert.DoesNotContain(Login[4..], scrubbed);
        Assert.Contains("12:00:02 next", scrubbed);
    }

    // A caller that masks rows as one text and splits it again gets back as many
    // rows as it gave, however many breaks a match ran over.
    [Fact]
    public void Apply_KeepsEveryRowBreakAMatchRanOver()
    {
        string[] rows = [$"a {Suicide[..3]}", Suicide[3..6], $"{Suicide[6..]} b", "next"];

        string[] scrubbed = MakeScrubber().Apply(string.Join('\n', rows)).Split('\n');

        Assert.Equal(new[] { "a " + BugReportScrub.Mask, "", " b", "next" }, scrubbed);
    }

    // The local API builds a scrubber for every response, without compiling its
    // patterns: it masks exactly what the report's does.
    [Fact]
    public void AnUncompiledScrubber_MasksTheSame()
    {
        BugReportScrub.Scrubber uncompiled = BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, Login),
            new(BugReportScrub.LoginPasswordLabel, Password),
            new(BugReportScrub.SuicidePasswordLabel, Suicide),
        ], ["Tester"], compiled: false);
        string text = $"12:00:01 {Login.ToUpperInvariant()} {Password[..5]}\n12:00:01 {Password[5..]} ^[[1m{Suicide[..2]}^[[0m{Suicide[2..]}";

        Assert.Equal(MakeScrubber().Apply(text), uncompiled.Apply(text));
        Assert.DoesNotContain(Suicide[2..], uncompiled.Apply(text));
    }

    // The continuation row of a wrapped line can have no time of its own: the
    // report pads it with blanks instead.
    [Fact]
    public void Apply_MasksAValueWrappedOntoARowWithNoTimeOfItsOwn()
    {
        string text = $"12:00:01 xxxxxxxxxxxxxxxx {Suicide[..4]}\n         {Suicide[4..]} tail";

        Assert.Equal($"12:00:01 xxxxxxxxxxxxxxxx {BugReportScrub.Mask}\n tail", MakeScrubber().Apply(text));
    }

    // A value with a space in it, wrapped at the space. The terminal's row has
    // no trailing blank, so in rows joined bare (the local API's scrollback) the
    // space is simply gone.
    [Theory]
    [InlineData("12:00:01 xxx my\n12:00:01 pass tail", "12:00:01 xxx [redacted]\n tail")]
    [InlineData("12:00:01 xxx my\n         pass tail", "12:00:01 xxx [redacted]\n tail")]
    [InlineData("xxx my\npass tail", "xxx [redacted]\n tail")]
    [InlineData("xxx my pass tail", "xxx [redacted] tail")]
    [InlineData("xxx mypass tail", "xxx mypass tail")]            // not the value: no row break stood for the space
    public void Apply_MasksAValueWithASpaceInIt_WrappedAtTheSpace(string text, string expected)
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, "my pass")], []);

        Assert.Equal(expected, scrubber.Apply(text));
    }

    // The program log prints a line break inside a message as ⏎.
    [Theory]
    [InlineData("12:00:01.123  [Info]  X: sent endg⏎ame77")]
    [InlineData("12:00:01.123  [Info]  X: sent endg ⏎ ame77")]
    public void Apply_MasksAValueBrokenByALineBreakInALogMessage(string line) =>
        Assert.Equal($"12:00:01.123  [Info]  X: sent {BugReportScrub.Mask}", MakeScrubber().Apply(line));

    // A value that is part of the word "redacted" must not be found again inside
    // the masks: it would mark itself out in every one of them.
    [Fact]
    public void AMaskIsNeverReadAgainForAnotherValue_AndMaskingTwiceChangesNothing()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, Login),
            new(BugReportScrub.SuicidePasswordLabel, "acte"),
        ], []);

        string once = scrubber.Apply($"user {Login} enacted it");

        Assert.Equal($"user {BugReportScrub.Mask} en{BugReportScrub.Mask}d it", once);
        Assert.Equal(once, scrubber.Apply(once));
    }

    // One value's end is the other's start, and the text runs them together:
    // neither is left with its tail showing.
    [Fact]
    public void ValuesThatOverlapInTheText_AreMaskedAsOne()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, "abcd"),
            new(BugReportScrub.SuicidePasswordLabel, "defg"),
        ], []);

        Assert.Equal($"x {BugReportScrub.Mask} y", scrubber.Apply("x abcdefg y"));
        // Side by side they are two.
        Assert.Equal($"{BugReportScrub.Mask}{BugReportScrub.Mask}", scrubber.Apply("abcddefg"));
    }

    // Only the name the game shows everywhere (the full name, or the first name)
    // exempts a login name. A login that is the surname alone is masked.
    [Fact]
    public void LoginNameEqualToTheSurnameAlone_IsMasked()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.LoginNameLabel, "Ironhand")], ["Thorgrim Ironhand"]);

        Assert.Equal($"id {BugReportScrub.Mask}", scrubber.Apply("id Ironhand"));
        Assert.Empty(scrubber.Notes);
    }

    // The game echoes a suicide password as stars. Paradigm prints one for each
    // character sent, however many; Stock's call is echsec('*', 8). Whatever the
    // run's length it is the echo, and it is masked whole: a report doesn't say
    // how long the password is. The placeholder here is 20 characters.
    [Theory]
    [InlineData("12:00:01 Enter your suicide password:********************", "12:00:01 Enter your suicide password:[redacted]")]   // Paradigm: one a character
    [InlineData("12:00:01 Enter your suicide password:********", "12:00:01 Enter your suicide password:[redacted]")]               // Stock: a width of 8
    [InlineData("12:00:01 [HP=100/MA=50]:Enter your suicide password: ***", "12:00:01 [HP=100/MA=50]:Enter your suicide password: [redacted]")]
    [InlineData("12:00:01 Enter the current password:*********************", "12:00:01 Enter the current password:[redacted]")]
    [InlineData("12:00:01 Enter New Password:*", "12:00:01 Enter New Password:[redacted]")]
    [InlineData("Enter your suicide password:^[[0;37m****^[[1m****^[[0m^M", "Enter your suicide password:^[[0;37m[redacted]^[[0m^M")]
    [InlineData("Enter New Password:***^H ^H*****^M", "Enter New Password:[redacted]^M")]
    public void TheEchoOfASuicidePassword_IsMaskedWhateverItsLength(string text, string expected)
    {
        // Saved or not: a password typed by hand is echoed all the same.
        Assert.Equal(expected, MakeScrubber().Apply(text));
        Assert.Equal(expected, BugReportScrub.Build([], []).Apply(text));
        // Masking again changes nothing.
        Assert.Equal(expected, MakeScrubber().Apply(expected));
    }

    [Theory]
    [InlineData("12:00:01 *Combat Off*")]
    [InlineData("12:00:01 Enter your suicide password:")]
    [InlineData("12:00:01 Enter your suicide password:\n12:00:02 *Combat Off*")]
    [InlineData("Enter your suicide password:^M\n*Combat Engaged*^M")]
    [InlineData("12:00:01 Somebody gossips: my password is ******** lol")]
    public void StarsThatAreNotAPasswordsEcho_AreLeft(string text) =>
        Assert.Equal(text, MakeScrubber().Apply(text));

    // At 40 columns the statline and the prompt don't fit a row, and neither does
    // a prompt with a long echo: the terminal wraps, and the echo (or the prompt
    // it follows) is split over two rows. Read as the one line the terminal
    // recorded them to be, the echo is found and masked; no star is left, and
    // the row count stays.
    [Theory]
    [InlineData("[HP=100/MA=50]:Enter your suicide password:", 20)]   // the prompt itself wraps
    [InlineData("Enter your suicide password:", 20)]                  // the echo wraps
    [InlineData("Enter your suicide password:", 60)]                  // onto a third row
    [InlineData("xxxxxxxxxxxxEnter your suicide password:", 9)]       // the prompt ends at the margin
    [InlineData("Enter New Password:", 30)]
    public void TheEchoOfASuicidePassword_WrappedAtFortyColumns_IsMasked(string prompt, int stars)
    {
        MudPlay.Terminal.TerminalEmulator emulator = new(40, 12);
        static byte[] B(string s) => System.Text.Encoding.Latin1.GetBytes(s);
        emulator.Feed(B("Town Square\r\n"));
        emulator.Feed(B(prompt + new string('*', stars) + "\r\n"));
        emulator.Feed(B("Invalid password specified.\r\n"));
        emulator.Feed(B("*Combat Off*\r\n"));

        IReadOnlyList<MudPlay.Terminal.TranscriptSnapshot.Line> lines = MudPlay.Terminal.TranscriptSnapshot.Tail(emulator, 0);
        List<string> rows = lines.Select(l => l.Text).ToList();
        int before = rows.Count;
        Assert.Contains(lines, l => l.SoftWrapped);

        BugReportScrub.MaskWrappedPasswordEchoes(rows, lines.Select(l => l.SoftWrapped).ToList(), 40);
        string text = BugReportScrub.Build([], []).Apply(string.Join('\n', rows));

        Assert.Equal(before, rows.Count);
        Assert.Equal(1, CountOf(text, BugReportScrub.Mask));
        Assert.Equal(2, text.Count(c => c == '*'));   // the game's own *Combat Off*
        Assert.Contains("Town Square", text);
        Assert.Contains("Invalid password specified.\n*Combat Off*", text);
        // The prompt's words are all still there, across the rows they were on.
        Assert.Contains(prompt.Replace(" ", string.Empty), string.Concat(rows).Replace(" ", string.Empty));
    }

    // A row that only follows a prompt was not wrapped onto: its stars are the
    // game's.
    [Fact]
    public void AStarredRowAfterAPromptRow_ThatWasNotWrappedOnto_IsLeft()
    {
        List<string> rows = ["Enter your suicide password:", "*Combat Off*", "xxxxxxxxxx", "*Combat Engaged*"];

        BugReportScrub.MaskWrappedPasswordEchoes(rows, [false, false, true, false], 10);

        Assert.Equal(new[] { "Enter your suicide password:", "*Combat Off*", "xxxxxxxxxx", "*Combat Engaged*" }, rows);
    }

    [Fact]
    public void Render_ShowsNoPasswordLength_FromItsEcho()
    {
        const string placeholder = "placeholder-password-20";
        BugReportBuilder.BugReportCapture capture = new(
            new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), RealmType.ParaMud,
            [
                new BugReportBuilder.Section("Scrollback (oldest first)", "intro",
                    ["12:00:01 [HP=10/MA=5]:suicide", "12:00:01 Enter your suicide password:" + new string('*', placeholder.Length), "12:00:02 Invalid password specified."]),
                new BugReportBuilder.Section("Wire — raw", "```\nEnter your suicide password:" + new string('*', placeholder.Length) + "^M\n```", InBudget: false),
            ],
            BugReportScrub.Build([new(BugReportScrub.SuicidePasswordLabel, placeholder)], []));

        string md = BugReportBuilder.Render(capture, "x");

        Assert.DoesNotContain("***", md);
        Assert.Equal(2, CountOf(md, "Enter your suicide password:" + BugReportScrub.Mask));
    }

    [Fact]
    public void Apply_IsCaseInsensitive()
    {
        string scrubbed = MakeScrubber().Apply($"x {Password.ToUpperInvariant()} y {Suicide.ToUpperInvariant()} z");
        Assert.Equal($"x {BugReportScrub.Mask} y {BugReportScrub.Mask} z", scrubbed);
    }

    [Fact]
    public void LoginNameEqualToTheCharacterName_IsLeftAndSaidSo()
    {
        BugReportScrub.Scrubber scrubber = MakeScrubber(characterName: "Loginzed Surname");

        Assert.Equal($"{Login} telepaths: hi", scrubber.Apply($"{Login} telepaths: hi"));
        Assert.Contains(scrubber.Notes, n => n.Contains("same as the character's name"));
        // The passwords are still masked.
        Assert.Equal(BugReportScrub.Mask, scrubber.Apply(Password));
    }

    [Fact]
    public void ValueShorterThanFourCharacters_IsMaskedAsAWholeWordOnly_AndNothingSaysHowLongItIs()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.SuicidePasswordLabel, "ab"), new(BugReportScrub.LoginPasswordLabel, Password)],
            []);

        Assert.Equal("cabbage", scrubber.Apply("cabbage"));
        Assert.Equal("[HP=1]:[redacted]", scrubber.Apply("[HP=1]:ab"));
        Assert.Equal("typed  sent  [redacted]", scrubber.Apply("typed  sent  AB"));
        Assert.Empty(scrubber.Notes);
        Assert.Equal(2, scrubber.MaskedValues);
    }

    // A saved three-letter value was masked inside every word holding it, and a
    // report read "[redacted]gram log" (paradigm-20261030-190322). As a word of
    // its own it is still masked, wherever the report shows it.
    [Theory]
    [InlineData("## Program log", "## Program log")]
    [InlineData("12:00:01 [HP=10]:Probe stats, prompt", "12:00:01 [HP=10]:Probe stats, prompt")]
    [InlineData("12:00:01 Enter your User-ID: pro", "12:00:01 Enter your User-ID: [redacted]")]
    [InlineData("12:00:04.000  typed  sent  PRO", "12:00:04.000  typed  sent  [redacted]")]
    [InlineData("^[[1;37mpro^M", "^[[1;37m[redacted]^M")]
    [InlineData("^[[0;37mp^[[1mro^M", "^[[0;37m[redacted]^M")]
    [InlineData("12:00:01 xxx p\n12:00:01 ro tail", "12:00:01 xxx [redacted]\n tail")]
    public void AThreeLetterValue_IsMaskedOnlyAsAWordOfItsOwn(string text, string expected)
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, "pro")], []);

        Assert.Equal(expected, scrubber.Apply(text));
    }

    // A trigger that only holds the value inside another word is listed as it is.
    [Fact]
    public void ADefinitionWithAShortValueInsideAWord_IsNotLeftOut()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, "pro")], []);

        Assert.False(scrubber.Contains("on `prompt` -> `rest`"));
        Assert.True(scrubber.Contains("on `User-ID` -> `PRO`"));
    }

    [Fact]
    public void AValueIsAlwaysWrittenAsRedacted_NeverBlankedOrStarred()
    {
        // A password that is an ordinary word is masked inside other words too.
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, "sword")], []);

        Assert.Equal("a bastard [redacted] and a [redacted]sman", scrubber.Apply("a bastard sword and a swordsman"));
    }

    [Fact]
    public void AValueBrokenByAColourCodeOrACorrectionInTheRawWire_IsMasked()
    {
        BugReportScrub.Scrubber scrubber = MakeScrubber();

        // A colour change in the middle of the word.
        Assert.DoesNotContain("zed", scrubber.Apply($"^[[0;37m{Login[..5]}^[[1;33m{Login[5..]}^M"));
        // Typed with a slip and a backspace: the game erases with ^H space ^H.
        string corrected = $"User-ID: {Login[..4]}x^H ^H{Login[4..]}^M";
        string masked = scrubber.Apply(corrected);
        Assert.Contains(BugReportScrub.Mask, masked);
        Assert.DoesNotContain(Login[4..], masked);
        // A line with a backspace and no value in it is left exactly as it was.
        const string overstrike = "Obvious exits: n^Hnorth^M";
        Assert.Equal(overstrike, scrubber.Apply(overstrike));
    }

    [Fact]
    public void ANonAsciiValue_IsMaskedAsTheTerminalShowsItToo()
    {
        const string secret = "pässwörd";
        string shown = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(437)!
            .GetString(System.Text.Encoding.Latin1.GetBytes(secret));
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginPasswordLabel, secret)], []);

        Assert.Equal(BugReportScrub.Mask, scrubber.Apply(secret));
        Assert.Equal(BugReportScrub.Mask, scrubber.Apply(shown));
    }

    [Fact]
    public void OnlyTheInGameNameExemptsALoginName_AndUnreadableValuesAreCounted()
    {
        // Built as the client builds it: the stat sheet's name is the only public one.
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.LoginNameLabel, Login)], ["Someone Else"], unreadable: 2);

        Assert.Equal(BugReportScrub.Mask, scrubber.Apply(Login));
        Assert.Equal(2, scrubber.UnreadableValues);
    }

    [Fact]
    public void Prescrubbed_MasksEverySectionAndDropsTheScrubber()
    {
        BugReportBuilder.BugReportCapture held = BugReportBuilder.Prescrubbed(CaptureWith(MakeScrubber()));

        Assert.Null(held.Scrubber);
        string md = BugReportBuilder.RenderStateOnly(held);
        Assert.DoesNotContain(Login, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, md);
        Assert.DoesNotContain(Suicide, md);
    }

    [Fact]
    public void Contains_FindsAValueInADefinition()
    {
        BugReportScrub.Scrubber scrubber = MakeScrubber();
        Assert.True(scrubber.Contains($"say {Password.ToLowerInvariant()}"));
        Assert.False(scrubber.Contains("say hello"));
        Assert.False(scrubber.Contains(null));
    }

    [Fact]
    public void NoSecrets_ChangesNothing()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginNameLabel, null)], []);
        Assert.Equal(0, scrubber.MaskedValues);
        Assert.Equal("anything at all", scrubber.Apply("anything at all"));
    }

    [Fact]
    public void Scrubber_DoesNotPrintItsValues()
    {
        string printed = CaptureWith(MakeScrubber()).ToString();
        Assert.DoesNotContain(Login, printed);
        Assert.DoesNotContain(Password, printed);
        Assert.DoesNotContain(Suicide, printed);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
