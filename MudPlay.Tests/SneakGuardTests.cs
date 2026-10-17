using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Stealth;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Sneak keeping (user, 2026-09-28): a room we fight in holds sneak-ending automation
// until the backstab fires; sneaking past NPCs we won't fight holds it until a room
// with none; our sneaked move in flight holds it until it lands. Plain
// commands queue and go out once the hold lifts; gear swaps re-run.
public sealed class SneakGuardTests
{
    private sealed class World
    {
        public bool AutoSneak = true, BackstabOwed, MoveInFlight, Npc, Fighting, InCombat, Stealthed = true;
        public readonly List<string> Sent = new();
        public readonly SneakGuard Guard;

        public World()
        {
            Guard = new SneakGuard(() => AutoSneak, () => BackstabOwed, () => MoveInFlight,
                () => Npc, () => Fighting, () => InCombat, () => Stealthed);
            Guard.SetWireSender(Sent.Add);
        }
    }

    [Fact]
    public void BackstabOwed_HoldsUntilItFires()
    {
        World w = new() { BackstabOwed = true, Npc = true, Fighting = true };
        Assert.Equal(SneakHold.UntilBackstab, w.Guard.Current);
        w.BackstabOwed = false;
        Assert.Equal(SneakHold.None, w.Guard.Current);   // fighting now; nothing to keep
    }

    // ShadowResting with a monster in the room — one combat would engage, Auto-Sneak
    // on or off — holds anything that ends the sneak until the rest is done (user,
    // 2026-09-30; report paradigm-20260930-193005).
    [Fact]
    public void ShadowRestBesideAMonster_HoldsUntilRested()
    {
        bool resting = true;
        World w = new() { AutoSneak = false, Npc = true, Fighting = true };
        w.Guard.SetShadowRestProbe(() => resting);
        Assert.Equal(SneakHold.UntilRested, w.Guard.Current);
        Assert.True(w.Guard.TakeIfHeld("invite bob"));

        resting = false;                               // rest-max, or a monster attacked
        w.Guard.Poll();
        Assert.Equal(SneakHold.None, w.Guard.Current);
    }

    // With the master switch off Auto Sneak reads off, which would lift the hold
    // and send everything held. The queue is kept instead and goes out, as it
    // would have, once the switch is back on and the hold really lifts.
    [Fact]
    public void MasterSwitchOff_HeldCommandsAreKept_NotFlushed()
    {
        World w = new() { Npc = true };
        bool off = false;
        w.Guard.MasterSwitchOff = () => off;
        w.Guard.Poll();
        Assert.True(w.Guard.TakeIfHeld("invite Raijin"));

        off = true;
        w.AutoSneak = false;
        w.Guard.Poll();
        Assert.Empty(w.Sent);

        off = false;
        w.AutoSneak = true;
        w.Guard.Poll();
        Assert.Empty(w.Sent);          // still sneaking past the NPC: still held
        w.Npc = false;
        w.Guard.Poll();
        Assert.Equal(new[] { "invite Raijin" }, w.Sent);
    }

    [Fact]
    public void ShadowRestInAnEmptyRoom_HoldsNothing()
    {
        World w = new() { Npc = false };
        w.Guard.SetShadowRestProbe(() => true);
        Assert.Equal(SneakHold.None, w.Guard.Current);
    }

    [Fact]
    public void SneakingPastNpcs_HoldsUntilARoomWithNone()
    {
        World w = new() { Npc = true };
        Assert.Equal(SneakHold.UntilClearRoom, w.Guard.Current);
        w.Npc = false;
        Assert.Equal(SneakHold.None, w.Guard.Current);
    }

    // A command sent while our sneaked move is in flight lands in the room we're
    // entering (report paradigm-20260927-121050).
    [Fact]
    public void MoveInFlight_Holds()
    {
        World w = new() { MoveInFlight = true };
        Assert.Equal(SneakHold.MoveInFlight, w.Guard.Current);
    }

    [Fact]
    public void NothingHeld_WhenTheSneakIsAlreadyGone_OrWeAreFighting()
    {
        Assert.Equal(SneakHold.None, new World { Npc = true, Stealthed = false }.Guard.Current);
        Assert.Equal(SneakHold.None, new World { Npc = true, InCombat = true }.Guard.Current);
        Assert.Equal(SneakHold.None, new World { Npc = true, AutoSneak = false }.Guard.Current);
    }

    [Fact]
    public void QueuedCommands_GoOutWhenTheHoldLifts()
    {
        World w = new() { Npc = true };
        w.Guard.Poll();
        Assert.True(w.Guard.TakeIfHeld("invite Raijin"));
        Assert.Empty(w.Sent);

        int released = 0;
        w.Guard.Released += () => released++;
        w.Npc = false;
        w.Guard.Poll();

        Assert.Equal(new[] { "invite Raijin" }, w.Sent);
        Assert.Equal(1, released);
        Assert.False(w.Guard.TakeIfHeld("invite Suijin"));   // nothing held: send now
    }

    [Theory]
    [InlineData("wear plate mail", true)]
    [InlineData("rem torch", true)]
    [InlineData("sea", true)]
    [InlineData("open north", true)]
    [InlineData("bash n", true)]
    [InlineData(".@poisoned", true)]
    [InlineData(">Raijin {ok}", true)]
    [InlineData("rest", true)]
    [InlineData("get sword", false)]
    [InlineData("/Raijin {ok}", false)]
    [InlineData("bg hello", false)]
    [InlineData("n", false)]
    public void EndsSneak_FollowsTheEngineList(string command, bool ends) =>
        Assert.Equal(ends, SneakBreakingCommands.EndsSneak(command));

    // A `use` ends a sneak when the item's spell is cast, which takes the pack and the
    // game data to answer (UseEndsSneakTests). Asked with neither, it counts as ended.
    [Theory]
    [InlineData("use torch")]
    [InlineData("read scroll")]
    [InlineData("eat ration")]
    [InlineData("drink waterskin")]
    [InlineData("light torch")]
    public void EndsSneak_ItemCommandWithNoItemRule_CountsAsEnded(string command) =>
        Assert.True(SneakBreakingCommands.EndsSneak(command));

    [Fact]
    public void ShadowRest_KeepsTheSneakThroughARest()
    {
        Assert.False(SneakBreakingCommands.EndsSneak("rest", shadowRest: true));
        Assert.True(SneakBreakingCommands.EndsSneak("wear plate mail", shadowRest: true));
    }

    [Theory]
    [InlineData("invite Raijin", true)]
    [InlineData(".@poisoned", true)]
    [InlineData(".Level 20!", true)]
    [InlineData(".@party vortex", false)]   // a teleport step's relay goes with the step
    [InlineData(".@trap north", false)]
    [InlineData(".@panic", false)]
    [InlineData("wear plate mail", false)]  // gear re-derives; it isn't replayed
    public void CanWait_OnlyPlainCommands(string command, bool canWait) =>
        Assert.Equal(canWait, SneakBreakingCommands.CanWait(command));

    // A `break` or an attack a party member relays (`@do`, `@party`) sets or lifts
    // the user's break hold as it goes out, with the member's name read off the
    // handler while it is sending. A line taken for later goes out when that name is
    // gone, so none of these may ever wait.
    [Theory]
    [InlineData("break")]
    [InlineData("bre")]
    [InlineData("a giant rat")]
    [InlineData("attack giant rat")]
    [InlineData("bash giant rat")]
    [InlineData("kick giant rat")]
    [InlineData("bs giant rat")]
    [InlineData("mmis giant rat")]
    public void CanWait_NeverABreakOrAnAttack(string command) =>
        Assert.False(SneakBreakingCommands.CanWait(command));

    [Fact]
    public void Equipment_HeldSwap_ReRunsWhenReleased()
    {
        bool held = true;
        var snapshot = InventorySnapshot.Empty with
        {
            CarriedItems = new[] { "silver dagger" },
            LastUpdated = System.DateTimeOffset.UtcNow,
        };
        var mgr = new EquipmentManager(
            readEquipment: () => new EquipmentSettings(),
            getSnapshot: () => snapshot,
            readCombat: () => new CombatSettings(),
            writeCombat: _ => { });
        List<byte[]> sent = new();
        mgr.SetWireSender(sent.Add);
        mgr.SetGearHold(() => held);

        mgr.SwapWeapon("silver dagger", null);
        Assert.Empty(sent);
        Assert.Contains("weapon", mgr.HeldGearKinds);

        held = false;
        mgr.RunHeldGear();
        Assert.Equal(new[] { "eq silver dagger" },
            sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
    }
}
