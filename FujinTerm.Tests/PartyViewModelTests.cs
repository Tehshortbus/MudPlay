using System.Text;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyViewModelTests
{
    // Tests use the 3-arg ctor with profile: null so they don't reach
    // through AppServices.Current — LocalRank stays at the Mid default,
    // which is fine; the rank-chip render is exercised by smoke / live.

    [Fact]
    public void HeaderText_EmptyParty_ShowsNoPartyActive()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        Assert.Equal("No party active", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_NoLeader_FallsBackToMemberCount()
    {
        // Mid-formation state — members exist but nobody's flagged
        // IsLeader yet. Fall back to the legacy "Party (N)" form so
        // the header still says something useful.
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        state.Members.Add(new PartyMember { Name = "Forged" });
        Assert.Equal("Party (1)", vm.HeaderText);
        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Equal("Party (2)", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_LeaderKnown_ShowsLeaderGivenAndHp()
    {
        // Skeleton-design header: "{LeaderGiven} ({LeaderHpPercent}%)".
        // Given-name only — MajorMUD addresses players that way at most
        // prompts so the header reads consistently with in-game text.
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        PartyMember leader = new() { Name = "Thaurin Lastname", IsLeader = true, HpPercent = 94 };
        state.Members.Add(leader);
        Assert.Equal("Thaurin (94%)", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_LeaderHpChanges_RefreshesLive()
    {
        // Per-member PropertyChanged subscription should fire a header
        // refresh whenever the leader's HpPercent ticks — the skeleton
        // header is a live readout, not a once-on-load value.
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        PartyMember leader = new() { Name = "Thaurin", IsLeader = true, HpPercent = 100 };
        state.Members.Add(leader);
        Assert.Equal("Thaurin (100%)", vm.HeaderText);

        leader.HpPercent = 75;
        Assert.Equal("Thaurin (75%)", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_RemovedMember_UnsubscribesCleanly()
    {
        // Members removed from the collection shouldn't keep firing
        // header refreshes — verified by removing the leader, then
        // mutating their HpPercent and asserting the header reverts
        // to the count fallback.
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        PartyMember leader = new() { Name = "Thaurin", IsLeader = true, HpPercent = 100 };
        state.Members.Add(leader);
        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Equal("Thaurin (100%)", vm.HeaderText);

        state.Members.Remove(leader);
        // Helper has no IsLeader → falls back to count.
        Assert.Equal("Party (1)", vm.HeaderText);
        // Mutating the removed leader's HpPercent should NOT affect
        // the header text now — the subscription has been removed.
        leader.HpPercent = 50;
        Assert.Equal("Party (1)", vm.HeaderText);
    }

    [Fact]
    public void Uninvite_AsLeader_SendsCommand()
    {
        PartyState state = new();
        state.SelfIsLeader = true;
        PartyMember target = new() { Name = "Helper" };
        state.Members.Add(target);

        List<byte[]> wire = new();
        PartyViewModel vm = new(state, wire.Add, profile: null);

        vm.UninviteCommand.Execute(target);

        byte[] sent = Assert.Single(wire);
        Assert.Equal("uninvite Helper\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Uninvite_NotLeader_DoesNothing()
    {
        PartyState state = new();
        state.SelfIsLeader = false;
        PartyMember target = new() { Name = "Helper" };
        state.Members.Add(target);

        List<byte[]> wire = new();
        PartyViewModel vm = new(state, wire.Add, profile: null);

        vm.UninviteCommand.Execute(target);

        Assert.Empty(wire);
    }

    [Fact]
    public void Uninvite_NullMember_DoesNothing()
    {
        PartyState state = new();
        state.SelfIsLeader = true;
        List<byte[]> wire = new();
        PartyViewModel vm = new(state, wire.Add, profile: null);

        vm.UninviteCommand.Execute(null);

        Assert.Empty(wire);
    }

    [Fact]
    public void LocalRank_DefaultsToMid_WhenNoProfileBound()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        Assert.Equal(PartyRank.Mid, vm.LocalRank);
    }
}
