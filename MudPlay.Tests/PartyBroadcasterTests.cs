using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyBroadcasterTests
{
    private static (PartyBroadcaster bc, PartyState state, List<byte[]> wire) Setup()
    {
        PartyState state = new();
        PartyBroadcaster bc = new(state);
        List<byte[]> wire = new();
        bc.SetWireSender(wire.Add);
        return (bc, state, wire);
    }

    private static void AddMember(PartyState s, string name, bool isSelf = false)
    {
        s.Members.Add(new PartyMember { Name = name, IsSelf = isSelf });
        s.IsInParty = true;
    }

    // ===== BroadcastExpReset =====

    [Fact]
    public void BroadcastExpReset_NoParty_Silent()
    {
        var (bc, _, wire) = Setup();
        bc.BroadcastExpReset();
        Assert.Empty(wire);
    }

    [Fact]
    public void BroadcastExpReset_ToggleOff_Silent()
    {
        var (bc, state, wire) = Setup();
        AddMember(state, "Helper");
        bc.AutoExpResetEnabled = false;

        bc.BroadcastExpReset();

        Assert.Empty(wire);
    }

    [Fact]
    public void BroadcastExpReset_SkipsSelf()
    {
        var (bc, state, wire) = Setup();
        AddMember(state, "Forged", isSelf: true);
        AddMember(state, "Helper");

        bc.BroadcastExpReset();

        // Only Helper gets the telepath; self is skipped.
        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Helper @Reset\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void BroadcastExpReset_TelepathsAllNonSelfMembers()
    {
        var (bc, state, wire) = Setup();
        AddMember(state, "Forged", isSelf: true);
        AddMember(state, "Helper");
        AddMember(state, "Tank");
        AddMember(state, "Cleric");

        bc.BroadcastExpReset();

        Assert.Equal(3, wire.Count);
        Assert.Equal("/Helper @Reset\r", Encoding.Latin1.GetString(wire[0]));
        Assert.Equal("/Tank @Reset\r",   Encoding.Latin1.GetString(wire[1]));
        Assert.Equal("/Cleric @Reset\r", Encoding.Latin1.GetString(wire[2]));
    }

    // ===== Generic Broadcast =====

    [Fact]
    public void Broadcast_DoesNotConsultAutoExpResetToggle()
    {
        // Generic Broadcast bypasses the auto-exp-reset gate by design.
        var (bc, state, wire) = Setup();
        AddMember(state, "Helper");
        bc.AutoExpResetEnabled = false;

        bc.Broadcast("@panic!");

        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Helper @panic!\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Broadcast_NullOrEmptyCommand_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws
        // ArgumentNullException for null and ArgumentException for
        // empty / whitespace — Assert.Throws is exact-type so split.
        var (bc, _, _) = Setup();
        Assert.Throws<ArgumentException>(() => bc.Broadcast(""));
        Assert.Throws<ArgumentException>(() => bc.Broadcast("   "));
        Assert.Throws<ArgumentNullException>(() => bc.Broadcast(null!));
    }

    [Fact]
    public void Broadcast_MemberWithoutName_Skipped()
    {
        var (bc, state, wire) = Setup();
        state.Members.Add(new PartyMember { Name = string.Empty });
        state.Members.Add(new PartyMember { Name = "Real" });
        state.IsInParty = true;

        bc.Broadcast("@kill");

        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Real @kill\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Broadcast_SkipInvited_ExcludesPendingInvitee()
    {
        var (bc, state, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Joined" });
        state.Members.Add(new PartyMember { Name = "Tristian", IsInvited = true });
        state.IsInParty = true;

        bc.Broadcast("@level", skipInvited: true);

        byte[] sent = Assert.Single(wire);
        Assert.Equal("/Joined @level\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void Broadcast_DefaultIncludesInvited()
    {
        // Default (skipInvited=false) keeps @Reset / @wealth behaviour: a stray
        // telepath to a pending invitee is harmless and there's no reply to wait on.
        var (bc, state, wire) = Setup();
        state.Members.Add(new PartyMember { Name = "Joined" });
        state.Members.Add(new PartyMember { Name = "Tristian", IsInvited = true });
        state.IsInParty = true;

        bc.Broadcast("@Reset");

        Assert.Equal(2, wire.Count);
        Assert.Equal("/Joined @Reset\r",   Encoding.Latin1.GetString(wire[0]));
        Assert.Equal("/Tristian @Reset\r", Encoding.Latin1.GetString(wire[1]));
    }
}
