using System.Text;
using FujinTerm.Game;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyViewModelTests
{
    [Fact]
    public void HeaderText_ReflectsMemberCount()
    {
        PartyState state = new();
        PartyViewModel vm = new(state);

        Assert.Equal("Party (0)", vm.HeaderText);
        state.Members.Add(new PartyMember { Name = "Forged" });
        Assert.Equal("Party (1)", vm.HeaderText);
        state.Members.Add(new PartyMember { Name = "Helper" });
        Assert.Equal("Party (2)", vm.HeaderText);
        state.Members.RemoveAt(0);
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
        PartyViewModel vm = new(state, wire.Add);

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
        PartyViewModel vm = new(state, wire.Add);

        vm.UninviteCommand.Execute(target);

        Assert.Empty(wire);
    }

    [Fact]
    public void Uninvite_NullMember_DoesNothing()
    {
        PartyState state = new();
        state.SelfIsLeader = true;
        List<byte[]> wire = new();
        PartyViewModel vm = new(state, wire.Add);

        vm.UninviteCommand.Execute(null);

        Assert.Empty(wire);
    }
}
