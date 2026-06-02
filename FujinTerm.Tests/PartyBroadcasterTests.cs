using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using Xunit;

namespace FujinTerm.Tests;

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

    [Fact]
    public void BroadcastExpReset_NoWireSender_Silent()
    {
        PartyState state = new();
        AddMember(state, "Helper");
        PartyBroadcaster bc = new(state);
        // No SetWireSender call — should silently no-op rather than throw.
        bc.BroadcastExpReset();
    }

    // ===== Generic Broadcast =====

    [Fact]
    public void Broadcast_DoesNotConsultAutoExpResetToggle()
    {
        // Generic Broadcast bypasses the auto-exp-reset gate by design —
        // future panic / kill broadcasts in Phase 12 will use it.
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
}
