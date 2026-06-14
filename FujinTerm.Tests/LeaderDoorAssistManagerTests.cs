using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Decision coverage for <see cref="LeaderDoorAssistManager"/>: only the
/// leader's door-bash failure (with the toggle on) triggers a matching
/// help command, with the verb chosen by the Other-tab pick/bash
/// preference and the full direction word normalised to its short token.
/// </summary>
public sealed class LeaderDoorAssistManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public PartyState Party { get; } = new();
        public PartySettings PartyCfg { get; } = new() { HelpLeaderOpenDoors = true };
        public OtherSettings OtherCfg { get; } = new();
        public LeaderDoorAssistManager Mgr { get; }
        public List<byte[]> Sent { get; } = new();

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Mgr = new LeaderDoorAssistManager(Router, Party,
                () => PartyCfg, () => OtherCfg);
            Mgr.SetWireSender(Sent.Add);
        }

        public void InParty(string leaderName)
        {
            Party.Members.Add(new PartyMember { Name = leaderName, IsLeader = true });
            Party.Members.Add(new PartyMember { Name = "Raijin", IsSelf = true });
            Party.LeaderName = leaderName;
            Party.IsInParty = true;
            Party.SelfIsLeader = false;
        }

        public void Line(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, [], DateTimeOffset.UtcNow, false));

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose() => Mgr.Dispose();
    }

    [Fact]
    public void LeaderBashFail_TogglesOn_SendsBashSameDirection()
    {
        using Harness h = new();
        h.InParty("Fujin");

        h.Line("You see Fujin attempt to bash the door to the north.");

        Assert.Equal("bash n", h.LastSent);
    }

    [Fact]
    public void LeaderBashFail_PicklocksPreferred_SendsPick()
    {
        using Harness h = new();
        h.InParty("Fujin");
        h.OtherCfg.PicklocksOverBash = true;

        h.Line("You see Fujin attempt to bash the door to the southeast.");

        Assert.Equal("pick se", h.LastSent);
    }

    [Fact]
    public void NonLeaderBashFail_DoesNothing()
    {
        using Harness h = new();
        h.InParty("Fujin");

        h.Line("You see Stranger attempt to bash the door to the north.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LeaderBashFail_ToggleOff_DoesNothing()
    {
        using Harness h = new();
        h.InParty("Fujin");
        h.PartyCfg.HelpLeaderOpenDoors = false;

        h.Line("You see Fujin attempt to bash the door to the north.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NotInParty_DoesNothing()
    {
        using Harness h = new();
        // No party — observing anyone bash a door is irrelevant.
        h.Line("You see Fujin attempt to bash the door to the north.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void LeaderName_FamilyName_MatchesByGivenName()
    {
        using Harness h = new();
        h.InParty("Fujin Wuz");

        h.Line("You see Fujin attempt to bash the door to the up.");

        Assert.Equal("bash u", h.LastSent);
    }
}
