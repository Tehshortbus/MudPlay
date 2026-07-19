using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// The walk-time checkspell hazard-buff provisioner: on the approach hook it
// `use`s a carried buff-source item so the buff is up before the step lands, and
// a per-source-item timer keyed on the buff's duration debounces the re-use so a
// fast traverse spends ONE charge while a stretch outlasting the buff re-raises
// it. These pin the raise / skip / re-raise boundaries and the no-op paths.
public sealed class AutoHazardCounterProvisionerTests
{
    private static readonly RoomKey HazardRoom = new(1, 5);
    private static readonly RoomKey BenignRoom = new(1, 6);

    // Desert room (spell 700) countered by buff 300, raised by `use waterskin`
    // (item 60), with a 300s protection window.
    private static Room DesertRoom() => new()
    {
        Key = HazardRoom,
        Name = "Scorching Desert",
        Spell = 700,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static RoomHazardIndex.RoomHazard WaterskinHazard(int durationSeconds = 300) =>
        new(
            new IReadOnlyList<int>[] { new[] { 60 } },
            new[] { new RoomHazardIndex.BuffCounter(300, durationSeconds, new[] { 60 }) });

    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int Carried = 1;   // waterskins on hand
        public AutoHazardCounterProvisioner Engine { get; }

        public Harness(RoomHazardIndex.RoomHazard? hazard = null, Room? room = null)
        {
            room ??= DesertRoom();
            hazard ??= WaterskinHazard();
            Engine = new AutoHazardCounterProvisioner(
                resolveRoom:    key => key == room.Key ? room : null,
                hazardForSpell: spell => spell == room.Spell ? hazard : null,
                carriedCount:   id => id == 60 ? Carried : 0,
                itemName:       id => id == 60 ? "waterskin" : null,
                now:            () => Now);
            Engine.SetWireSender(_ => { });
        }

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        public IReadOnlyList<string> Sent => Engine.LastSentForTests
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();
    }

    [Fact]
    public void Approaching_HazardRoom_RaisesBuff()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Equal(new[] { "use waterskin" }, h.Sent);
    }

    [Fact]
    public void SecondApproach_WithinWindow_SkipsReUse()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);
        h.Advance(60);                       // buff still up (300s window − 15s margin)
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Single(h.Sent);               // one charge spent, not two
    }

    [Fact]
    public void SecondApproach_AfterWindow_ReRaises()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);
        h.Advance(300);                      // past the refresh window
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Equal(2, h.Sent.Count);       // buff lapsed → re-raised
    }

    [Fact]
    public void NothingCarried_SendsNothing()
    {
        Harness h = new() { Carried = 0 };
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void BenignRoom_SendsNothing()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(BenignRoom);   // no room / no hazard resolves
        Assert.Empty(h.Sent);
    }

    // Dur 0 (buff duration absent from data) → the fallback periodic refresh
    // (60s) still debounces a rapid re-approach rather than re-`use`ing every step.
    [Fact]
    public void UnknownDuration_UsesFallbackRefresh()
    {
        Harness h = new(hazard: WaterskinHazard(durationSeconds: 0));
        h.Engine.OnApproachingRoom(HazardRoom);
        h.Advance(30);
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Single(h.Sent);               // 30s < 60s fallback → still covered
        h.Advance(60);
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Equal(2, h.Sent.Count);       // past 60s → re-raised
    }
}
