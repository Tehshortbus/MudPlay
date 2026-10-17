using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Coverage for the @suicide remote-command consumer. The interesting
// behaviours: sends both commands when a password is stored, replies
// to the original sender on invalid response, and ignores invalid
// lines that don't belong to one of our pending invocations.
public sealed class SuicideHandlerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public ProfileService Profile { get; }
        public PasswordProtector Protector { get; }
        public PartyState Party { get; } = new();
        public PlayerState PlayerState { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public WirePromptScanner PromptScanner { get; } = new();
        public RemoteCommandManager Engine { get; }
        public SuicideHandler Handler { get; }
        // The clock the Handler reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> HandlerNow { get; set; } = () => DateTimeOffset.UtcNow;
        public List<byte[]> Wire { get; } = new();
        public string TmpDir { get; }

        public Harness(int currentLives = 9)
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            // ChatRouter is required for RemoteCommandManager to receive
            // telepath input.
            ChatRouter chat = new(Router);
            Profile = TestProfiles.Service();
            Profile.LoadDefaultProfile();
            TmpDir = Path.Combine(Path.GetTempPath(), "fterm-suicide-handler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TmpDir);
            Protector = new PasswordProtector(Path.Combine(TmpDir, ".credkey"));
            Engine = new RemoteCommandManager(chat, Party, Players, Log)
            {
                LivesProvider = () => currentLives,
                MaxSuicideLivesThreshold = 3,
            };
            Engine.SetWireSender(Wire.Add);
            Handler = new SuicideHandler(Engine, Router, Profile, Protector, PromptScanner, Log, now: () => HandlerNow());
            Handler.SetWireSender(Wire.Add);
        }

        public void Dispose()
        {
            Handler.Dispose();
            Engine.Dispose();
            Directory.Delete(TmpDir, recursive: true);
        }
    }

    // The game's next prompt, as the wire delivers it.
    private static void NextPrompt(Harness h) =>
        h.PromptScanner.Append(Encoding.Latin1.GetBytes("[HP=100]: "));

    private static void DispatchTelepath(MessageRouter router, string from, string body)
    {
        string line = $"{from} telepaths: {body}";
        router.Dispatch(new LineExtractor.EmittedLine(
            line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
    }

    private static void DispatchLine(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false));

    private static void GrantElevated(PlayerDatabase db, string name)
    {
        db.RecordObservation(name, null, null, null, null, null, null, DateTime.UtcNow);
        db.EditCustomization(name, new PlayerCustomization(
            RemoteControls: PlayerRemoteControls.SysopCommands));
    }

    [Fact]
    public void SuicideWithStoredPassword_SendsCommandAndPassword()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        // Only the command goes out until the game asks for the password.
        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.Contains("suicide\r", joined);
        Assert.DoesNotContain("hunter2", joined);
        Assert.True(h.Handler.AwaitingPasswordPrompt);

        // A row that isn't the prompt sends nothing; the prompt sends the password.
        h.Handler.ObserveCursorRow("[HP=100/MA=50]:suicide");
        Assert.DoesNotContain("hunter2",
            string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b))));
        h.Handler.ObserveCursorRow("Enter your suicide password:");
        Assert.Equal("hunter2\r", Encoding.Latin1.GetString(h.Wire[^1]));
        Assert.False(h.Handler.AwaitingPasswordPrompt);
    }

    [Fact]
    public void StoredPassword_PromptAfterAStatlineOnTheSameRow_SendsPassword()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow("[HP=100/MA=50]:Enter your suicide password:");

        Assert.Equal("hunter2\r", Encoding.Latin1.GetString(h.Wire[^1]));
    }

    // The statline is the user's own (Settings -> Statline), so where it ends on
    // the row is asked of the prompt scanner's pattern, not of an "[HP=" shape.
    [Fact]
    public void StoredPassword_PromptAfterACustomStatline_SendsPassword()
    {
        using Harness h = new();
        h.PromptScanner.InstallRegex(StatlinePromptRegexBuilder.Build("full custom <%h hp / %m ma>%r "));
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow("<100 hp / 50 ma> suicide");
        Assert.True(h.Handler.AwaitingPasswordPrompt);
        h.Handler.ObserveCursorRow("<100 hp / 50 ma> Enter your suicide password:");

        Assert.Equal("hunter2\r", Encoding.Latin1.GetString(h.Wire[^1]));
        Assert.False(h.Handler.AwaitingPasswordPrompt);
    }

    // A statline the configured pattern doesn't read is not known to be the
    // game's: the password stays unsent and the wait runs out.
    [Fact]
    public void StoredPassword_PromptAfterAStatlineTheSettingDoesNotDescribe_SendsNothingAndGivesUp()
    {
        using Harness h = new();
        h.PromptScanner.InstallRegex(StatlinePromptRegexBuilder.Build("full custom <%h hp / %m ma>%r "));
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");
        DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.HandlerNow = () => now;

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();
        h.Handler.ObserveCursorRow("[HP=100/MA=50]:Enter your suicide password:");
        Assert.Empty(h.Wire);
        Assert.True(h.Handler.AwaitingPasswordPrompt);

        now += SuicideHandler.PromptWait + TimeSpan.FromSeconds(1);
        h.Handler.CheckTimeout();

        Assert.False(h.Handler.AwaitingPasswordPrompt);
        Assert.Equal("/Trusted {suicide failed, the game did not ask for the password}\r",
            Encoding.Latin1.GetString(Assert.Single(h.Wire)));
    }

    [Theory]
    [InlineData("Bob telepaths: ]: Enter your suicide password:")]
    [InlineData("Bob telepaths: Enter your suicide password:")]
    [InlineData("Bob gossips: [HP=1/MA=1]:Enter your suicide password:")]
    [InlineData("You hear: Enter your suicide password:")]
    [InlineData("[HP=100/MA=50]:Enter your suicide password: now")]
    [InlineData("Enter your suicide password: please")]
    public void StoredPassword_ThePromptsWordsInsideAnythingElse_SendNothing(string row)
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow(row);

        Assert.DoesNotContain(h.Wire, b => Encoding.Latin1.GetString(b).Contains("hunter2"));
        Assert.True(h.Handler.AwaitingPasswordPrompt);
    }

    // The telepath pacer writes from its own timer, past the send gate. Wired as
    // the client wires it: everything the handler and the remote-command engine
    // send goes to the pacer, and the pacer writes to the socket.
    private sealed class Paced : IDisposable
    {
        public Harness H { get; } = new();
        public TelepathPacer Pacer { get; }
        public List<string> Socket { get; } = new();
        public DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        private readonly List<Action> _timers = new();

        public Paced(string password = "hunter2")
        {
            Pacer = new TelepathPacer((_, action) => _timers.Add(action), () => Now);
            Pacer.SetWriter(b => Socket.Add(Encoding.Latin1.GetString(b)));
            H.Handler.SetWireSender(Pacer.Send);
            H.Engine.SetWireSender(Pacer.Send);
            H.Handler.Telepaths = Pacer;
            H.HandlerNow = () => Now;
            GrantElevated(H.Players, "Trusted");
            H.Profile.Current!.EncryptedSuicidePassword = H.Protector.Protect(password);
        }

        // Let time pass and every pacing timer that was waiting fire.
        public void Advance(TimeSpan by)
        {
            Now += by;
            Action[] due = _timers.ToArray();
            _timers.Clear();
            foreach (Action timer in due) timer();
        }

        // Two telepaths in a burst: the first goes now, the second waits its gap.
        public void QueueATelepathBehindItsGap()
        {
            Pacer.Send(Encoding.Latin1.GetBytes("/Bob first\r/Bob queued\r"));
            Assert.Equal(new[] { "/Bob first\r" }, Socket);
        }

        public void Dispose() => H.Dispose();
    }

    // A telepath that lands between `suicide` and the password is what the game
    // takes for the password, and the stored one then goes to the statline in
    // clear. One already queued stays queued until the password is on the wire.
    [Fact]
    public void StoredPassword_ATelepathQueuedBeforeTheCommand_IsNotWrittenUntilThePasswordIs()
    {
        using Paced p = new();
        p.QueueATelepathBehindItsGap();

        DispatchTelepath(p.H.Router, "Trusted", "@suicide");
        Assert.True(p.Pacer.Held);
        p.Advance(TimeSpan.FromSeconds(1));
        p.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "/Bob first\r", "suicide\r" }, p.Socket);

        p.H.Handler.ObserveCursorRow("Enter your suicide password:");

        Assert.Equal(new[] { "/Bob first\r", "suicide\r", "hunter2\r", "/Bob queued\r" }, p.Socket);
        Assert.False(p.Pacer.Held);
    }

    // A refused telepath's resend is a write from the pacer too.
    [Fact]
    public void StoredPassword_AResendOwedDuringTheWait_IsHeldLikeTheRest()
    {
        using Paced p = new();
        p.Pacer.Send(Encoding.Latin1.GetBytes("/Bob first\r"));

        DispatchTelepath(p.H.Router, "Trusted", "@suicide");
        p.Advance(TimeSpan.FromSeconds(1));
        p.Pacer.OnAck("--- Telepath Not Sent ---");
        p.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "/Bob first\r", "suicide\r" }, p.Socket);

        p.H.Handler.ObserveCursorRow("Enter your suicide password:");

        Assert.Equal(new[] { "/Bob first\r", "suicide\r", "hunter2\r", "/Bob first\r" }, p.Socket);
    }

    // However the wait ends, the pacer is let go and the telepath goes out; the
    // password goes out only when the prompt was answered.
    [Theory]
    [InlineData("refused")]
    [InlineData("timed out")]
    [InlineData("another line sent")]
    [InlineData("password gone")]
    [InlineData("handler disposed")]
    public void StoredPassword_TheWaitEndingAnyWay_LetsTheQueuedTelepathGo(string ending)
    {
        using Paced p = new();
        p.QueueATelepathBehindItsGap();
        DispatchTelepath(p.H.Router, "Trusted", "@suicide");
        p.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("/Bob queued\r", p.Socket);

        switch (ending)
        {
            case "refused":
                DispatchLine(p.H.Router, "You may not suicide or reroll in this room!");
                break;
            case "timed out":
                p.Now += SuicideHandler.PromptWait + TimeSpan.FromSeconds(1);
                p.H.Handler.CheckTimeout();
                break;
            case "another line sent":
                p.H.Handler.NoteLineSent("look\r"u8);
                break;
            case "password gone":
                p.H.Profile.Current!.EncryptedSuicidePassword = null;
                p.H.Handler.ObserveCursorRow("Enter your suicide password:");
                break;
            case "handler disposed":
                p.H.Handler.Dispose();
                break;
        }
        p.Advance(TimeSpan.FromSeconds(1));

        Assert.False(p.Pacer.Held);
        Assert.False(p.H.Handler.AwaitingPasswordPrompt);
        Assert.Contains("/Bob queued\r", p.Socket);
        Assert.DoesNotContain("hunter2\r", p.Socket);
    }

    // A password that looks like a telepath is still the answer to the prompt:
    // it is written at once, ahead of what is queued, never paced behind it.
    [Fact]
    public void StoredPassword_ThatBeginsWithASlash_IsWrittenAtOnce_AheadOfQueuedTelepaths()
    {
        using Paced p = new(password: "/placeholder pw");
        p.QueueATelepathBehindItsGap();

        DispatchTelepath(p.H.Router, "Trusted", "@suicide");
        p.Advance(TimeSpan.FromSeconds(1));
        p.H.Handler.ObserveCursorRow("Enter your suicide password:");

        Assert.Equal(new[] { "/Bob first\r", "suicide\r", "/placeholder pw\r", "/Bob queued\r" }, p.Socket);
        // The two telepaths await their acknowledgements; the password is no telepath.
        Assert.Equal(2, p.Pacer.InFlight);
    }

    // A stored password is sent whole, however long: nothing here counts on a
    // length. What the game echoes for it differs by realm (Paradigm one star a
    // character, Stock's echo call has a width of 8); either row is just the
    // answered prompt, and nothing more is sent for it.
    [Theory]
    [InlineData(20)]   // Paradigm: a star for each character sent
    [InlineData(8)]    // Stock: echsec('*', 8)
    public void StoredPassword_LongerThanEightCharacters_IsSentWhole_AndItsEchoSendsNothingMore(int starsEchoed)
    {
        const string placeholder = "placeholder-pw-20-ch";
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect(placeholder);

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow("Enter your suicide password:");
        Assert.Equal(placeholder + "\r", Encoding.Latin1.GetString(h.Wire[^1]));

        int sent = h.Wire.Count;
        h.Handler.ObserveCursorRow("Enter your suicide password:" + new string('*', starsEchoed));
        Assert.Equal(sent, h.Wire.Count);
        Assert.False(h.Handler.AwaitingPasswordPrompt);
    }

    // The echo standing on the row while a later @suicide waits is not the
    // prompt: the row must be the prompt alone.
    [Theory]
    [InlineData(20)]
    [InlineData(8)]
    public void StoredPassword_ARowStillShowingAnEarlierEcho_IsNotThePrompt(int starsEchoed)
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("placeholder-pw-20-ch");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow("Enter your suicide password:" + new string('*', starsEchoed));

        Assert.DoesNotContain(h.Wire, b => Encoding.Latin1.GetString(b).Contains("placeholder"));
        Assert.True(h.Handler.AwaitingPasswordPrompt);
    }

    // More of the same: a statline with a player's line after it, a bracketed
    // word standing in for one, and our own say quoting the prompt.
    [Theory]
    [InlineData("Bob gossips: [x]: Enter your suicide password: lol")]
    [InlineData("[HP=100/MA=50]:Bob telepaths: ]:Enter your suicide password:")]
    [InlineData("You say \"]: Enter your suicide password:\"")]
    public void StoredPassword_ThePromptQuotedAfterAStatlineOrInOurOwnSay_SendsNothing(string row)
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Handler.ObserveCursorRow(row);

        Assert.DoesNotContain(h.Wire, b => Encoding.Latin1.GetString(b).Contains("hunter2"));
        Assert.True(h.Handler.AwaitingPasswordPrompt);
    }

    [Fact]
    public void StoredPassword_TheSendGateIsHeldFromTheCommandUntilThePasswordIsSent()
    {
        using Harness h = new();
        EngineSendGate gate = new();
        h.Handler.SendGate = gate;
        List<string> engineWire = new();
        Action<byte[]> engine = gate.WrapEngineSender(b => engineWire.Add(Encoding.Latin1.GetString(b)));
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        // In the window an engine's command never reaches the game, where it
        // would be read as the password.
        Assert.True(gate.IsLocked);
        engine(Encoding.Latin1.GetBytes("par\r"));
        Assert.Empty(engineWire);

        h.Handler.ObserveCursorRow("Enter your suicide password:");

        Assert.False(gate.IsLocked);
        engine(Encoding.Latin1.GetBytes("par\r"));
        Assert.Equal("par\r", Assert.Single(engineWire));
    }

    [Fact]
    public void StoredPassword_AnotherLineSentInTheWindow_CancelsAndThePasswordNeverFollows()
    {
        using Harness h = new();
        EngineSendGate gate = new();
        h.Handler.SendGate = gate;
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();
        // A key on its own is not a line; the line the user then sends is.
        h.Handler.NoteLineSent("l"u8);
        Assert.True(h.Handler.AwaitingPasswordPrompt);
        h.Handler.NoteLineSent("look\r"u8);

        Assert.False(h.Handler.AwaitingPasswordPrompt);
        Assert.False(gate.IsLocked);
        Assert.Equal("/Trusted {suicide cancelled, another command went out first}\r",
            Encoding.Latin1.GetString(Assert.Single(h.Wire)));

        // The prompt now on the row was answered by that line: nothing more goes out.
        h.Wire.Clear();
        h.Handler.ObserveCursorRow("Enter your suicide password:");
        Assert.Empty(h.Wire);
    }

    [Theory]
    [InlineData("You may not suicide or reroll during a tournament.")]
    [InlineData("You may not suicide or reroll in this room!")]
    public void StoredPassword_TheGamesRefusal_EndsTheWaitAtOnce(string refusal)
    {
        using Harness h = new();
        EngineSendGate gate = new();
        h.Handler.SendGate = gate;
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();
        DispatchLine(h.Router, refusal);

        Assert.False(h.Handler.AwaitingPasswordPrompt);
        Assert.False(gate.IsLocked);
        Assert.Equal("/Trusted {suicide refused by the game}\r", Encoding.Latin1.GetString(Assert.Single(h.Wire)));
    }

    [Fact]
    public void StoredPassword_Timeout_ReleasesTheSendGate()
    {
        using Harness h = new();
        EngineSendGate gate = new();
        h.Handler.SendGate = gate;
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");
        DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.HandlerNow = () => now;

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        Assert.True(gate.IsLocked);
        now += SuicideHandler.PromptWait + TimeSpan.FromSeconds(1);
        NextPrompt(h);

        Assert.False(gate.IsLocked);
        Assert.DoesNotContain(h.Wire, b => Encoding.Latin1.GetString(b).Contains("hunter2"));
    }

    [Fact]
    public void StoredPassword_PromptNeverComes_PasswordIsNeverSentAndSenderIsTold()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");
        DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.HandlerNow = () => now;

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        // Past the wait, even the prompt itself no longer draws the password.
        now += SuicideHandler.PromptWait + TimeSpan.FromSeconds(1);
        h.Handler.ObserveCursorRow("Enter your suicide password:");

        byte[] sent = Assert.Single(h.Wire);
        Assert.Equal("/Trusted {suicide failed, the game did not ask for the password}\r",
            Encoding.Latin1.GetString(sent));
        Assert.False(h.Handler.AwaitingPasswordPrompt);
    }

    [Fact]
    public void StoredPassword_PromptNeverComes_WarnOnDenialOff_StaysSilent()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Engine.WarnOnDenial = false;
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");
        DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.HandlerNow = () => now;

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();
        now += SuicideHandler.PromptWait + TimeSpan.FromSeconds(1);
        NextPrompt(h);

        Assert.Empty(h.Wire);
        Assert.False(h.Handler.AwaitingPasswordPrompt);
    }

    [Fact]
    public void StoredPassword_SecondAttemptWhileWaiting_IsRefusedAndSendsNoSecondCommand()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();
        DispatchTelepath(h.Router, "Trusted", "@suicide");

        byte[] sent = Assert.Single(h.Wire);
        Assert.Equal("/Trusted {@suicide already in-flight, try again shortly}\r",
            Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void NoStoredPassword_SendsProFirst_NotSuicideImmediately()
    {
        // Updated flow: with no stored password, we don't know
        // whether the realm has a password set. Type `pro` first
        // and disambiguate from the response. If we sent bare
        // `suicide` straight away and the realm DID have a password,
        // we'd hang at the "Enter your suicide password:" prompt
        // with no answer.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        // First wire-send from the handler is `pro\r`; suicide must
        // NOT be on the wire yet (waiting on `pro` response).
        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.Contains("pro\r", joined);
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void NoStoredPw_RealmConfirmsNotSet_SendsSuicideAtNextStatline()
    {
        // pro pre-check: server response includes "You do not have a
        // suicide password set." before the next statline arrives.
        // Decision point is the NEXT prompt observation (which
        // brackets pro output per the Playpen wire trace) — at that
        // point _seenNotSetInProWindow is true, fire suicide.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();   // drop the `pro` send

        // Server response: "not set" line during the window...
        DispatchLine(h.Router, "You do not have a suicide password set.");
        // ...then the next statline closes the window.
        NextPrompt(h);

        byte[] sent = Assert.Single(h.Wire);
        Assert.Equal("suicide\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void NoStoredPw_NextStatlineWithoutNotSet_RepliesMismatch()
    {
        // pro output doesn't include "not set" → realm has a password
        // we don't have stored. Mismatch — telepath sender with the
        // user-specified reply text.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        // Pro output arrives + next statline closes the window
        // without "not set" ever appearing.
        NextPrompt(h);

        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Contains("/Trusted", text);
        Assert.Contains("Suicide failed, password set in game but not stored.", text);
    }

    [Fact]
    public void NoStoredPw_NextStatlineWithWarnOnDenialOff_StaysSilent()
    {
        using Harness h = new();
        h.Engine.WarnOnDenial = false;
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        NextPrompt(h);

        Assert.Empty(h.Wire);
    }

    [Fact]
    public void NoStoredPw_SecondAttemptDuringProCheck_Refused()
    {
        // Second @suicide arrives while the pro window is open from
        // the first one — refuse the second defensively so we don't
        // double-send `pro` or end up in tangled state.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        GrantElevated(h.Players, "AlsoTrusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        DispatchTelepath(h.Router, "AlsoTrusted", "@suicide");

        // Second invocation gets a denial reply (single send),
        // no extra `pro` on the wire.
        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Contains("/AlsoTrusted", text);
        Assert.Contains("already in-flight", text);
    }

    [Fact]
    public void NoStoredPw_NotSetLineOutsidePending_NotAReply()
    {
        // User typed `pro` manually (no @suicide pending). The
        // "not set" line fires but we shouldn't send suicide on the
        // wire — only @suicide-initiated checks should drive that.
        using Harness h = new();
        // No DispatchTelepath @suicide call — handler has no pending state.

        DispatchLine(h.Router, "You do not have a suicide password set.");

        // No `suicide\r` from the handler.
        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("suicide\r"));
    }

    [Fact]
    public void InvalidResponse_TelepathsBackToSender()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("wrongpw");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        // Server responds with the invalid line.
        DispatchLine(h.Router, "Invalid password specified.");

        // Handler should have telepathed the sender with the canonical
        // failure body wrapped in braces.
        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Equal("/Trusted {invalid suicide password is stored, unable}\r", text);
    }

    [Fact]
    public void InvalidResponse_WhenWarnOnDenialOff_StaysSilent()
    {
        // Per the engine-wide reply policy: WarnOnDenial gates ALL
        // invalid / denial replies including specific-reason ones
        // emitted by handlers. With the flag off, the
        // invalid-password telepath is suppressed.
        using Harness h = new();
        h.Engine.WarnOnDenial = false;
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("wrongpw");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        DispatchLine(h.Router, "Invalid password specified.");

        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("invalid suicide password is stored"));
    }

    [Fact]
    public void InvalidResponseWithNoPendingInvocation_IsIgnored()
    {
        // A "Invalid password specified." line from the user's own
        // manual `suicide` attempt mustn't trigger a reply (no @suicide
        // was issued, no _pendingReply captured).
        using Harness h = new();
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("foo");

        DispatchLine(h.Router, "Invalid password specified.");

        // No telepath was queued because nothing was pending.
        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("invalid suicide password is stored"));
    }

    [Fact]
    public void UnauthorisedSender_IsDeniedByEngine_NoCommandSent()
    {
        // Sender has no permissions on the Players-tab row → engine
        // refuses to invoke the handler. We should observe NO
        // "suicide" command on the wire from the handler itself.
        using Harness h = new();
        // Don't grant Elevated to this player.
        h.Players.RecordObservation("Stranger", null, null, null, null, null, null, DateTime.UtcNow);

        DispatchTelepath(h.Router, "Stranger", "@suicide");

        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void HardBlockAtLowLives_PreventsInvocation()
    {
        // Lives ≤ MaxSuicideLivesThreshold = engine refuses the
        // invocation even with Elevated permission.
        using Harness h = new(currentLives: 2);   // threshold 3 default
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("foo");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void Catalog_SuicideMappedToSysopCommands()
    {
        // Pin the catalog mapping so a future refactor that
        // accidentally drops @suicide or moves it to a different
        // category gets caught.
        Assert.True(RemoteCommandCatalog.TryGetCategory("@suicide", out PlayerRemoteControls cat));
        Assert.Equal(PlayerRemoteControls.SysopCommands, cat);
    }
}
