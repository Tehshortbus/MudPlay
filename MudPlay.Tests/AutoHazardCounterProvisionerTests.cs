using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

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

    // A passive full-immunity guard (the desert sunstone wristband); item 99 here.
    // Carrying/wearing it makes the whole hazard a no-op — the provisioner must skip
    // the `use` entirely.
    private const int ImmunityItem = 99;

    // Buff 300 raised by the waterskin; its lapse-damage spell is 712 (the desert
    // "you need water, soon!" prompt), which drives the reactive re-raise. When
    // immunityItem is non-zero the counter also carries a passive immunity guard.
    private static RoomHazardIndex.RoomHazard WaterskinHazard(int durationSeconds = 300, int immunityItem = 0) =>
        new(
            new IReadOnlyList<int>[] { new[] { 60 } },
            new[]
            {
                new RoomHazardIndex.BuffCounter(
                    300, 712, durationSeconds, new[] { 60 },
                    immunityItem > 0 ? new[] { immunityItem } : Array.Empty<int>()),
            });

    // The two placeholder-free desert lines the reactive layer keys on: the swig
    // confirmation (buff spell 300) and the lapse-damage prompt (spell 712).
    private const string SwigLine   = "You take a swig of water from your waterskin.";
    private const string ThirstLine = "You suffer in the desert heat... you need water, soon!";

    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int Carried = 1;   // waterskins on hand
        public bool ImmuneCarried = false;   // wearing/holding the immunity guard
        public bool WalkActive = true;
        public bool Following = false;   // in a party, not the leader
        public string? Halted;    // reason from the halt callback, null until halted
        public AutoHazardCounterProvisioner Engine { get; }
        private readonly List<byte[]> _wire = new();
        public AutoModeController Controller { get; }

        public Harness(RoomHazardIndex.RoomHazard? hazard = null, Room? room = null)
        {
            room ??= DesertRoom();
            hazard ??= WaterskinHazard();
            Func<int, Func<string, bool>?> matcher = spell =>
            {
                if (spell == 300)
                    return line => line.Contains("swig of water", StringComparison.OrdinalIgnoreCase);
                if (spell == 712)
                    return line => line.Contains("you need water", StringComparison.OrdinalIgnoreCase);
                return null;
            };
            Engine = new AutoHazardCounterProvisioner(
                resolveRoom:    key => key == room.Key ? room : null,
                hazardForSpell: spell => spell == room.Spell ? hazard : null,
                carriedCount:   id => id == 60 ? Carried : (id == ImmunityItem && ImmuneCarried ? 1 : 0),
                itemName:       id => id == 60 ? "waterskin" : (id == ImmunityItem ? "sunstone wristband" : null),
                messageMatcherForSpell: matcher,
                walkActive:     () => WalkActive,
                haltWalk:       reason => Halted = reason,
                now:            () => Now,
                followingLeader: () => Following);
            Engine.SetWireSender(_wire.Add);
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            Controller = new AutoModeController(profile);
            Engine.MasterSwitchOff = () => Controller.Blocks("Hazard counter item");
        }

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        public IReadOnlyList<string> Sent => _wire
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();

        public IReadOnlyList<string> Uses => Sent.Where(l => l.StartsWith("use ")).ToList();
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

    // Report -112011: a worn sunstone grants full immunity, so raising the waterskin
    // buff is a pointless charge. Holding an immunity guard skips the `use` entirely.
    [Fact]
    public void ImmunityGuardCarried_SkipsUse()
    {
        Harness h = new(WaterskinHazard(immunityItem: ImmunityItem)) { ImmuneCarried = true };
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Empty(h.Sent);
    }

    // The guard only suppresses when actually held — configured but not carried, the
    // provisioner still raises the buff off the waterskin as before.
    [Fact]
    public void ImmunityGuardNotCarried_StillRaises()
    {
        Harness h = new(WaterskinHazard(immunityItem: ImmunityItem)) { ImmuneCarried = false };
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Equal(new[] { "use waterskin" }, h.Sent);
    }

    // A lapse prompt seen while immune neither re-`use`s nor halts — we're safe.
    [Fact]
    public void ImmunityGuardCarried_LapsePromptIgnored_NoHalt()
    {
        Harness h = new(WaterskinHazard(immunityItem: ImmunityItem)) { ImmuneCarried = true };
        h.Engine.OnApproachingRoom(HazardRoom);   // armed, no use (immune)
        h.Engine.OnServerLine(ThirstLine);        // lapse prompt
        Assert.Empty(h.Sent);
        Assert.Null(h.Halted);
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

    // Reactive path: the buff ships no wear-off message, so the timer estimates the
    // lapse. When it drops early the room re-emits the thirst prompt; seeing it (and
    // the swig having confirmed the first `use`), we fire exactly ONE re-raise —
    // even though the refresh timer still thinks the buff is up.
    [Fact]
    public void ThirstPrompt_AfterSwig_ReRaisesOnce()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);   // predictive use (#1), awaiting swig
        h.Engine.OnServerLine(SwigLine);          // swig confirms — charge drawn
        h.Advance(10);                            // still inside the 285s timer window
        h.Engine.OnServerLine(ThirstLine);        // buff lapsed early → reactive use (#2)
        Assert.Equal(2, h.Sent.Count);
        Assert.Null(h.Halted);
    }

    // A thirst prompt arriving before the swig confirmation means the `use` drew
    // nothing — out of charges. We halt rather than march deeper into the hazard.
    [Fact]
    public void ThirstPrompt_WithNoSwig_HaltsWalk()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);   // predictive use (#1), awaiting swig
        h.Engine.OnServerLine(ThirstLine);        // no swig seen → out of charges
        Assert.Single(h.Uses);                    // no second `use` fired into the void
        Assert.Equal(".I'm out of waterskins!", h.Sent[^1]);
        Assert.NotNull(h.Halted);
    }

    // Full reactive cycle: raise, confirm, re-raise on lapse, then a SECOND lapse
    // with no swig for the re-raise → out of charges → halt.
    [Fact]
    public void SecondThirst_WithNoSwig_HaltsWalk()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);   // use #1
        h.Engine.OnServerLine(SwigLine);          // confirms #1
        h.Engine.OnServerLine(ThirstLine);        // lapse → use #2, awaiting swig
        h.Engine.OnServerLine(ThirstLine);        // no swig for #2 → halt
        Assert.Equal(2, h.Uses.Count);
        Assert.NotNull(h.Halted);
    }

    // A thirst prompt with nothing carried to re-raise the buff halts — the route
    // can no longer counter the hazard it's standing in.
    [Fact]
    public void ThirstPrompt_NothingCarried_HaltsWithoutUse()
    {
        Harness h = new() { Carried = 0 };
        h.Engine.OnApproachingRoom(HazardRoom);   // armed, but nothing to `use`
        h.Engine.OnServerLine(ThirstLine);
        Assert.Empty(h.Uses);
        Assert.Equal(new[] { ".I'm out of waterskins!" }, h.Sent);
        Assert.NotNull(h.Halted);
    }

    // A lapse line seen while no walk is running is the player merely standing in
    // the room — not our route committing to cross it. Stay inert.
    [Fact]
    public void ThirstPrompt_WalkInactive_NoAction()
    {
        Harness h = new();
        h.Engine.OnApproachingRoom(HazardRoom);   // use #1 while walking
        h.Engine.OnServerLine(SwigLine);          // confirms #1
        h.WalkActive = false;                     // walk ended
        h.Engine.OnServerLine(ThirstLine);        // stray line — ignored
        Assert.Single(h.Sent);
        Assert.Null(h.Halted);
    }

    // ----- following a party leader (no walk of our own) -------------------

    // The leader's route carries a follower into the desert; with no walk of its own
    // the approach hook never fires, so it raises on arrival instead.
    [Fact]
    public void Follower_ArrivingInHazardRoom_RaisesBuff()
    {
        Harness h = new() { WalkActive = false, Following = true };
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Equal(new[] { "use waterskin" }, h.Sent);
    }

    [Fact]
    public void Follower_NextHazardRoom_WithinWindow_SpendsNoSecondCharge()
    {
        Harness h = new() { WalkActive = false, Following = true };
        h.Engine.OnArrivedInRoom(HazardRoom);
        h.Advance(30);
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Follower_ThirstPrompt_ReRaises_AndOutOfCharges_OnlyLogs()
    {
        Harness h = new() { WalkActive = false, Following = true };
        h.Engine.OnArrivedInRoom(HazardRoom);     // use #1
        h.Engine.OnServerLine(SwigLine);          // confirmed
        h.Engine.OnServerLine(ThirstLine);        // lapse → use #2
        Assert.Equal(2, h.Sent.Count);
        h.Engine.OnServerLine(ThirstLine);        // no swig for #2 → out of charges
        Assert.Equal(".I'm out of waterskins!", h.Sent[^1]);   // tells the room
        Assert.Null(h.Halted);                    // no walk of ours to halt
        h.Engine.OnServerLine(ThirstLine);        // still out — said once, not every tick
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void NotFollowing_Arrival_DoesNothing()
    {
        // Solo / leading: our own walk's approach hook owns raising; an arrival alone
        // (a hand-typed move) never spends a charge.
        Harness h = new() { WalkActive = false, Following = false };
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Follower_WithOwnWalkRunning_LeavesItToTheApproachHook()
    {
        Harness h = new() { WalkActive = true, Following = true };
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Empty(h.Sent);
    }

    // ----- the master switch (user, 2026-10-10) -----------------------------

    [Fact]
    public void SwitchOff_ApproachingHazardRoom_SendsNothing_AndCountsTheSkip()
    {
        Harness h = new();
        h.Controller.TurnOff();
        h.Engine.OnApproachingRoom(HazardRoom);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Controller.SkippedSinceOff["Hazard counter item"]);
    }

    [Fact]
    public void SwitchOff_FollowerArriving_SendsNothing_AndCountsTheSkip()
    {
        Harness h = new() { WalkActive = false, Following = true };
        h.Controller.TurnOff();
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Controller.SkippedSinceOff["Hazard counter item"]);
    }

    // Off, a lapse prompt neither re-raises, nor tells the room, nor halts the walk.
    [Fact]
    public void SwitchOff_ThirstPrompt_NoUse_NoSay_NoHalt()
    {
        Harness h = new() { Carried = 0 };
        h.Engine.OnApproachingRoom(HazardRoom);   // armed while on
        h.Controller.TurnOff();
        h.Engine.OnServerLine(ThirstLine);
        Assert.Empty(h.Sent);
        Assert.Null(h.Halted);
        Assert.Equal(1, h.Controller.SkippedSinceOff["Hazard counter item"]);
    }

    // A skip stamps nothing: back on, the same arrival uses the item once, and the
    // out-of-items say is announced afresh rather than believed already made.
    [Fact]
    public void SwitchOffThenOn_InTheRoom_UsesItOnce()
    {
        Harness h = new() { WalkActive = false, Following = true };
        h.Controller.TurnOff();
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Empty(h.Sent);
        h.Controller.TurnOn();
        h.Engine.OnArrivedInRoom(HazardRoom);
        Assert.Equal(new[] { "use waterskin" }, h.Sent);
        h.Engine.OnArrivedInRoom(HazardRoom);     // timer now stamped: no second charge
        Assert.Single(h.Sent);
    }

    [Fact]
    public void SwitchOffThenOn_OutOfItems_SaysItOnceOnTheNextLapse()
    {
        Harness h = new() { Carried = 0 };
        h.Engine.OnApproachingRoom(HazardRoom);
        h.Controller.TurnOff();
        h.Engine.OnServerLine(ThirstLine);
        Assert.Empty(h.Sent);
        h.Controller.TurnOn();
        h.Engine.OnServerLine(ThirstLine);
        Assert.Equal(new[] { ".I'm out of waterskins!" }, h.Sent);
        Assert.NotNull(h.Halted);
    }
}
