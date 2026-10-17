using System.Text;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The sent-command trail: one entry per command, with its origin, its sender and
// what became of it; nothing kept while a login or a password may be going out.
public sealed class SentCommandTrailTests
{
    private static byte[] Line(string text) => Encoding.Latin1.GetBytes(text + "\r");

    private static (EngineSendGate gate, SentCommandTrail trail, List<string> wire) Setup()
    {
        EngineSendGate gate = new();
        SentCommandTrail trail = new();
        gate.Trail = trail;
        return (gate, trail, new List<string>());
    }

    [Fact]
    public void WrappedSend_IsRecordedOnceWithItsSenderName()
    {
        var (gate, trail, wire) = Setup();
        Action<byte[]> engineSend = gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b)));
        Action<byte[]> combat = gate.Named(engineSend, "Combat");

        combat(Line("a orc"));

        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal("a orc", entry.Text);
        Assert.Equal(SentCommandTrail.Origin.Engine, entry.Origin);
        Assert.Equal("Combat", entry.Sender);
        Assert.Equal(SentCommandTrail.Outcome.Sent, entry.Outcome);
        Assert.Equal("a orc\r", Assert.Single(wire));
        Assert.Null(gate.CurrentSender);
    }

    [Fact]
    public void RouteStepsAndUserRelayedCommands_CarryTheirOrigin()
    {
        var (gate, trail, _) = Setup();
        Action<byte[]> route = gate.Named(gate.WrapEngineSender(_ => { }, routeSteps: true), "Walker");
        Action<byte[]> trigger = gate.Named(gate.MarkUserCommands(gate.WrapEngineSender(_ => { })), "Trigger");

        route(Line("n"));
        trigger(Line("gos hello"));

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal((SentCommandTrail.Origin.RouteStep, "Walker"), (entries[0].Origin, entries[0].Sender));
        Assert.Equal((SentCommandTrail.Origin.UserRelayed, "Trigger"), (entries[1].Origin, entries[1].Sender));
    }

    [Fact]
    public void LockedGate_RecordsTheDropWithTheHoldAndCountsIt()
    {
        var (gate, trail, wire) = Setup();
        Action<byte[]> poller = gate.Named(gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b))), "PartyPoller");
        gate.Hold("TrainerScreen");

        poller(Line("par"));

        Assert.Empty(wire);
        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal(SentCommandTrail.Outcome.Dropped, entry.Outcome);
        Assert.Equal("send gate held: TrainerScreen", entry.Why);
        Assert.Equal(1, gate.DroppedCount);
        Assert.Equal(("par", "PartyPoller"), (gate.LastDropped!.Value.Command, gate.LastDropped.Value.Sender));
        Assert.Equal(new[] { "TrainerScreen" }, gate.Holds);
    }

    [Fact]
    public void TakenForLaterBySneakKeeping_IsRecordedAsHeld()
    {
        var (gate, trail, wire) = Setup();
        gate.SetSneakHooks(takeForLater: command => command.StartsWith("say", StringComparison.Ordinal), sent: _ => { });
        Action<byte[]> sender = gate.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b)));

        sender(Line("say level up"));

        Assert.Empty(wire);
        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal(SentCommandTrail.Outcome.HeldForLater, entry.Outcome);
    }

    [Fact]
    public void TypedInput_BecomesOneEntryPerLine_WhetherSentWholeOrKeyByKey()
    {
        SentCommandTrail trail = new();

        trail.ObserveTyped(Encoding.Latin1.GetBytes("look\r\n"));
        foreach (char key in "nw") trail.ObserveTyped([(byte)key]);
        trail.ObserveTyped([0x08]);                       // backspace over the w
        trail.ObserveTyped([0x1B, (byte)'[', (byte)'A']); // an arrow key's escape is not text
        trail.ObserveTyped([(byte)'\r']);

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal("look", entries[0].Text);
        Assert.Equal("n", entries[1].Text);
        Assert.All(entries, e => Assert.Equal(SentCommandTrail.Origin.Typed, e.Origin));
    }

    // Each line the user types is told as its Enter goes out, as the line their keys
    // made up: sent whole by the terminal, or a key at a time with a backspace and
    // an arrow key among them. It is told whether or not the trail keeps it (a line
    // typed at a prompt other than the game's is not kept), since it is what was
    // typed either way. A line any part of which went by while nothing is kept is
    // not told: it may be a password.
    [Fact]
    public void TypedLineEntered_IsToldTheLineTheKeysMadeUp_InEitherInputMode()
    {
        SentCommandTrail trail = new();
        List<string> told = new();
        trail.TypedLineEntered = told.Add;
        trail.TypedAtGamePrompt = _ => true;    // typed at the game's own prompt

        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo @timer sync\r"));
        foreach (char key in "/suu") trail.ObserveTyped([(byte)key]);
        trail.ObserveTyped([0x7F]);                        // delete the second u
        trail.ObserveTyped([0x1B, (byte)'[', (byte)'D']);  // an arrow key
        foreach (char key in "e @timer sync") trail.ObserveTyped([(byte)key]);
        trail.ObserveTyped([(byte)'\r']);
        trail.ObserveTyped([(byte)'\r']);                  // a bare Enter is no line

        Assert.Equal(new[] { "/bo @timer sync", "/sue @timer sync" }, told);
        Assert.Equal(told, trail.Snapshot().Select(e => e.Text));
    }

    // A line typed at a prompt that is not the game's own may be a password: the
    // trail keeps only that something was typed there, and tells the line to
    // nobody. A telepath typed at the game's prompt afterwards is still told.
    [Fact]
    public void TypedLineEntered_IsNotToldALineTheTrailWithholdsAtAPrompt()
    {
        SentCommandTrail trail = new();
        List<string> told = new();
        trail.TypedLineEntered = told.Add;
        bool atGamePrompt = false;
        trail.TypedAtGamePrompt = _ => atGamePrompt;

        trail.ObserveTyped(Encoding.Latin1.GetBytes("hunter2\r"));
        trail.ObserveTyped(Encoding.Latin1.GetBytes("/hunter 2\r"));   // one shaped like a telepath

        Assert.Empty(told);
        Assert.All(trail.Snapshot(), e => Assert.Equal(SentCommandTrail.WithheldAtPrompt, e.Text));
        Assert.Equal(2, trail.Snapshot().Length);

        atGamePrompt = true;
        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo @timer sync\r"));
        Assert.Equal("/bo @timer sync", Assert.Single(told));
    }

    // A typed line longer than the game takes is not kept by the trail, which
    // holds only its head: it is told to nobody either. One of the full length is.
    [Fact]
    public void TypedLineEntered_IsNotToldALineLongerThanTheTrailKeeps()
    {
        SentCommandTrail trail = new();
        List<string> told = new();
        trail.TypedLineEntered = told.Add;
        trail.TypedAtGamePrompt = _ => true;

        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo " + new string('x', SentCommandTrail.MaxTextLength) + "\r"));
        Assert.Empty(told);
        Assert.Equal(SentCommandTrail.TooLong, Assert.Single(trail.Snapshot()).Text);

        string full = "/bo " + new string('x', SentCommandTrail.MaxTextLength - 4);
        trail.ObserveTyped(Encoding.Latin1.GetBytes(full + "\r"));
        Assert.Equal(full, Assert.Single(told));
    }

    [Fact]
    public void TypedLineEntered_IsNotToldALineTypedWhileNothingIsKept()
    {
        SentCommandTrail trail = new();
        List<string> told = new();
        trail.TypedLineEntered = told.Add;
        bool withhold = true;
        trail.Withhold = () => withhold;

        trail.ObserveTyped(Encoding.Latin1.GetBytes("secret\r"));
        // Begun while nothing is kept, finished after: still not told.
        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo @ti"));
        withhold = false;
        trail.ObserveTyped(Encoding.Latin1.GetBytes("mer sync\r"));
        Assert.Empty(told);

        // Typed while lines are kept, its Enter while they are not.
        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo @timer sync"));
        withhold = true;
        trail.ObserveTyped([(byte)'\r']);
        Assert.Empty(told);

        withhold = false;
        trail.ObserveTyped(Encoding.Latin1.GetBytes("/bo @timer sync\r"));
        Assert.Equal("/bo @timer sync", Assert.Single(told));
    }

    // The five ways a typed suicide password could reach the trail. The line is
    // judged at its Enter by the terminal's cursor row: only a line typed at the
    // game's own prompt is kept.
    [Theory]
    [InlineData("set sui", "Enter New Password:")]
    [InlineData("SET  SUICIDE", "Enter the current password:")]
    [InlineData("reroll", "Enter your suicide password:")]
    [InlineData("suicide", "Enter your suicide password:")]
    public void ATypedPassword_IsWithheld_ByTheCursorRowAlone(string command, string promptRow)
    {
        string row = "[HP=100/MA=50]:";
        SentCommandTrail trail = new() { TypedAtGamePrompt = line => SentCommandTrail.RowIsGamePrompt(row, line) };

        trail.ObserveTyped(Encoding.Latin1.GetBytes(command + "\r"));
        row = promptRow;
        trail.ObserveTyped(Encoding.Latin1.GetBytes("s3cretpw\r"));
        row = "[HP=100/MA=50]:";
        trail.ObserveTyped(Encoding.Latin1.GetBytes("look\r"));

        string[] texts = trail.Snapshot().Select(e => e.Text).ToArray();
        Assert.Equal(new[] { command, SentCommandTrail.WithheldAtPrompt, "look" }, texts);
    }

    // The same flows by the other cover: the suicide tracker says a password may
    // be going out from the command on, in every spelling the game takes, and
    // the trail keeps nothing while it does. Typed in the order SendUserInput
    // hands a line on: the tracker first, then the trail.
    [Theory]
    [InlineData("set suicide")]
    [InlineData("set sui")]
    [InlineData("SET  SUICIDE")]
    [InlineData("reroll")]
    [InlineData("suicide")]
    public void ATypedPassword_IsNotKept_UnderTheSuicideTrackersCoverAlone(string command)
    {
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        string tmp = Path.Combine(Path.GetTempPath(), "mudplay-trail-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            MudPlay.Game.SuicidePasswordTracker tracker =
                new(router, new EngineSendGate(), profile, new PasswordProtector(Path.Combine(tmp, ".credkey")));
            SentCommandTrail trail = new() { Withhold = () => tracker.PasswordEntryLikely };

            foreach (string line in new[] { command, "s3cretpw" })
            {
                byte[] data = Encoding.Latin1.GetBytes(line + "\r");
                tracker.ObserveOutbound(data);
                trail.ObserveTyped(data);
            }

            Assert.DoesNotContain(trail.Snapshot(), e => e.Text.Contains("s3cretpw"));
            Assert.Equal(2, trail.Withheld);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // An engine's line goes out between the typed `suicide` and the password. The
    // game takes the engine's line for the password, so the user's lands at the
    // ordinary game prompt, where the cursor-row rule keeps it. The tracker's
    // cover must still be standing: an engine's line doesn't use it up.
    [Theory]
    [InlineData("suicide")]
    [InlineData("reroll")]
    public void ATypedPassword_AtTheStatlineAfterAnEngineLineAteItsPrompt_IsNotKept(string command)
    {
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        string tmp = Path.Combine(Path.GetTempPath(), "mudplay-trail-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            EngineSendGate gate = new();
            MudPlay.Game.SuicidePasswordTracker tracker =
                new(router, gate, profile, new PasswordProtector(Path.Combine(tmp, ".credkey")));
            SentCommandTrail trail = new()
            {
                Withhold = () => tracker.PasswordEntryLikely,
                // The statline is on the cursor row throughout.
                TypedAtGamePrompt = line => SentCommandTrail.RowIsGamePrompt("[HP=100/MA=50]:", line),
            };
            gate.Trail = trail;
            void Typed(string line)
            {
                byte[] data = Encoding.Latin1.GetBytes(line + "\r");
                tracker.ObserveOutbound(data, usersOwn: true);
                trail.ObserveTyped(data);
            }
            Action<byte[]> engine = gate.Named(
                gate.WrapEngineSender(data => tracker.ObserveOutbound(data, usersOwn: false)), "PartyPoller");

            Typed(command);
            engine(Line("par"));
            engine(Line("stat"));
            Typed("s3cretpw");
            Typed("look");

            // While the cover stands nothing is kept, the engine's lines included;
            // it ends with the user's own second line.
            Assert.DoesNotContain(trail.Snapshot(), e => e.Text.Contains("s3cretpw"));
            Assert.Equal(new[] { "look" }, trail.Snapshot().Select(e => e.Text));
            Assert.Equal(4, trail.Withheld);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // A line is kept whole or not at all. Cut short, it could end in the first
    // half of a password, and the report's masking looks for the whole value.
    [Fact]
    public void ALineUpToTheGamesLimit_IsKeptWhole_AndALongerOneIsNotKeptInPart()
    {
        const string placeholder = "placeholder-password";
        // The value lies across what used to be the cut, at 160 characters.
        string line = "say " + new string('x', 146) + placeholder + " tail";
        Assert.True(line.IndexOf(placeholder, StringComparison.Ordinal) < 160
            && line.IndexOf(placeholder, StringComparison.Ordinal) + placeholder.Length > 160);
        SentCommandTrail trail = new();

        trail.ObserveTyped(Encoding.Latin1.GetBytes(line + "\r"));
        trail.Record(new string('y', SentCommandTrail.MaxTextLength), SentCommandTrail.Origin.Engine, "E", SentCommandTrail.Outcome.Sent);
        trail.ObserveUserLine(Encoding.Latin1.GetBytes(new string('z', 250) + placeholder + "\r\n"), SentCommandTrail.Origin.Api);
        trail.ObserveTyped(Encoding.Latin1.GetBytes(new string('w', 250) + placeholder + "\r"));

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal(line, entries[0].Text);
        Assert.Equal(SentCommandTrail.MaxTextLength, entries[1].Text.Length);
        Assert.Equal(SentCommandTrail.TooLong, entries[2].Text);
        Assert.Equal(SentCommandTrail.TooLong, entries[3].Text);

        // So the report's masking finds the whole value, and nothing of it is left.
        BugReportBuilder.BugReportCapture capture = new(
            DateTimeOffset.Now, MudPlay.Game.RealmType.Stock,
            [BugReportSentCommands.Build(trail, new BugReportChatCensor(Array.Empty<ulong>()))],
            BugReportScrub.Build([new(BugReportScrub.SuicidePasswordLabel, placeholder)], []));
        string md = BugReportBuilder.Render(capture, "x");
        Assert.DoesNotContain("placeholder", md);
        Assert.Contains(new string('x', 146) + BugReportScrub.Mask + " tail", md);
    }

    [Fact]
    public void ATypedPassword_IsWithheld_WhenAnEngineCommandWentOutBetween()
    {
        string row = "[HP=100/MA=50]:";
        EngineSendGate gate = new();
        SentCommandTrail trail = new() { TypedAtGamePrompt = line => SentCommandTrail.RowIsGamePrompt(row, line) };
        gate.Trail = trail;

        trail.ObserveTyped(Encoding.Latin1.GetBytes("suicide\r"));
        row = "Enter your suicide password:";
        gate.Named(gate.WrapEngineSender(_ => { }), "PartyPoller")(Line("par"));
        trail.ObserveTyped(Encoding.Latin1.GetBytes("s3cretpw\r"));

        Assert.DoesNotContain(trail.Snapshot(), e => e.Text.Contains("s3cretpw"));
        Assert.Equal(SentCommandTrail.WithheldAtPrompt, trail.Snapshot()[^1].Text);
    }

    [Theory]
    [InlineData("[HP=100/MA=50]:", "look", true)]
    [InlineData("[HP=100/MA=50]: (Resting)", "look", true)]
    [InlineData("[HP=100/MA=50]:lo", "look", true)]            // the game echoing the line as typed
    [InlineData("[HP=100/MA=50]:Enter your suicide password:", "x", false)]
    [InlineData("Enter New Password:", "x", false)]
    [InlineData("Are you sure (y/n)?", "y", false)]
    [InlineData("", "look", false)]
    public void RowIsGamePrompt_OnlyTheStatlinePrompt(string row, string typed, bool expected) =>
        Assert.Equal(expected, SentCommandTrail.RowIsGamePrompt(row, typed));

    // Settings -> Statline decides what the prompt looks like, so the row is read
    // with the pattern built from that setting: a statline with no "[HP=" in it
    // is still the game's prompt, and the class-default shape is not once the
    // setting says otherwise.
    [Theory]
    [InlineData("<100 hp / 50 ma>", "look", true)]
    [InlineData("<100 hp / 50 ma> lo", "look", true)]
    [InlineData("<100 hp / 50 ma> (Resting)", "look", true)]
    [InlineData("<100 hp / 50 ma> Enter your suicide password:", "x", false)]
    [InlineData("Bob gossips: <100 hp / 50 ma>", "look", false)]
    [InlineData("[HP=100/MA=50]:", "look", false)]
    [InlineData("Enter New Password:", "x", false)]
    public void RowIsGamePrompt_ReadsTheConfiguredStatline(string row, string typed, bool expected) =>
        Assert.Equal(expected, SentCommandTrail.RowIsGamePrompt(
            row, typed, MudPlay.Game.StatlinePromptRegexBuilder.Build("full custom <%h hp / %m ma>%r ")));

    [Fact]
    public void TypedAtACustomStatline_IsKept_AndAPasswordAfterItIsNot()
    {
        System.Text.RegularExpressions.Regex statline =
            MudPlay.Game.StatlinePromptRegexBuilder.Build("full custom %h/%Hhp %m/%Mma> ");
        string row = "212/212hp 40/90ma>";
        SentCommandTrail trail = new() { TypedAtGamePrompt = line => SentCommandTrail.RowIsGamePrompt(row, line, statline) };

        trail.ObserveTyped(Encoding.Latin1.GetBytes("suicide\r"));
        row = "212/212hp 40/90ma> Enter your suicide password:";
        trail.ObserveTyped(Encoding.Latin1.GetBytes("s3cretpw\r"));
        row = "212/212hp 40/90ma>";
        trail.ObserveTyped(Encoding.Latin1.GetBytes("look\r"));

        Assert.Equal(new[] { "suicide", SentCommandTrail.WithheldAtPrompt, "look" },
            trail.Snapshot().Select(e => e.Text));
    }

    // A line of the user's that didn't come from the terminal's keyboard says
    // where it did come from, and is not joined to a line still being typed.
    [Fact]
    public void LinesFromAnAliasAWindowAndTheApi_CarryTheirOwnOrigin_ApartFromTheLineBeingTyped()
    {
        SentCommandTrail trail = new();

        trail.ObserveTyped("lo"u8);                                              // half a line at the keyboard
        trail.ObserveUserLine("exp\r\n"u8, SentCommandTrail.Origin.Window);
        trail.ObserveUserLine("c 'heal' bob\r\n"u8, SentCommandTrail.Origin.Alias);
        trail.ObserveUserLine("gos hi\r"u8, SentCommandTrail.Origin.Api);
        trail.ObserveUserLine("\r"u8, SentCommandTrail.Origin.Window);           // a bare Enter is no command
        trail.ObserveTyped("ok\r"u8);

        Assert.Equal(new[]
        {
            (SentCommandTrail.Origin.Window, "exp"),
            (SentCommandTrail.Origin.Alias, "c 'heal' bob"),
            (SentCommandTrail.Origin.Api, "gos hi"),
            (SentCommandTrail.Origin.Typed, "look"),
        }, trail.Snapshot().Select(e => (e.Origin, e.Text)));
        Assert.All(trail.Snapshot(), e => Assert.Null(e.Sender));
    }

    // The game reads a window's or the API's line at whatever prompt is up, so
    // the rule for a typed line holds for them all: not at the game's own prompt,
    // not kept.
    [Theory]
    [InlineData(SentCommandTrail.Origin.Alias)]
    [InlineData(SentCommandTrail.Origin.Api)]
    [InlineData(SentCommandTrail.Origin.Window)]
    public void ALineFromAnyOrigin_IsWithheld_AtAPromptThatIsNotTheGames(SentCommandTrail.Origin origin)
    {
        string row = "[HP=100/MA=50]:";
        SentCommandTrail trail = new() { TypedAtGamePrompt = line => SentCommandTrail.RowIsGamePrompt(row, line) };

        trail.ObserveUserLine("set suicide\r\n"u8, origin);
        row = "Enter New Password:";
        trail.ObserveUserLine("s3cretpw\r\n"u8, origin);

        Assert.Equal(new[] { (origin, "set suicide"), (origin, SentCommandTrail.WithheldAtPrompt) },
            trail.Snapshot().Select(e => (e.Origin, e.Text)));
    }

    [Theory]
    [InlineData(SentCommandTrail.Origin.Alias)]
    [InlineData(SentCommandTrail.Origin.Api)]
    [InlineData(SentCommandTrail.Origin.Window)]
    public void ALineFromAnyOrigin_IsOnlyCounted_WhileNothingIsKept(SentCommandTrail.Origin origin)
    {
        SentCommandTrail trail = new() { Withhold = () => true };

        trail.ObserveUserLine("my-login-name\r\n"u8, origin);

        Assert.Empty(trail.Snapshot());
        Assert.Equal(1, trail.Withheld);
    }

    [Fact]
    public void ReportSection_NamesAnAliasAWindowAndTheApi()
    {
        SentCommandTrail trail = new();
        trail.ObserveUserLine("exp\r\n"u8, SentCommandTrail.Origin.Window);
        trail.ObserveUserLine("sneak\r\n"u8, SentCommandTrail.Origin.Alias);
        trail.ObserveUserLine("look\r\n"u8, SentCommandTrail.Origin.Api);
        trail.ObserveTyped("n\r"u8);

        BugReportBuilder.Section section = BugReportSentCommands.Build(
            trail, new BugReportChatCensor(Array.Empty<ulong>()));

        Assert.Matches(@"  window +sent     exp$", section.Entries![0]);
        Assert.Matches(@"  alias +sent     sneak$", section.Entries[1]);
        Assert.Matches(@"  api +sent     look$", section.Entries[2]);
        Assert.Matches(@"  typed +sent     n$", section.Entries[3]);
        Assert.Contains("`alias`", section.Body);
        Assert.Contains("`window`", section.Body);
        Assert.Contains("`api`", section.Body);
    }

    [Fact]
    public void KeysPressedWhileNothingIsKept_DoNotHeadTheFirstLineTypedInTheGame()
    {
        bool withhold = true;
        SentCommandTrail trail = new() { Withhold = () => withhold };

        trail.ObserveTyped("e"u8);          // a hot key at the board's menu
        withhold = false;
        trail.ObserveTyped("look\r"u8);
        trail.ObserveTyped("north\r"u8);

        Assert.Equal(new[] { "north" }, trail.Snapshot().Select(e => e.Text));
        Assert.Equal(1, trail.Withheld);
    }

    [Fact]
    public void RepeatedDropsOfOneCommandUnderOneHold_AreOneEntryWithACount()
    {
        var (gate, trail, _) = Setup();
        Action<byte[]> poller = gate.Named(gate.WrapEngineSender(_ => { }), "PartyPoller");
        gate.Hold("MortallyWounded");

        for (int i = 0; i < 5; i++) poller(Line("par"));

        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.StartsWith("send gate held: MortallyWounded; dropped 5 times, the last at ", entry.Why);
        Assert.Equal(5, gate.DroppedCount);
    }

    [Theory]
    [InlineData("set suicide", true)]
    [InlineData("set sui", true)]
    [InlineData("SET  SUICIDE", true)]
    [InlineData("set s", true)]
    [InlineData("set statline full", false)]
    [InlineData("set suicidex", false)]
    [InlineData("suicide", false)]
    public void IsSetSuicide_TakesAnyPrefixAnySpacing(string line, bool expected) =>
        Assert.Equal(expected, MudPlay.Game.SuicidePasswordTracker.IsSetSuicide(line));

    [Fact]
    public void Withheld_KeepsNothingAndOnlyCounts()
    {
        bool withhold = true;
        SentCommandTrail trail = new() { Withhold = () => withhold };

        trail.ObserveTyped(Encoding.Latin1.GetBytes("my-login-name\r"));
        trail.Record("hunter2", SentCommandTrail.Origin.Engine, "Suicide", SentCommandTrail.Outcome.Sent);
        withhold = false;
        trail.Record("look", SentCommandTrail.Origin.Typed, null, SentCommandTrail.Outcome.Sent);

        SentCommandTrail.Entry entry = Assert.Single(trail.Snapshot());
        Assert.Equal("look", entry.Text);
        Assert.Equal(2, trail.Withheld);
    }

    [Fact]
    public void Ring_KeepsTheNewestCapacityEntriesInOrder()
    {
        SentCommandTrail trail = new();
        for (int i = 0; i < SentCommandTrail.Capacity + 10; i++)
            trail.Record($"c{i}", SentCommandTrail.Origin.Engine, "E", SentCommandTrail.Outcome.Sent);

        SentCommandTrail.Entry[] entries = trail.Snapshot();
        Assert.Equal(SentCommandTrail.Capacity, entries.Length);
        Assert.Equal("c10", entries[0].Text);
        Assert.Equal($"c{SentCommandTrail.Capacity + 9}", entries[^1].Text);
    }

    [Fact]
    public void ReportSection_PrintsOriginOutcomeAndReasonAndCensorsOnlyChatCommands()
    {
        var (gate, trail, _) = Setup();
        BugReportChatCensor censor = StandInCensor.Of("flibber");
        gate.Named(gate.WrapEngineSender(_ => { }), "Combat")(Line("a flibber"));
        trail.ObserveTyped(Encoding.Latin1.GetBytes("gos you flibber\r"));
        gate.Hold("SuicidePassword");
        gate.Named(gate.WrapEngineSender(_ => { }), "PartyPoller")(Line("par"));

        BugReportBuilder.Section section = BugReportSentCommands.Build(trail, censor);

        Assert.Equal(3, section.Entries!.Count);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d  engine Combat +sent     a flibber$", section.Entries[0]);
        Assert.Matches(@"  typed +sent     gos you \[censored\]$", section.Entries[1]);
        Assert.EndsWith("DROPPED  par    (send gate held: SuicidePassword)", section.Entries[2]);
    }
}
