using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The master switch and remote commands: with it off nothing in the catalog is
// obeyed or answered except @auto-all, which also has to engage the switch when
// every toggle is already off and fall back to the base modes when switched on
// with nothing remembered (user, 2026-10-09).
public sealed class MasterSwitchRemoteCommandTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private const string Sender = "Tank";

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        // The clock the Engine reads: the real one unless a test sets its own.
        public Func<DateTimeOffset> EngineNow { get; set; } = () => DateTimeOffset.UtcNow;
        // The router the engine's chat lines arrive through.
        public MessageRouter Router { get; }
        // What Engine put on the wire, through the sender the client binds.
        public List<byte[]> Sent { get; } = new();
        public AutoModeRemoteHandler Handler { get; }
        public AutoModeController Controller { get; }
        public ProfileService Profile { get; }
        public PartyState Party { get; } = new();
        public List<string> Invoked { get; } = new();

        public Setup()
        {
            MessageRouter router = new();
            Router = router;
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = Party;
            party.Members.Add(new PartyMember { Name = Sender });
            PlayerDatabase players = new(new ProfileService(), () => null);
            players.RecordObservation(Sender, null, null, null, null, null, null, Now);
            players.EditCustomization(Sender, new PlayerCustomization(RemoteControls: ~PlayerRemoteControls.None));
            Profile = TestProfiles.Service();
            Profile.LoadDefaultProfile();
            Controller = new AutoModeController(Profile);
            Engine = new RemoteCommandManager(chat, party, players, now: () => EngineNow())
            {
                BlockedByMasterSwitch = what => Controller.Blocks("Remote commands", what),
                // Lives known and plentiful, so @suicide-shaped commands reach a handler.
                LivesProvider = () => 9,
            };
            Engine.SetWireSender(Sent.Add);

            // A recording handler for every catalog command; the real @auto-* /
            // @settings handlers then replace theirs.
            foreach (string command in RemoteCommandCatalog.Map.Keys)
                Engine.RegisterHandler(command, RemoteCommandCatalog.Map[command],
                    ctx => { Invoked.Add(command); ctx.Reply("done"); });
            Handler = new AutoModeRemoteHandler(Engine, Profile, Controller);
        }

        public void WriteGeneral(GeneralSettings general)
        {
            Profile.Current!.Settings ??= new();
            Profile.Current.Settings["General"] = JsonSerializer.SerializeToElement(general);
        }

        public AutoActionDefaults Mode =>
            Profile.Current!.Settings is { } settings && settings.TryGetValue("General", out JsonElement json)
                ? JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())!.AutoMode
                : new AutoActionDefaults();

        public string LastReply => Encoding.Latin1.GetString(Sent[^1]);

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, Sender, msg);

    private static AutoActionDefaults AllOff() => new()
    {
        AutoCombat = false, AutoNuke = false, AutoHeal = false, AutoRest = false,
        AutoBless = false, AutoLight = false, AutoGetItems = false,
        AutoGetCash = false, AutoSneak = false, AutoHide = false, AutoSearch = false,
    };

    // Every catalog command but the switch's own (driven by the real handler in
    // the tests below) and @wait / @ok, which are still taken down while it is
    // off so a leader knows where its followers stand when it comes back on
    // (PartyWaitMovementGateTests).
    public static IEnumerable<object[]> CatalogCommands() =>
        RemoteCommandCatalog.Map.Keys
            .Where(c => !c.Equals(RemoteCommandManager.MasterSwitchCommand, StringComparison.OrdinalIgnoreCase)
                        && !c.Equals("@wait", StringComparison.OrdinalIgnoreCase)
                        && !c.Equals("@ok", StringComparison.OrdinalIgnoreCase))
            .Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(CatalogCommands))]
    public void SwitchOff_CommandIsNeitherRunNorAnswered(string command)
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        AutoActionDefaults before = s.Mode;

        ChatLines.Hear(s.Router, Telepath(command));

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Sent);
        Assert.True(before.SameAs(s.Mode));
        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.Equal(1, s.Controller.SkippedSinceOff["Remote commands"]);
    }

    [Theory]
    [MemberData(nameof(CatalogCommands))]
    public void SwitchOn_CommandIsHandledAsBefore(string command)
    {
        using Setup s = new();
        // A refusal then sends nothing, so a reply can only be a handler's.
        s.Engine.WarnOnDenial = false;

        ChatLines.Hear(s.Router, Telepath(command));

        // Handled: it reached a handler (the recorder, or the real @auto-* /
        // @settings one, which answers) and nothing was counted as skipped.
        Assert.True(s.Invoked.Contains(command) || s.Sent.Count > 0,
            $"{command} was not handled with the switch on");
        Assert.Empty(s.Controller.SkippedSinceOff);
    }

    [Fact]
    public void SwitchOff_RelayBackIsDropped()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");

        ChatLines.Hear(s.Router, Telepath("&@invite"));
        ChatLines.Hear(s.Router, Telepath("&@auto-all on"));

        Assert.Empty(s.Sent);
        Assert.True(s.Controller.KillSwitchEngaged);
    }

    [Fact]
    public void SwitchOff_OrdinaryChatStartingWithAt_IsNotCountedAsACommand()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");

        ChatLines.Hear(s.Router, Telepath("@because I said so"));

        Assert.Empty(s.Controller.SkippedSinceOff);
        Assert.Empty(s.Sent);
    }

    [Fact]
    public void AutoAllOff_WithEveryToggleAlreadyOff_EngagesTheSwitch()
    {
        using Setup s = new();
        s.WriteGeneral(new GeneralSettings { AutoMode = AllOff() });

        ChatLines.Hear(s.Router, Telepath("@auto-all off"));

        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.Contains("@auto-all: off", s.LastReply);
    }

    [Fact]
    public void AutoAllOn_NothingRemembered_SwitchesOnTheBaseModes()
    {
        using Setup s = new();
        AutoActionDefaults baseModes = AllOff();
        baseModes.AutoCombat = true;
        baseModes.AutoRest = true;
        s.WriteGeneral(new GeneralSettings { AutoMode = AllOff(), AutoModeBase = baseModes });

        ChatLines.Hear(s.Router, Telepath("@auto-all on"));

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.True(s.Mode.AutoCombat);
        Assert.True(s.Mode.AutoRest);
        Assert.False(s.Mode.AutoNuke);
        Assert.Contains("@auto-all: on", s.LastReply);
    }

    [Theory]
    [InlineData("@auto-all on")]
    [InlineData("@auto-all")]
    public void SwitchOff_AutoAllStillWorks_AndRestoresTheToggles(string command)
    {
        using Setup s = new();
        AutoActionDefaults mode = AllOff();
        mode.AutoCombat = true;
        s.WriteGeneral(new GeneralSettings { AutoMode = mode });
        ChatLines.Hear(s.Router, Telepath("@auto-all off"));
        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.False(s.Mode.AutoCombat);

        ChatLines.Hear(s.Router, Telepath(command));

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.True(s.Mode.AutoCombat);
        Assert.Contains("@auto-all: on", s.LastReply);
    }

    [Fact]
    public void SwitchOff_AutoAllFromSomeoneWithoutTheGrant_IsStillDenied()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLine stranger = new(ChatChannel.TelepathIncoming, "Stranger", "@auto-all on");

        ChatLines.Hear(s.Router, stranger);

        Assert.True(s.Controller.KillSwitchEngaged);
        // And unanswered: a denial would make @auto-all the one command that tells
        // a stranger the client is there.
        Assert.Empty(s.Sent);
    }

    [Fact]
    public void SwitchOn_AutoAllFromSomeoneWithoutTheGrant_GetsTheUsualDenial()
    {
        using Setup s = new();
        ChatLine stranger = new(ChatChannel.TelepathIncoming, "Stranger", "@auto-all off");

        ChatLines.Hear(s.Router, stranger);

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.Single(s.Sent);
    }

    // ----- A @comeback kept for the switch --------------------------------

    private static void SwitchBackOn(Setup s)
    {
        s.Controller.TurnOn("test");
        s.Engine.ReplayHeldComebacks();
    }

    // A stranded member asks once, so their ask is kept and answered when the
    // switch is back on.
    [Fact]
    public void ComebackFromAMemberWhileOff_IsAnsweredAtSwitchOn()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLines.Hear(s.Router, Telepath("@comeback"));
        Assert.Empty(s.Invoked);

        SwitchBackOn(s);

        Assert.Equal(new[] { "@comeback" }, s.Invoked);
    }

    // Kept only from a sender who would be gone back for: a stranger's ask kept
    // and put through at switch-on drew the "not allowed" reply the off state is
    // there never to give.
    [Fact]
    public void ComebackFromAStrangerWhileOff_IsNotKept_AndNeverAnswered()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLines.Hear(s.Router, new ChatLine(ChatChannel.TelepathIncoming, "Stranger", "@comeback"));

        Assert.Empty(s.Engine.HeldComebackSenders);
        SwitchBackOn(s);

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Sent);
    }

    [Fact]
    public void KeptComeback_DroppedWhenTheSenderLeavesTheParty()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLines.Hear(s.Router, Telepath("@comeback"));

        s.Party.Members.Clear();
        SwitchBackOn(s);

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Sent);
    }

    // A profile load and a dropped connection both end the stay the ask was made
    // in; it must not be answered at some later switch-on.
    [Theory]
    [InlineData("another profile was loaded")]
    [InlineData("disconnected")]
    public void KeptComeback_DroppedByAProfileLoadOrADisconnect(string why)
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLines.Hear(s.Router, Telepath("@comeback"));

        s.Engine.DropHeldComebacks(why);
        SwitchBackOn(s);

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Sent);
    }

    // Older than "If leading, accept @comeback for": the party has moved on.
    [Fact]
    public void KeptComeback_DroppedOnceOlderThanTheComebackWindow()
    {
        using Setup s = new();
        DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        s.EngineNow = () => now;
        s.Engine.ComebackWindow = TimeSpan.FromMinutes(2);
        s.Controller.TurnOff("test");
        ChatLines.Hear(s.Router, Telepath("@comeback"));

        now += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);
        SwitchBackOn(s);

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Sent);
    }
}
