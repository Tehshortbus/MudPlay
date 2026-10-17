using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Recovery;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services.Patterns;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Another player's corpse recovered with their password: the record built from the
// recovery's lines, the gives that hand it back, and the password kept out of
// everything the client stores.
public sealed class CorpseHandoffTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private const string Password = "hunter2xyz";

    private sealed class Harness
    {
        public ProfileService Profile { get; } = TestProfiles.Service();
        public LogService Log { get; } = new();
        public CorpseHandoffManager Manager { get; }
        public List<string> Sent { get; } = new();
        public List<(string Command, Func<string, bool> MayGo)> Queue { get; } = new();
        public List<Action> Timers { get; } = new();
        public int Recalled { get; private set; }
        public bool Paradigm { get; set; } = true;
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public Harness()
        {
            Profile.LoadDefaultProfile();
            Profile.Current!.Name = "Fujin";
            CurrencyNaming naming = new();
            Manager = new CorpseHandoffManager(
                Profile,
                selfName: () => "Fujin",
                currentRoom: () => (new RoomRef(1, 224), "Darkwood Forest"),
                canonicalCurrency: naming.Canonicalize,
                isParadigm: () => Paradigm,
                runicName: () => naming.RunicName,
                sendPaced: (commands, _, mayGo) =>
                {
                    foreach (string c in commands) Queue.Add((c, mayGo));
                },
                recallPaced: _ => { Recalled++; Queue.Clear(); },
                scheduleAfter: (_, action) => Timers.Add(action),
                log: Log,
                now: () => Now);
        }

        // The pacer letting every queued give go.
        public void Pump()
        {
            foreach ((string command, Func<string, bool> mayGo) in Queue.ToList())
                if (mayGo(command)) Sent.Add(command);
            Queue.Clear();
        }

        public void FireTimers()
        {
            foreach (Action a in Timers.ToList()) a();
        }

        public void Recover(string owner, params string[] items)
        {
            Manager.ObserveCommand($"recover corpse {owner.ToLowerInvariant()} {Password}", usersOwn: true);
            Manager.OnLine($"You begin to pick through the corpse of {owner}...");
            Manager.NoteCoinsPicked("platinum", 51);
            Manager.NoteCoinsPicked("gold", 53);
            foreach (string item in items) Manager.OnLine($"You took {item}.");
            Manager.OnLine($"You have recovered the corpse of {owner}.");
        }
    }

    // ----- the recovery lines -------------------------------------------

    [Fact]
    public void Lines_ParseTheRecoverySequence()
    {
        Assert.Equal("Raijin", CorpseRecoveryLines.Begun("You begin to pick through the corpse of Raijin..."));
        Assert.Equal("Raijin", CorpseRecoveryLines.Completed("You have recovered the corpse of Raijin."));
        Assert.Equal("black star key", CorpseRecoveryLines.Took("You took black star key."));
        Assert.Null(CorpseRecoveryLines.Took("You took 12 damage."));
        Assert.Null(CorpseRecoveryLines.Begun("You notice corpse of Raijin here."));
    }

    [Theory]
    [InlineData("recover corpse raijin secret", true, "raijin", "secret")]
    [InlineData("RECOVER CORPSE Raijin secret", true, "Raijin", "secret")]
    [InlineData("recover corpse raijin two words", false, "", "")]           // a password is one word
    [InlineData("recover corpse raijin\nsecret", false, "", "")]
    [InlineData("recover corpse raijin", false, "", "")]
    [InlineData("recover raijin secret", false, "", "")]
    public void Command_ParsesNameAndPassword(string command, bool ok, string name, string password)
    {
        Assert.Equal(ok, CorpseRecoveryLines.TryParseRecoverWithPassword(command, out string n, out string p));
        Assert.Equal(name, n);
        Assert.Equal(password, p);
    }

    [Fact]
    public void Command_RedactedAndStrippedOfThePassword()
    {
        Assert.Equal("recover corpse raijin [redacted]", CorpseRecoveryLines.Redact($"recover corpse raijin {Password}"));
        Assert.Equal("recover corpse raijin", CorpseRecoveryLines.WithoutPassword($"recover corpse raijin {Password}"));
        Assert.Equal("recover corpse raijin", CorpseRecoveryLines.Redact("recover corpse raijin"));
        Assert.Equal("look", CorpseRecoveryLines.Redact("look"));
        Assert.Equal("@do from Raijin: 'recover corpse raijin [redacted]",
            CorpseRecoveryLines.Redact("@do from Raijin: 'recover corpse raijin pa'ss'"));
    }

    [Fact]
    public void Redact_StaysOnItsOwnLine()
    {
        const string withoutPassword = "12:00:01 recover corpse Aldric\n12:00:02 You begin to pick through the corpse of Aldric...";
        Assert.Equal(withoutPassword, CorpseRecoveryLines.Redact(withoutPassword));
        Assert.Equal("recover corpse Aldric\r\nYou begin to pick through the corpse of Aldric...",
            CorpseRecoveryLines.Redact("recover corpse Aldric\r\nYou begin to pick through the corpse of Aldric..."));

        const string withPassword = "recover corpse Aldric secret\nYou begin to pick through the corpse of Aldric...";
        Assert.Equal("recover corpse Aldric [redacted]\nYou begin to pick through the corpse of Aldric...",
            CorpseRecoveryLines.Redact(withPassword));

        BugReportScrub.Scrubber scrub = BugReportScrub.Build([], Array.Empty<string?>());
        Assert.Equal(withoutPassword, scrub.Apply(withoutPassword));
    }

    [Fact]
    public void OurOwnLogLines_AreNotMasked()
    {
        Harness h = new();
        h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        h.Manager.ObserveCommand($"recover corpse aldric {Password}", usersOwn: true);
        h.Manager.ObserveCommand($"recover corpse fujin {Password}", usersOwn: true);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Message.Contains(BugReportScrub.Mask));
        Assert.Contains(h.Log.Snapshot(), e => e.Message == "aldric's corpse: recovery sent, waiting for its lines");
    }

    // ----- recording -----------------------------------------------------

    [Fact]
    public void FriendsCorpse_RecordsEveryItemAndTheCoins()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "rope and grapple", "darkwood ring", "darkwood ring");

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal("Raijin", r.Owner);
        Assert.Equal("Darkwood Forest", r.RoomName);
        Assert.Equal(new[] { "thorned whip", "rope and grapple", "2 darkwood ring" }, r.Items.Select(i => i.ToString()));
        Assert.Equal(51, r.Coins.Platinum);
        Assert.Equal(53, r.Coins.Gold);
        Assert.Contains(h.Log.Snapshot(), e => e.Message == "recorded 4 item(s) from Raijin's corpse and 51 platinum pieces, 53 gold crowns");
    }

    [Fact]
    public void OwnCorpse_IsNeverRecorded()
    {
        Harness h = new();
        h.Recover("Fujin", "thorned whip");
        Assert.Empty(h.Manager.Records);
    }

    [Fact]
    public void RecoveryWithoutOurCommand_IsNotRecorded()
    {
        Harness h = new();
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");
        Assert.Empty(h.Manager.Records);
    }

    [Fact]
    public void ArmRunsOut_AndAnotherNamesCorpseIsNotTaken()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.OnLine("You begin to pick through the corpse of Nineteen...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Nineteen.");
        Assert.Empty(h.Manager.Records);

        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Now += CorpseHandoffManager.ArmWindow + TimeSpan.FromSeconds(1);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");
        Assert.Empty(h.Manager.Records);
    }

    [Fact]
    public void SeveralCorpsesInARow_AreEachRecorded()
    {
        // `recover corpse raijin p1;recover corpse ermias p2`: both go out before
        // either recovery prints.
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.ObserveCommand("recover corpse ermias otherpass", usersOwn: true);
        foreach (string owner in new[] { "Raijin", "Ermias" })
        {
            h.Manager.OnLine($"You begin to pick through the corpse of {owner}...");
            h.Manager.OnLine($"You took {owner.ToLowerInvariant()}'s sword.");
            h.Manager.OnLine($"You have recovered the corpse of {owner}.");
        }
        Assert.Equal(new[] { "Ermias", "Raijin" }, h.Manager.Records.Select(r => r.Owner).OrderBy(o => o));
    }

    [Fact]
    public void ACommandTypedMidBurst_DoesNotCutTheRecordShort()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.ObserveCommand("recover corpse ermias otherpass", usersOwn: true);
        h.Manager.OnLine("You took rope and grapple.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal(new[] { "thorned whip", "rope and grapple" }, r.Items.Select(i => i.Name));
    }

    [Fact]
    public void OurOwnAutoRecovery_DoesNotUseUpAFriendsArm()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.OnLine("You begin to pick through the corpse of Fujin...");
        h.Manager.OnLine("You took my own sword.");
        h.Manager.OnLine("You have recovered the corpse of Fujin.");
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal("Raijin", r.Owner);
        Assert.Equal(new[] { "thorned whip" }, r.Items.Select(i => i.Name));
    }

    [Fact]
    public void AMissedClosingLine_EndsTheRecordAfterFiveSeconds()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.FireTimers();                                   // CloseWindow passes
        h.Manager.OnLine("You took my own loot.");        // our own pickup later on
        h.Manager.NoteCoinsPicked("gold", 7);

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal(new[] { "thorned whip" }, r.Items.Select(i => i.Name));
        Assert.Equal(0, r.Coins.Gold);
        Assert.Contains("closing line", r.Note);
    }

    [Fact]
    public void ADisconnect_KeepsWhatTheRecordingHadSoFar()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: true);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.NotifyDisconnected();
        Assert.Equal("thorned whip", Assert.Single(Assert.Single(h.Manager.Records).Items).Name);
    }

    [Fact]
    public void ACommandNotTypedByTheUser_DoesNotArm_ButItsPasswordIsMasked()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}", usersOwn: false);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");
        Assert.Empty(h.Manager.Records);
        Assert.Contains(Password, h.Manager.SessionPasswords);
    }

    [Fact]
    public void ARelayedDo_LogsTheCommandWithItsPasswordMasked()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        LogService log = new();
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players, log);
        _routers[engine] = router;
        DoHandler handler = new(engine, log);
        List<string> wire = new();
        handler.SetWireSender(b => wire.Add(Encoding.Latin1.GetString(b)));
        DateTime now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
        players.RecordObservation("Raijin", null, null, null, null, null, null, now);
        players.EditCustomization("Raijin", new PlayerCustomization(RemoteControls: PlayerRemoteControls.ExecuteCommands));

        string said = $"@do recover corpse raijin {Password}";
        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.TelepathIncoming, "Raijin", said));

        Assert.Equal($"recover corpse raijin {Password}\r", Assert.Single(wire));
        Assert.Contains(log.Snapshot(), e => e.Message.Contains("recover corpse raijin [redacted]"));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.Contains(Password) || (e.Context?.Contains(Password) ?? false));
        Assert.DoesNotContain(Password, SessionLogService.FormatChatLine(
            new ChatLogEntry(now, ChatChannel.TelepathIncoming, "Raijin", said, $"Raijin telepaths: {said}")));
    }

    [Theory]
    [InlineData("recover corpse raijin " + Password, "recover corpse raijin")]
    [InlineData("look;recover corpse raijin " + Password, "look;recover corpse raijin")]
    [InlineData("recover corpse raijin " + Password + ";n", "recover corpse raijin;n")]
    [InlineData("look;n", "look;n")]
    public void History_DropsThePasswordFromEveryStep(string typed, string kept)
    {
        CommandHistory history = new();
        history.Record(typed);
        Assert.Equal(kept, Assert.Single(history.Entries));
    }

    // ----- the password ---------------------------------------------------

    [Fact]
    public void Password_NeverReachesTheRecordTheLogOrTheTrail()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");

        string stored = JsonSerializer.Serialize(h.Profile.Current);
        Assert.DoesNotContain(Password, stored);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Message.Contains(Password));
        Assert.Contains(Password, h.Manager.SessionPasswords);

        SentCommandTrail trail = new();
        string? told = null;
        trail.TypedLineEntered = line => told = line;
        trail.ObserveTyped(Encoding.Latin1.GetBytes($"recover corpse raijin {Password}\r"));
        trail.Record($"recover corpse raijin {Password}", SentCommandTrail.Origin.Engine, "Macro", SentCommandTrail.Outcome.Sent);
        Assert.All(trail.Snapshot(), e => Assert.Equal("recover corpse raijin [redacted]", e.Text));
        Assert.Equal("recover corpse raijin [redacted]", told);

        CommandHistory history = new();
        history.Record($"recover corpse raijin {Password}");
        Assert.Equal("recover corpse raijin", Assert.Single(history.Entries));
    }

    [Fact]
    public void Password_IsMaskedByTheBugReportScrubber()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.CorpsePasswordLabel, Password)], Array.Empty<string?>());
        Assert.DoesNotContain(Password, scrubber.Apply($"[HP=50]:recover corpse raijin {Password}"));
    }

    // ----- the gives -------------------------------------------------------

    [Fact]
    public void Plan_MergesNamesAndCountsOnParadigm()
    {
        IReadOnlyList<CorpseHandoffPlan.Give> gives = CorpseHandoffPlan.Build(
            [new("darkwood ring", 1), new("Darkwood ring", 1), new("thorned whip", 1)],
            CurrencyHoldings.Empty with { Runic = 2, Gold = 53 }, "Raijin", paradigm: true, runicName: "runic");
        Assert.Equal(new[]
        {
            "give 2 darkwood ring to Raijin",
            "give thorned whip to Raijin",
            "give 2 runic to Raijin",
            "give 53 gold to Raijin",
        }, gives.Select(g => g.Command));
    }

    [Fact]
    public void Plan_GivesOneCopyACommandOnStock()
    {
        IReadOnlyList<CorpseHandoffPlan.Give> gives = CorpseHandoffPlan.Build(
            [new("torch", 2)], CurrencyHoldings.Empty, "Bob", paradigm: false, runicName: "runic");
        Assert.Equal(new[] { "give torch to Bob", "give torch to Bob" }, gives.Select(g => g.Command));
    }

    [Fact]
    public void Handoff_ConfirmedGivesComeOffAndTheRecordGoesWhenEmpty()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "darkwood ring", "darkwood ring");
        Assert.Null(h.Manager.StartHandoff(h.Manager.Records[0], "raij"));
        h.Pump();
        Assert.Equal(4, h.Sent.Count);

        h.Manager.OnItemGivenAway("darkwood ring", 2, "Raijin");
        h.Manager.OnItemGivenAway("thorned whip", 1, "Raijin");
        h.Manager.OnCoinsGivenAway("Raijin", 51 * 10_000);
        Assert.Single(h.Manager.Records);
        h.Manager.OnCoinsGivenAway("Raijin", 53 * 100);

        Assert.Empty(h.Manager.Records);
        Assert.False(h.Manager.HandoffActive);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("everything handed back"));
    }

    [Fact]
    public void Handoff_RefusedAndUnansweredGivesStayListed()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "rope and grapple");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");
        h.Pump();

        h.Manager.OnGiveRefused("Raijin");               // the oldest give: thorned whip
        h.Manager.OnItemGivenAway("rope and grapple", 1, "Raijin");
        h.Manager.OnCoinsGivenAway("Raijin", 51 * 10_000);
        h.FireTimers();                                   // the gold give was never answered

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal(new[] { "thorned whip" }, r.Items.Select(i => i.Name));
        Assert.Equal(0, r.Coins.Platinum);
        Assert.Equal(53, r.Coins.Gold);
        Assert.Contains("1 refused", r.Note);
        Assert.Contains("1 not answered", r.Note);
        Assert.False(h.Manager.HandoffActive);
    }

    [Fact]
    public void Handoff_SomeoneElsesGiveLineIsNotTaken()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");
        h.Pump();
        h.Manager.OnItemGivenAway("thorned whip", 1, "Nineteen");
        Assert.Contains(h.Manager.Records[0].Items, i => i.Name == "thorned whip");
    }

    [Fact]
    public void Clear_RemovesTheRecord()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.Clear(h.Manager.Records[0]);
        Assert.Empty(h.Manager.Records);
    }
}
