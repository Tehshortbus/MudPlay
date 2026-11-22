using System;
using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// "Sysop goto": jump to a curated location with `sys goto <name>`. The manager gates
// the fire (combat / table membership / level), sends the verbatim keyword + a bare
// Enter, and arms a name-matched landing resync. These pin the gate order and the
// resync commit; the real starter values back the level-gate cases.
public sealed class SysopGotoManagerTests
{
    private sealed class Harness
    {
        public readonly List<string> Sent = new();
        public readonly List<string> Status = new();
        public int ForceRoomDisplayCalls;
        public readonly List<RoomKey> Committed = new();
        public bool Enabled = true;
        public bool InCombat;
        public int? Level;
        public List<SysopGotoLocation> Locations = SysopGotoLocation.DefaultStarterSet();
        public readonly Dictionary<RoomKey, string> RoomNames = new();
        public string? RoomHere;
        public bool RunStops;
        public readonly List<string> LandedElsewhere = new();

        public SysopGotoManager Build() => new(
            enabled: () => Enabled,
            locations: () => Locations,
            inCombat: () => InCombat,
            knownLevel: () => Level,
            roomName: key => RoomNames.TryGetValue(key, out string? n) ? n : null,
            roomHere: () => RoomHere,
            send: Sent.Add,
            forceRoomDisplay: () => ForceRoomDisplayCalls++,
            writeStatus: Status.Add,
            commitLocated: Committed.Add,
            landedElsewhere: why => { LandedElsewhere.Add(why); return RunStops; });
    }

    // The starter towns the level gate is judged against.
    private static readonly RoomKey Newhaven = new(1, 2150);   // MinLevel 0
    private static readonly RoomKey Lostcity = new(16, 426);   // MinLevel 40

    private static Harness WithNames()
    {
        var h = new Harness();
        h.RoomNames[Newhaven] = "Town Square";
        h.RoomNames[Lostcity] = "Lost City Gate";
        return h;
    }

    [Fact]
    public void Fire_SendsVerbatimKeyword_ForcesRoomDisplay_ArmsResync()
    {
        var h = WithNames();
        var mgr = h.Build();

        var loc = h.Locations.Find(l => l.Name == "newhaven")!;
        Assert.True(mgr.TryFire(loc, out string refusal));
        Assert.Empty(refusal);
        Assert.Equal(new[] { "sys goto newhaven" }, h.Sent);
        Assert.Equal(1, h.ForceRoomDisplayCalls);
        Assert.True(mgr.HasArmedLanding);
    }

    [Fact]
    public void Fire_WhileInCombat_SendsBreak_Refuses_NoGoto()
    {
        var h = WithNames();
        h.InCombat = true;
        var mgr = h.Build();

        var loc = h.Locations.Find(l => l.Name == "newhaven")!;
        Assert.False(mgr.TryFire(loc, out string refusal));
        Assert.Contains("in combat", refusal);
        Assert.Equal(new[] { "break" }, h.Sent);        // break only — no sys goto
        Assert.False(mgr.HasArmedLanding);
    }

    [Fact]
    public void TypedLine_UnknownKeyword_Refuses()
    {
        var h = WithNames();
        var mgr = h.Build();

        Assert.True(mgr.TryHandleTypedLine("sys goto nowhere"));  // ours, swallowed
        Assert.Empty(h.Sent);                                     // never sent to the game
        var msg = Assert.Single(h.Status);
        Assert.Contains("isn't in your Sys Goto table", msg);
    }

    [Fact]
    public void Fire_BelowMinLevel_Refuses()
    {
        var h = WithNames();
        h.Level = 20;
        var mgr = h.Build();

        var loc = h.Locations.Find(l => l.Name == "lostcity")!;  // needs 40
        Assert.False(mgr.TryFire(loc, out string refusal));
        Assert.Contains("needs level 40", refusal);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Fire_AtOrAboveMinLevel_Succeeds()
    {
        var h = WithNames();
        h.Level = 40;
        var mgr = h.Build();

        var loc = h.Locations.Find(l => l.Name == "lostcity")!;
        Assert.True(mgr.TryFire(loc, out _));
        Assert.Equal(new[] { "sys goto lostcity" }, h.Sent);
    }

    [Fact]
    public void Fire_UnknownLevel_PassesGate()
    {
        var h = WithNames();
        h.Level = null;                                          // statline not parsed yet
        var mgr = h.Build();

        var loc = h.Locations.Find(l => l.Name == "lostcity")!;
        Assert.True(mgr.TryFire(loc, out _));                    // manual fire trusts the user
        Assert.Equal(new[] { "sys goto lostcity" }, h.Sent);
    }

    [Fact]
    public void TypedLine_NotOurCommand_PassesThrough()
    {
        var h = WithNames();
        var mgr = h.Build();
        Assert.False(mgr.TryHandleTypedLine("go north"));
        Assert.False(mgr.TryHandleTypedLine("sysop status"));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void TypedLine_PowerOff_PassesThrough()
    {
        var h = WithNames();
        h.Enabled = false;
        var mgr = h.Build();
        Assert.False(mgr.TryHandleTypedLine("sys goto newhaven"));  // let the game refuse
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void OnRoomDisplayed_NameMatch_CommitsLandingOnce()
    {
        var h = WithNames();
        var mgr = h.Build();
        mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _);

        mgr.OnRoomDisplayed("Town Square");
        Assert.Equal(new[] { Newhaven }, h.Committed);
        Assert.False(mgr.HasArmedLanding);

        // A second display doesn't re-commit — the arm was consumed.
        mgr.OnRoomDisplayed("Town Square");
        Assert.Single(h.Committed);
    }

    // Report stock-20261122-124831: a board whose silvermere isn't the starter row's room.
    [Fact]
    public void ALandingInAnotherRoom_WarnsOnTheTerminal_NamingBothRooms()
    {
        var h = WithNames();
        h.RoomHere = "Slimy Sewer Tunnel";
        var mgr = h.Build();
        mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _);

        mgr.OnRoomDisplayed("Silvermere Residence");

        string notice = Assert.Single(h.Status);
        Assert.StartsWith("[Sys goto 'newhaven' landed in Silvermere Residence", notice);
        Assert.Contains("Town Square", notice);
        Assert.EndsWith("]", notice);
        Assert.DoesNotContain("Navigation stopped", notice);
        Assert.Empty(h.Committed);
        Assert.Single(h.LandedElsewhere);
    }

    [Fact]
    public void ALandingInAnotherRoom_SaysSoWhenItStoppedARun()
    {
        var h = WithNames();
        h.RoomHere = "Slimy Sewer Tunnel";
        h.RunStops = true;
        var mgr = h.Build();
        mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _);

        mgr.OnRoomDisplayed("Silvermere Residence");

        Assert.Contains("Navigation stopped.", Assert.Single(h.Status));
    }

    [Theory]
    [InlineData("Slimy Sewer Tunnel", "slimy sewer tunnel")]   // refused: the room we left, shown again
    [InlineData(null, "Silvermere Residence")]                 // no known room to compare
    [InlineData("Slimy Sewer Tunnel", "Town Square")]          // landed where the table says
    public void NoWarning_WhenTheDisplayDoesNotProveTheRowWrong(string? left, string shown)
    {
        var h = WithNames();
        h.RoomHere = left;
        var mgr = h.Build();
        mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _);

        mgr.OnRoomDisplayed(shown);

        Assert.Empty(h.Status);
        Assert.Empty(h.LandedElsewhere);
    }

    [Fact]
    public void OnRoomDisplayed_NameMismatch_DropsWithoutCommitting()
    {
        var h = WithNames();
        var mgr = h.Build();
        mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _);

        mgr.OnRoomDisplayed("Some Other Room");                  // jump refused / wrong landing
        Assert.Empty(h.Committed);
        Assert.False(mgr.HasArmedLanding);
    }

    [Fact]
    public void Fire_UnknownLandingRoom_DoesNotArm()
    {
        var h = new Harness();                                   // no RoomNames seeded
        var mgr = h.Build();

        Assert.True(mgr.TryFire(h.Locations.Find(l => l.Name == "newhaven")!, out _));
        Assert.Equal(new[] { "sys goto newhaven" }, h.Sent);
        Assert.False(mgr.HasArmedLanding);                       // can't resync an unknown room
    }

    [Fact]
    public void UsableNow_EmptyWhenPowerOff()
    {
        var h = WithNames();
        h.Enabled = false;
        var mgr = h.Build();
        Assert.Empty(mgr.UsableNow);
    }

    // ----- Wimpy escape (HealthManager's "sys goto wimpy instead of hanging") -----

    [Fact]
    public void Wimpy_OutOfCombat_FiresVerbatim_NoBreak()
    {
        var h = WithNames();
        var mgr = h.Build();

        Assert.True(mgr.TryFireForWimpy("newhaven"));
        Assert.Equal(new[] { "sys goto newhaven" }, h.Sent);   // no break needed
        Assert.Equal(1, h.ForceRoomDisplayCalls);
        Assert.True(mgr.HasArmedLanding);
    }

    [Fact]
    public void Wimpy_InCombat_BreaksThenFires()
    {
        var h = WithNames();
        h.InCombat = true;
        var mgr = h.Build();

        Assert.True(mgr.TryFireForWimpy("newhaven"));
        Assert.Equal(new[] { "break", "sys goto newhaven" }, h.Sent);   // break first, then jump
    }

    [Fact]
    public void Wimpy_IgnoresLevelGate()
    {
        // A life-saving escape isn't a routing choice — the min-level gate that blocks
        // a manual/menu fire doesn't apply here.
        var h = WithNames();
        h.Level = 1;
        var mgr = h.Build();

        Assert.True(mgr.TryFireForWimpy("lostcity"));   // needs L40 for a normal fire
        Assert.Equal(new[] { "sys goto lostcity" }, h.Sent);
    }

    [Fact]
    public void Wimpy_PowerOff_ReturnsFalse_NoSend()
    {
        var h = WithNames();
        h.Enabled = false;
        var mgr = h.Build();

        Assert.False(mgr.TryFireForWimpy("newhaven"));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Wimpy_UnknownLocation_ReturnsFalse_NoSend()
    {
        var h = WithNames();
        var mgr = h.Build();

        Assert.False(mgr.TryFireForWimpy("nowhere"));
        Assert.Empty(h.Sent);
    }
}
