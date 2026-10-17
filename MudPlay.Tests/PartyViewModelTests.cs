using System.Text;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyViewModelTests
{
    [Fact]
    public void HeaderText_EmptyParty_ShowsNoPartyActive()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        Assert.Equal("Party — no party active", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_NoSelfRow_FallsBackToMemberCount()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        state.Members.Add(new PartyMember { Name = "Forged" });
        Assert.Equal("Party (1)", vm.HeaderText);
        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Equal("Party (2)", vm.HeaderText);
    }

    // The menu's name, then this character — not the leader (report: Cidir's window
    // read "Nineteen (95%)").
    [Fact]
    public void HeaderText_ShowsOurOwnGivenNameAndHp_NotTheLeaders()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        state.Members.Add(new PartyMember { Name = "Nineteen ByNineteen", IsLeader = true, HpPercent = 95 });
        state.Members.Add(new PartyMember { Name = "Cidir", IsSelf = true, HpPercent = 100 });
        Assert.Equal("Party — Cidir (100%)", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_OurHpChanges_RefreshesLive()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        PartyMember self = new() { Name = "Cidir Priest", IsSelf = true, HpPercent = 100 };
        state.Members.Add(self);
        List<string?> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        self.HpPercent = 75;

        Assert.Contains(nameof(PartyViewModel.HeaderText), changed);
        Assert.Equal("Party — Cidir (75%)", vm.HeaderText);
    }

    [Fact]
    public void HeaderText_RemovedMember_UnsubscribesCleanly()
    {
        PartyState state = new();
        PartyViewModel vm = new(state, wireSender: null, profile: null);
        PartyMember self = new() { Name = "Cidir", IsSelf = true, HpPercent = 100 };
        state.Members.Add(self);
        state.Members.Add(new PartyMember { Name = "Helper" });
        state.Members.Remove(self);
        List<string?> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        self.HpPercent = 50;

        Assert.Empty(changed);
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

    // LocalRank follows the loaded character's Party rank, live as it's edited.
    [Fact]
    public void LocalRank_FollowsTheProfilesPartyRank()
    {
        MudPlay.Services.ProfileService profile = TestProfiles.Loaded();
        SetRank(profile, PartyRank.Back);
        using PartyViewModel vm = new(new PartyState(), wireSender: null, profile);
        Assert.Equal(PartyRank.Back, vm.LocalRank);

        SetRank(profile, PartyRank.Front);
        profile.NotifyMutated();

        Assert.Equal(PartyRank.Front, vm.LocalRank);
    }

    private static void SetRank(MudPlay.Services.ProfileService profile, PartyRank rank)
    {
        profile.Current!.Settings ??= new();
        profile.Current.Settings["Party"] =
            System.Text.Json.JsonSerializer.SerializeToElement(new PartySettings { Rank = rank });
    }
}
