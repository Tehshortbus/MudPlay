using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// PartyProbeManager telepaths @level + @version to a member the FIRST time we
// party with them on a local day, and records the @version reply onto the
// player record. These cover the daily gate, the invited→joined edge, the
// enable/suspend gates, and the brace-wrapped version-reply capture.
public sealed class PartyProbeManagerTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PlayerDatabase Players = new(new ProfileService(), () => null);
        public readonly List<byte[]> Wire = new();
        public readonly PartyProbeManager Probe;

        public DateTime Now = DateTime.UnixEpoch;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Probe = new PartyProbeManager(Chat, State, Players, clock: () => Now, log: null);
            Probe.SetWireSender(Wire.Add);
        }

        public PartyMember AddMember(string name, bool self = false, bool invited = false)
        {
            PartyMember m = new() { Name = name, IsSelf = self, IsInvited = invited };
            State.Members.Add(m);
            return m;
        }

        public void Reply(string sender, string message)
        {
            string line = $"{sender} telepaths: {message}";
            Router.Dispatch(new Terminal.LineExtractor.EmittedLine(
                line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public string WireText => string.Join("|", Wire.ConvertAll(b => Encoding.Latin1.GetString(b)));
    }

    // ----- daily gate ----------------------------------------------------

    [Fact]
    public void FirstPartyOfDay_SendsLevelAndVersion()
    {
        var h = new Harness();
        h.AddMember("Bob");

        Assert.Contains("/Bob @level\r", h.WireText);
        Assert.Contains("/Bob @version\r", h.WireText);
        // The join is stamped so a same-day rejoin won't re-probe.
        Assert.Equal(h.Now, h.Players.GetLastPartiedUtc("Bob"));
    }

    [Fact]
    public void SameNameNewClass_DropsOldTitleAndLevel_AndReprobesToday()
    {
        // Report stock-20260924-133615: the record was a level-1 Priest titled
        // "Curate" (probed earlier today); the name now belongs to a Mage. The old
        // title band kept the party's level window at 1–14, shutting a 1–3 gate.
        var h = new Harness();
        h.Players.RecordObservation("Raijin", "Priest", "Human", "Neutral", "Curate", null, null, h.Now);
        h.Players.RecordLevel("Raijin", 1, h.Now);
        h.Players.RecordPartied("Raijin", h.Now);

        PartyMember m = h.AddMember("Raijin");     // joins with no class yet — same day, no probe
        Assert.Empty(h.Wire);
        m.Class = "Mage";                           // the next `par` fills it in

        PlayerRecord rec = h.Players.Find("Raijin")!;
        Assert.Equal("Mage", rec.Class);
        Assert.Null(rec.Title);
        Assert.Null(rec.Level);
        Assert.Contains("/Raijin @level\r", h.WireText);
    }

    [Fact]
    public void SameClassOnRoster_KeepsTheRecord()
    {
        var h = new Harness();
        h.Players.RecordObservation("Bob", "Priest", null, null, "Curate", null, null, h.Now);
        h.Players.RecordPartied("Bob", h.Now);

        PartyMember m = h.AddMember("Bob");
        m.Class = "priest";

        Assert.Equal("Curate", h.Players.Find("Bob")!.Title);
        Assert.Empty(h.Wire);
    }

    [Fact]
    public void SecondPartySameDay_DoesNotResend()
    {
        var h = new Harness();
        h.Players.RecordPartied("Bob", h.Now);   // already partied earlier today

        h.AddMember("Bob");

        Assert.Empty(h.Wire);
    }

    [Fact]
    public void NextDay_ReprobesTheSamePlayer()
    {
        var h = new Harness();
        h.Players.RecordPartied("Bob", h.Now);   // partied "yesterday"
        h.Now += TimeSpan.FromHours(25);          // a later calendar day

        h.AddMember("Bob");

        Assert.Contains("/Bob @version\r", h.WireText);
    }

    // ----- membership edges ----------------------------------------------

    [Fact]
    public void InvitedMember_NotProbedUntilTheyJoin()
    {
        var h = new Harness();
        PartyMember m = h.AddMember("Tristian", invited: true);
        Assert.Empty(h.Wire);

        m.IsInvited = false;   // acceptance edge

        Assert.Contains("/Tristian @version\r", h.WireText);
    }

    [Fact]
    public void SelfMember_NeverProbed()
    {
        var h = new Harness();
        h.AddMember("Me", self: true);
        Assert.Empty(h.Wire);
    }

    // ----- enable / suspend gates ----------------------------------------

    [Fact]
    public void Disabled_SendsNothing()
    {
        var h = new Harness();
        h.Probe.Enabled = false;
        h.AddMember("Bob");
        Assert.Empty(h.Wire);
    }

    [Fact]
    public void Suspended_SendsNothing_ThenResumes()
    {
        var h = new Harness();
        h.Probe.NotifyDisconnected();
        h.AddMember("Bob");
        Assert.Empty(h.Wire);

        h.Probe.NotifyEnteredRealm();
        h.AddMember("Al");
        Assert.Contains("/Al @version\r", h.WireText);
    }

    [Fact]
    public void TrainerMenu_SuppressesTheProbe()
    {
        var h = new Harness();
        h.Probe.IsInTrainerMenu = () => true;
        h.AddMember("Bob");
        Assert.Empty(h.Wire);
    }

    // ----- @version reply capture ----------------------------------------

    [Fact]
    public void VersionReply_RecordedOntoPlayer()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Reply("Bob", "{MudPlay 2.37.0}");

        Assert.Equal("MudPlay 2.37.0", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void VersionReply_MegaMudForm_Recorded()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Reply("Bob", "{MegaMud 1.03u}");

        Assert.Equal("MegaMud 1.03u", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void LevelReply_NotMistakenForVersion()
    {
        var h = new Harness();
        h.AddMember("Bob");

        // The member answers @level first — must not be recorded as a version, and
        // the expectation stays armed for the real @version line that follows.
        h.Reply("Bob", "{Level 12, 1,234 exp, 500 to next level}");
        Assert.Null(h.Players.Find("Bob")?.Version);
        // MegaMUD's shape too — letter-led with digits, so only the prefix rule stops it.
        h.Reply("Bob", "{Level: 12  Needed: 1,000  Will level in: ?}");
        Assert.Null(h.Players.Find("Bob")?.Version);

        h.Reply("Bob", "{MudPlay 2.37.0}");
        Assert.Equal("MudPlay 2.37.0", h.Players.Find("Bob")!.Version);
    }

    // Outside a probe window only a reply that can only be a client version is
    // taken — the join probe's telepath may never have been delivered.
    [Fact]
    public void UnsolicitedReply_KnownClientVersion_Records_OtherShapesIgnored()
    {
        var h = new Harness();
        h.Reply("Bob", "{Docks 1/520}");
        Assert.Null(h.Players.Find("Bob")?.Version);
        h.Reply("Bob", "{MudPlay 3.105.0}");
        Assert.Equal("MudPlay 3.105.0", h.Players.Find("Bob")!.Version);
        h.Reply("Bob", "{MegaMud 1.03u}");
        Assert.Equal("MegaMud 1.03u", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void VersionReply_AfterWindow_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Now += TimeSpan.FromMinutes(1);   // past the version window
        // Version-shaped but not a known client — only a probe window vouches for it.
        h.Reply("Bob", "{Zmud 7.21}");

        Assert.Null(h.Players.Find("Bob")?.Version);
    }

    [Fact]
    public void UnexpectedVersionLine_Ignored()
    {
        var h = new Harness();
        // No probe sent to Carol — a version-shaped line from an unknown client isn't
        // recorded (a known client's is; see UnsolicitedReply_KnownClientVersion…).
        h.Reply("Carol", "{Zmud 7.21}");
        Assert.Null(h.Players.Find("Carol")?.Version);
    }

    // ----- version parse unit ---------------------------------------------

    [Theory]
    [InlineData("{MudPlay 2.37.0}", true, "MudPlay 2.37.0")]
    [InlineData("{MegaMud 1.03u}", true, "MegaMud 1.03u")]
    [InlineData("MudPlay 2.37.0", false, "")]          // not brace-wrapped
    [InlineData("{Permission denied}", false, "")]        // no digit → denial/chat
    [InlineData("{Level 12, 1 exp}", false, "")]          // level reply
    [InlineData("{HP=10/20,MA=5/5}", false, "")]          // health reply
    [InlineData("{}", false, "")]                          // empty
    [InlineData("{2.37.0}", false, "")]                    // no leading letter
    [InlineData("{sending loop 'x' (3 rooms, 1 line)}", false, "")]   // a @loop send line, whatever the loop is named
    [InlineData("{loop send of 'Road 2' cancelled}", false, "")]
    [InlineData("{preparing to send: MudPlay 9.9.9, yes to confirm, no to deny}", false, "")]
    public void TryParseVersion_Cases(string message, bool ok, string expected)
    {
        bool parsed = PartyProbeManager.TryParseVersion(message, out string version);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(expected, version);
    }

    // An @events or @event reply names an event, and a digit in a name is all the
    // version rule asks for: none of them is a client's version.
    [Theory]
    [InlineData("{events: Farm 2 on, Bank off}")]
    [InlineData("{events (all disabled): Farm 2 on}")]
    [InlineData("{events (2/3): MudPlay 9.9.9 off}")]
    [InlineData("{event 'Farm 2' switched off}")]
    [InlineData("{event 'Farm 2' is already on}")]
    [InlineData("{no event named 'MudPlay 9.9.9'}")]
    [InlineData("{event name 'farm' matches 2: 'Farm 2', 'Farm 3'}")]
    [InlineData("{events: 1 Farm on, 2 Bank off}")]
    [InlineData("{event 2 'Farm' switched off}")]
    [InlineData("{event 2 (unnamed) switched off}")]
    [InlineData("{no event number 9: there are 4}")]
    public void EventReplyNamingAnEvent_IsNoVersion(string message) =>
        Assert.False(PartyProbeManager.TryParseVersion(message, out _));
}
