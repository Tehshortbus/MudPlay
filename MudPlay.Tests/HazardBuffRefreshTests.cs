using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Game.Stealth;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A hazard counter's buff (the desert waterskin) is tracked quietly and raised again
// shortly before it runs out, not sooner, and only where it matters (user,
// 2026-10-10). Inside its window the `use` is sent by the between-round cast pass,
// which holds it for sneak keeping like any buff; at the last call it goes out where
// the character stands.
public sealed class HazardBuffRefreshTests : IDisposable
{
    private readonly string _root;

    public HazardBuffRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-hazard-buff-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly RoomKey Oasis = new(1, 1);
    private static readonly RoomKey Dunes = new(1, 2);
    private static readonly RoomKey DeepDesert = new(1, 3);
    // Their own spells: negate magic (#310), and the drowning cure (#515), which
    // removes two spells by number and nothing else.
    private static readonly RoomKey Crypt = new(1, 4);
    private static readonly RoomKey Shallows = new(1, 5);
    private const int NegateMagic = 310;
    private const int StopDrowning = 515;
    // The Bone Room's command textblock: `enter tapestry` casts negate magic and
    // then teleports.
    private const int TapestryCommands = 9498;

    private const int Waterskin = 60;
    private const int BuffSpell = 300;
    private const int HeatSpell = 712;
    private const string SwigLine = "You take a swig of water from your waterskin.";
    private const string ThirstLine = "You suffer in the desert heat... you need water, soon!";

    // The buff lasts 1800 s, as the waterskin's does: its window opens at 1740 s and
    // its last call comes at 1785 s.
    private const int Lasts = 1800;

    private static Room Desert(RoomKey key) => new()
    {
        Key = key, Name = "Scorching Desert", Spell = 700, Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static Room Casting(RoomKey key, int spell) => new()
    {
        Key = key, Name = "Elsewhere", Spell = spell, Exits = new Dictionary<Direction, RoomExit>(),
    };

    private static readonly RoomHazardIndex.BuffCounter Counter =
        new(BuffSpell, HeatSpell, Lasts, new[] { Waterskin }, Array.Empty<int>());

    private static readonly RoomHazardIndex.RoomHazard Heat =
        new(new IReadOnlyList<int>[] { new[] { Waterskin } }, new[] { Counter });

    // The provisioner with everything around it a test can turn.
    private sealed class Field
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public int Carried = 1;
        public int? Charges;
        public bool WalkActive = true;
        public bool Following;
        public bool SneakKept;
        public bool SendBlocked;
        public bool AutoSneak = true;
        public List<RoomKey> RoomsAhead = new() { Dunes };
        public List<string> Wire { get; } = new();
        // The gate a step is held on, and the one-shot timers its caps were given.
        public MovementCoordinator Coordinator { get; } = new();
        public List<(TimeSpan After, Action Run)> Timers { get; } = new();
        public int UsesSent;
        public int SneakSpent;
        public LogService Log { get; } = new();
        public AutoModeController Switch { get; }
        public AutoHazardCounterProvisioner Engine { get; }

        public Field()
        {
            Engine = new AutoHazardCounterProvisioner(
                resolveRoom: key => key == Dunes || key == DeepDesert ? Desert(key)
                    : key == Crypt ? Casting(key, NegateMagic)
                    : key == Shallows ? Casting(key, StopDrowning)
                    : null,
                hazardForSpell: spell => spell == 700 ? Heat : null,
                carriedCount: id => id == Waterskin ? Carried : 0,
                itemName: id => id == Waterskin ? "waterskin" : null,
                messageMatcherForSpell: spell => spell switch
                {
                    BuffSpell => line => line.Contains("swig of water", StringComparison.OrdinalIgnoreCase),
                    // The seed's wording for #712, in both realms' message data.
                    HeatSpell => line => line.Contains("You suffer in the desert heat...", StringComparison.OrdinalIgnoreCase),
                    _ => null,
                },
                walkActive: () => WalkActive,
                now: () => Now,
                log: Log,
                followingLeader: () => Following,
                roomsAhead: () => RoomsAhead,
                allCounters: () => new[] { Counter },
                sneakKept: () => SneakKept,
                chargesLeft: _ => Charges,
                sendBlocked: () => SendBlocked,
                autoSneakOn: () => AutoSneak,
                roomStripsBuff: (roomSpell, buffSpell) =>
                    roomSpell == NegateMagic || (roomSpell == StopDrowning && buffSpell is 512 or 513),
                commandStripsBuff: (roomCmd, command, _) =>
                    roomCmd == TapestryCommands && command.Trim().Equals("enter tapestry", StringComparison.OrdinalIgnoreCase));
            Engine.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Engine.SetStepHold(Coordinator, (after, run) => Timers.Add((after, run)));
            Engine.UseSent += () => UsesSent++;
            Engine.SneakSpentHere += () => SneakSpent++;
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            Switch = new AutoModeController(profile);
            Engine.MasterSwitchOff = () => Switch.Blocks("Hazard counter item");
        }

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        public IReadOnlyList<string> LogLines => Log.Snapshot().Select(e => e.Message).ToList();
    }

    // ----- tracking -------------------------------------------------------

    // The buff's own line starts its clock wherever it shows, for a `use` typed by
    // hand as for one of ours, so the step into the desert spends nothing more.
    [Fact]
    public void BuffLine_SeenAnywhere_StartsTheClock()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);            // typed by hand, far from the desert

        f.Advance(600);
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Empty(f.Wire);
        Assert.Null(f.Engine.DueNow());
    }

    // A death wipes it, and across a dropped link or a new profile nothing can be
    // vouched for: unknown counts as off when a hazard room is next.
    [Fact]
    public void Forget_CountsTheBuffAsOff_WhenAHazardRoomIsNext()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        Assert.Single(f.Wire);

        f.Engine.Forget("death");
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(2, f.Wire.Count);
        Assert.Contains(f.LogLines, l => l.Contains("buff tracking reset (death)"));
    }

    // "buffs survive disconnects, relogs", and "as soon as we've disconnected all
    // timers need to be paused until we're back in the game, then they all resume"
    // (user, 2026-10-10): the time away is not taken off the buff.
    [Fact]
    public void DroppedLink_StopsTheClock_AndItRunsOnWithWhatItHadLeft()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Advance(1000);                            // 800 s left

        f.Engine.Pause();
        f.Advance(5000);                            // away for longer than the buff lasts
        Assert.Null(f.Engine.DueNow());
        Assert.Contains("800 s left when it paused", f.Engine.DescribeTracking()[1]);

        f.Engine.Resume();
        Assert.Null(f.Engine.DueNow());             // still 800 s
        f.Advance(739);
        Assert.Null(f.Engine.DueNow());
        f.Advance(1);                               // 60 s left: the window opens
        Assert.Equal(("use waterskin", false), f.Engine.DueNow());
        Assert.Contains(f.LogLines, l => l.StartsWith("buff clock paused (the link dropped) — buff 300 has 800 s left"));
        Assert.Contains(f.LogLines, l => l.StartsWith("buff clock resumed after 5000 s away — buff 300 has 800 s left"));
    }

    // Until the first prompt back in the game nothing is sent: what is on the screen
    // is the board's login, not the game.
    [Fact]
    public void WhileTheLinkIsDown_NothingIsSent()
    {
        Field f = new();
        f.Engine.Pause();

        f.Engine.OnApproachingRoom(Dunes);
        Assert.False(f.Engine.FireDue());
        Assert.Empty(f.Wire);

        f.Engine.Resume();
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Single(f.Wire);
    }

    // The thirst line can show on the way back in, ahead of the first prompt. It is
    // the game's word that the buff is off, and is taken; its `use` waits until we are
    // back, like everything else.
    [Fact]
    public void ThirstLineWhileTheLinkIsDown_CountsTheBuffAsOff_AndSendsNothing()
    {
        Field f = new() { AutoSneak = false };
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.Engine.Pause();
        f.Advance(600);

        f.Engine.OnServerLine(ThirstLine);
        Assert.Single(f.Wire);

        f.Engine.Resume();
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    // A buff whose line shows while the clock is stopped started then: the time away
    // is not added to it on top.
    [Fact]
    public void ABuffRaisedOnTheWayBackIn_IsNotCreditedWithTheTimeAway()
    {
        Field f = new() { AutoSneak = false };
        f.Engine.OnServerLine(SwigLine);
        f.Engine.Pause();
        f.Advance(600);
        f.Engine.OnServerLine(SwigLine);            // drunk by hand at the first room display
        f.Engine.Resume();

        f.Advance(1784);
        Assert.Null(f.Engine.DueNow());
        f.Advance(1);                               // 1785 s after that drink, not 2385
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    // A death while the link was down (a hang-up death, worked out at the login)
    // still ends the buff.
    [Fact]
    public void ADeathWhileTheLinkWasDown_EndsTheBuff()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Engine.Pause();

        f.Engine.Forget("death");
        f.Engine.Resume();
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(new[] { "use waterskin" }, f.Wire);
    }

    // "dying or walking through a room that purges buffs, negate magic" (user,
    // 2026-10-10): the buff has no wear-off line, so the room's own spell says it.
    [Fact]
    public void ARoomWhoseSpellDispels_EndsTheTrackedBuff()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Engine.OnArrivedInRoom(Crypt);

        Assert.Empty(f.Engine.DescribeTracking());
        Assert.Contains(f.LogLines, l => l.StartsWith("buff 300 taken off by 1/4 (Elsewhere), whose spell strips it"));
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
    }

    // A room that removes other spells by number takes nothing off this one: the
    // drowning cure's rooms are on the desert's own map.
    [Fact]
    public void ARoomThatOnlyRemovesOtherSpells_LeavesTheBuffOn()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Engine.OnArrivedInRoom(Shallows);
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Empty(f.Wire);
        Assert.Single(f.Engine.DescribeTracking());
    }

    // The index answers for one buff: a dispel takes everything, a RemovesSpell only
    // what it names.
    [Fact]
    public void StripIndex_ADispelTakesAnyBuff_ARemoveOnlyTheSpellsItNames()
    {
        string dir = Path.Combine(_root, "strip");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), """
            [ { "Map Number": 1, "Room Number": 4, "Name": "Crypt", "Spell": 310 },
              { "Map Number": 12, "Room Number": 5, "Name": "Shallows", "Spell": 515 },
              { "Map Number": 12, "Room Number": 6, "Name": "Scorching Desert", "Spell": 683 } ]
            """);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), """
            [ { "Number": 310, "Abil-0": 73, "AbilVal-0": 0 },
              { "Number": 515, "Abil-0": 122, "AbilVal-0": 512, "Abil-1": 122, "AbilVal-1": 513 },
              { "Number": 683, "Abil-0": 148, "AbilVal-0": 2653 } ]
            """);
        GameDataCache cache = new(_root);
        cache.SwitchSet("strip");
        RoomBuffStripIndex index = new(cache);
        index.OnActiveSetChanged("strip");

        Assert.True(index.StripsBuff(310, 711));
        Assert.True(index.StripsBuffs(515));
        Assert.False(index.StripsBuff(515, 711));
        Assert.True(index.StripsBuff(515, 512));
        Assert.False(index.StripsBuff(683, 711));
    }

    // A room command can cast the dispel too (asked whether those should end the
    // tracked buff: "yes", user, 2026-10-10). The command going out in that room is
    // what is judged; any other command there leaves the buff alone.
    [Fact]
    public void ARoomCommandThatCastsADispel_EndsTheTrackedBuff()
    {
        Room boneRoom = new()
        {
            Key = new RoomKey(17, 1772), Name = "Bone Room", Cmd = TapestryCommands,
            Exits = new Dictionary<Direction, RoomExit>(),
        };
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Engine.OnRoomCommandSent(boneRoom, "look tapestry");
        Assert.Single(f.Engine.DescribeTracking());

        f.Engine.OnRoomCommandSent(boneRoom, "enter tapestry");
        Assert.Empty(f.Engine.DescribeTracking());
        Assert.Contains(f.LogLines, l => l.StartsWith("buff 300 taken off by `enter tapestry` in 17/1772 (Bone Room), a command that casts a dispel"));
    }

    // What the index reads for it: the casts of a room command's line and of a room
    // spell's textblock, each through the blocks they hand over to. The cast pass's
    // own "this room strips buffs" is asked of what the room spell does of itself,
    // and stays as it was.
    [Fact]
    public void StripIndex_ReadsDispelsCastByARoomCommandAndByARoomSpellsTextblock()
    {
        string dir = Path.Combine(_root, "casts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), """
            [ { "Map Number": 17, "Room Number": 1772, "Name": "Bone Room", "CMD": 9498 },
              { "Map Number": 15, "Room Number": 1112, "Name": "Grand Altar", "CMD": 4011 },
              { "Map Number": 1, "Room Number": 5, "Name": "Healer", "CMD": 159 },
              { "Map Number": 17, "Room Number": 2980, "Name": "Ancient Fortress", "Spell": 1361 } ]
            """);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), """
            [ { "Number": 310, "Abil-0": 73, "AbilVal-0": 0 },
              { "Number": 645, "Abil-0": 73, "AbilVal-0": 0 },
              { "Number": 223, "Abil-0": 122, "AbilVal-0": 143 },
              { "Number": 1361, "Abil-0": 148, "AbilVal-0": 0, "MinBase": 4120, "MaxBase": 4120 } ]
            """);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), """
            [ { "Number": 9498, "Action": "enter tapestry:needmonster 1003 1373:message 3148:cast 310:teleport 1773 17:message 837\ngo tapestry:needmonster 1003 1373:message 3148:cast 310:teleport 1773 17:message 837" },
              { "Number": 4011, "Action": "touch ruby:message 541:random 4013" },
              { "Number": 4013, "Action": "cast 645:message 542" },
              { "Number": 159, "Action": "buy cure disease:price 100 1560:cast 223" },
              { "Number": 4120, "Action": "failroomitem 1891:clearitem 1958:cast 645:teleport 3090 17:summon 1016" } ]
            """);
        GameDataCache cache = new(_root);
        cache.SwitchSet("casts");
        RoomBuffStripIndex index = new(cache);
        index.OnActiveSetChanged("casts");

        Assert.True(index.CommandStripsBuff(9498, "enter tapestry", 711));
        Assert.True(index.CommandStripsBuff(9498, " GO TAPESTRY ", 711));
        Assert.False(index.CommandStripsBuff(9498, "look tapestry", 711));
        Assert.True(index.CommandStripsBuff(4011, "touch ruby", 711));          // through `random`
        Assert.False(index.CommandStripsBuff(159, "buy cure disease", 711));    // removes other spells by number
        Assert.True(index.CommandStripsBuff(159, "buy cure disease", 143));

        Assert.True(index.StripsBuff(1361, 711));
        Assert.False(index.StripsBuffs(1361));
    }

    // A transport token casts negate magic ahead of its teleport.
    [Fact]
    public void NegateMagicFromAToken_EndsTheTrackedBuff()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Engine.NoteNegateMagic("token of Silvermere");

        Assert.Empty(f.Engine.DescribeTracking());
        Assert.Contains(f.LogLines, l => l.StartsWith("buff tracking reset (negate magic: token of Silvermere)"));
    }

    [Fact]
    public void Tracking_IsDescribed_ForTheBugReport()
    {
        Field f = new();
        Assert.Empty(f.Engine.DescribeTracking());

        f.Engine.OnApproachingRoom(Dunes);
        IReadOnlyList<string> described = f.Engine.DescribeTracking();
        string sent = described[0];
        Assert.Equal("the step waits for the answer to a `use`", described[1]);
        Assert.Contains("buff 300: on since", sent);
        Assert.Contains("a `use` went out; its line not seen", sent);
        Assert.Contains("in 1800 s", sent);
        Assert.Contains("refresh window 60 s, last call 15 s", sent);

        f.Engine.OnServerLine(SwigLine);
        Assert.Contains("its line was seen", Assert.Single(f.Engine.DescribeTracking()));
    }

    // ----- timing: before it wears off, but not too soon ---------------------

    // The window buys the search for a room with no NPCs, so it comes with
    // Auto-Sneak. Without it the refresh keeps to the last 15 s, and then it can't
    // wait.
    [Theory]
    [InlineData(1740, false)]
    [InlineData(1784, false)]
    [InlineData(1785, true)]
    [InlineData(1900, true)]
    public void AutoSneakOff_TheRefreshIsDueOnlyAtTheLastCall(int secondsOn, bool due)
    {
        Field f = new() { AutoSneak = false };
        f.Engine.OnServerLine(SwigLine);

        f.Advance(secondsOn);

        Assert.Equal(due ? ("use waterskin", true) : null, f.Engine.DueNow());
    }

    // Nor is anything looked ahead for: with the desert a step away and the buff off
    // nothing is on offer, and the first drink goes out at the edge, on the step.
    [Fact]
    public void AutoSneakOff_NoLookAhead_TheFirstDrinkGoesOutAtTheEdge()
    {
        Field f = new() { AutoSneak = false, RoomsAhead = new() { Oasis, Dunes } };

        Assert.Null(f.Engine.DueNow());
        Assert.False(f.Engine.FireDue());
        Assert.Empty(f.Wire);

        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Contains(f.LogLines, l => l.Contains("refreshed in its last 15 s (Auto-Sneak off"));
    }

    // Standing in the desert with Auto-Sneak on and the buff up: nothing is due until
    // 60 s before it ends, it can wait until 15 s before, and then it can't.
    [Theory]
    [InlineData(1739, false, false)]
    [InlineData(1740, true, false)]
    [InlineData(1784, true, false)]
    [InlineData(1785, true, true)]
    [InlineData(1900, true, true)]
    public void InTheDesert_TheRefreshIsDueOnlyInsideItsWindow(int secondsOn, bool due, bool mustGoNow)
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Advance(secondsOn);

        (string What, bool MustGoNow)? offer = f.Engine.DueNow();
        Assert.Equal(due, offer is not null);
        if (offer is { } o)
        {
            Assert.Equal("use waterskin", o.What);
            Assert.Equal(mustGoNow, o.MustGoNow);
        }
    }

    // Outside hazard country nothing is refreshed at all, whatever the clock says.
    [Fact]
    public void NoCounteredRoomHereOrAhead_NothingIsDue()
    {
        Field f = new() { RoomsAhead = new() { Oasis } };
        Assert.Null(f.Engine.DueNow());             // off, and it doesn't matter here

        f.Engine.OnServerLine(SwigLine);
        f.Advance(1790);
        Assert.Null(f.Engine.DueNow());             // about to run out, and it doesn't matter here
    }

    // A countered room a few planned steps ahead makes it matter. With the buff off
    // the `use` is due, and can wait for a good moment: the step into the desert is
    // its own last call.
    [Fact]
    public void CounteredRoomAhead_BuffOff_IsDue_AndCanWait()
    {
        Field f = new() { RoomsAhead = new() { Oasis, Oasis, Dunes } };

        (string What, bool MustGoNow)? offer = f.Engine.DueNow();

        Assert.Equal(("use waterskin", false), offer);
        Assert.Contains(f.LogLines, l => l.StartsWith("due: `use waterskin` — the buff is off or not known to be on"));
    }

    // Standing in a countered room by hand, with no walk or loop and no leader: "if
    // auto master toggle is on, yes, if off, no" (user, 2026-10-10). Same clock, same
    // windows.
    [Theory]
    [InlineData(true, 1739, false, false)]
    [InlineData(true, 1740, true, false)]
    [InlineData(true, 1785, true, true)]
    [InlineData(false, 1784, false, false)]
    [InlineData(false, 1785, true, true)]
    public void StandingInACounteredRoomByHand_TheRefreshRunsOnTheSameClock(
        bool autoSneak, int secondsOn, bool due, bool mustGoNow)
    {
        Field f = new() { WalkActive = false, AutoSneak = autoSneak };
        f.Engine.OnServerLine(SwigLine);

        f.Advance(secondsOn);

        Assert.Equal(due ? ("use waterskin", mustGoNow) : null, f.Engine.DueNow());
    }

    // A follower carried into the desert drinks on arrival, and the same clock then
    // keeps the buff up for it.
    [Fact]
    public void AFollower_DrinksOnArrival_AndIsKeptUpByTheSameClock()
    {
        Field f = new() { WalkActive = false, Following = true };

        f.Engine.OnArrivedInRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Null(f.Engine.DueNow());

        f.Advance(1785);
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    [Fact]
    public void StandingByHand_TheSwitchDecides_AndElsewhereNothingIsDue()
    {
        Field f = new() { WalkActive = false };

        f.Switch.TurnOff();
        Assert.False(f.Engine.FireDue());
        Assert.Empty(f.Wire);

        f.Switch.TurnOn();
        Assert.True(f.Engine.FireDue());
        Assert.Equal(new[] { "use waterskin" }, f.Wire);

        f.Engine.Forget("test");
        f.RoomsAhead = new() { Oasis };
        Assert.Null(f.Engine.DueNow());
    }

    // The lapse line with nothing moving the character: the line sends nothing of
    // its own (the say and the halt are a walk's), the buff counts as off, and the
    // refresh is due where the character stands.
    [Fact]
    public void LapseLine_StandingByHand_CountsTheBuffAsOff_AndTheRefreshIsDue()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);          // walked in
        f.Engine.OnServerLine(SwigLine);
        f.WalkActive = false;                       // and stopped there
        f.Advance(100);

        f.Engine.OnServerLine(ThirstLine);

        Assert.Single(f.Wire);
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    // One `use` per window: once it has gone out nothing more is due until the next.
    [Fact]
    public void OneUsePerWindow()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Advance(1750);

        Assert.True(f.Engine.FireDue());
        Assert.Null(f.Engine.DueNow());
        Assert.False(f.Engine.FireDue());
        f.Engine.OnApproachingRoom(DeepDesert);

        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Equal(1, f.UsesSent);
        Assert.Contains(f.LogLines, l => l.StartsWith("used `use waterskin` for buff 300 1/2 (Scorching Desert)"));
    }

    // The step into a countered room is the last call: with time still on the buff
    // nothing goes out, and inside the last 15 s it does.
    [Fact]
    public void StepIntoACounteredRoom_UsesOnlyAtTheLastCall()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);

        f.Advance(1750);                            // inside the window, not at the last call
        f.Engine.OnApproachingRoom(DeepDesert);
        Assert.Empty(f.Wire);

        f.Advance(40);                              // 10 s left
        f.Engine.OnApproachingRoom(DeepDesert);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
    }

    // ----- a `use` the game turns away ---------------------------------------

    // The round refusal puts the clock back where it was, so the `use` is due again
    // and goes out a round later.
    [Fact]
    public void RoundRefusal_PutsTheClockBack_AndTheUseIsDueAgain()
    {
        Field f = new();
        f.Engine.OnServerLine(SwigLine);
        f.Advance(1750);
        Assert.True(f.Engine.FireDue());

        f.Engine.OnServerLine("You have already cast a spell this round!");

        Assert.NotNull(f.Engine.DueNow());
        Assert.Contains(f.LogLines, l => l.Contains("refused: `use waterskin` for buff 300 — already cast this round"));
        Assert.True(f.Engine.FireDue());
        Assert.Equal(2, f.Wire.Count);
    }

    // The same refusal answering a `use` that could not wait (the step's last call):
    // the game takes one such cast a round, so asking again at once only draws the
    // line again. Nothing more goes out by the step's road; the cast pass is owed it,
    // wherever the character stands, and sends it when it has the next round's cast.
    [Fact]
    public void RoundRefusal_AtTheStep_IsNotAskedAgain_UntilThePassHasTheNextRoundsCast()
    {
        Field f = new() { AutoSneak = false, RoomsAhead = new() { Oasis, Dunes } };
        for (int i = 0; i < 6; i++)
        {
            f.Engine.OnApproachingRoom(Dunes);
            f.Engine.OnServerLine("You have already cast a spell this round!");
        }
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.True(f.Engine.HoldingStep);
        Assert.True(f.Coordinator.IsGateAsserted(MovementCoordinator.HazardBuffGate));
        Assert.Contains(f.Engine.DescribeTracking(), l => l.Contains("the cast pass sends it with the next one, and the step waits for it"));

        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
        Assert.True(f.Engine.FireDue());
        Assert.Equal(2, f.Wire.Count);
        Assert.True(f.Engine.HoldingStep);          // now for that one's answer

        f.Engine.OnServerLine(SwigLine);
        Assert.False(f.Engine.HoldingStep);
        Assert.False(f.Coordinator.IsPaused);
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(2, f.Wire.Count);
    }

    // The round passes and the pass never had a cast for it (the switch went off, a
    // menu had the keyboard): the step is let go, and asks its last call afresh.
    [Fact]
    public void RoundRefusal_AtTheStep_IsBoundedByTheRound()
    {
        Field f = new() { AutoSneak = false };
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine("You have already cast a spell this round!");
        Assert.Equal(TimeSpan.FromSeconds(6), f.Timers[^1].After);

        f.Advance(6);
        f.Timers[^1].Run();

        Assert.False(f.Engine.HoldingStep);
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(2, f.Wire.Count);
    }

    // The step waits for the answer to the `use` sent ahead of it, so it goes in with
    // the buff on; an answer that never comes holds it three seconds and no longer.
    [Fact]
    public void TheStep_WaitsForTheUsesAnswer_AndNoLongerThanItsCap()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        Assert.True(f.Engine.HoldingStep);
        Assert.Equal(TimeSpan.FromSeconds(3), f.Timers[^1].After);

        f.Timers[^1].Run();
        Assert.False(f.Engine.HoldingStep);
        Assert.False(f.Coordinator.IsPaused);

        // A refusal that holds lets the step go as well: there is nothing to wait for.
        Field g = new();
        g.Engine.OnApproachingRoom(Dunes);
        g.Engine.OnServerLine("There are no more uses in waterskin.");
        Assert.False(g.Engine.HoldingStep);

        // A follower has no step of its own to hold.
        Field h = new() { WalkActive = false, Following = true };
        h.Engine.OnArrivedInRoom(Dunes);
        Assert.Single(h.Wire);
        Assert.False(h.Engine.HoldingStep);
    }

    // The thirst line's own `use` keeps to the round as well: refused for the round's
    // cast, the next thirst line sends nothing, and the pass is owed it.
    [Fact]
    public void RoundRefusal_OfTheLapseLinesUse_IsLeftToThePass()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.Advance(100);

        f.Engine.OnServerLine(ThirstLine);
        f.Engine.OnServerLine("You have already cast a spell this round!");
        f.Engine.OnServerLine(ThirstLine);

        Assert.Equal(2, f.Wire.Count);
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    // The game says the item isn't there, or is spent: asking again would only draw
    // the same line, so nothing more is sent until the pack changes.
    [Theory]
    [InlineData("You don't have waterskin.")]
    [InlineData("There are no more uses in waterskin.")]
    [InlineData("You may not use that item!")]
    public void RefusalThatHolds_SendsNothingMore_UntilThePackChanges(string refusal)
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);

        f.Engine.OnServerLine(refusal);
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Null(f.Engine.DueNow());
        Assert.Single(f.Wire);
        Assert.Contains(f.LogLines, l => l.Contains("refused: `use waterskin`") && l.Contains("not sent again until the pack changes"));

        f.Carried = 2;                              // another one picked up
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(2, f.Wire.Count);
    }

    // The spent one leaves the pack and a fresh one arrives: the count is back to
    // what it was when the item was set aside, and the fresh one is used all the same,
    // because the pack was seen without it in between. It used to stay set aside, and
    // the thirst line then said "out of waterskins" with a full one carried.
    [Fact]
    public void SetAsideItem_SeenGoneThenReplaced_IsUsedAgain()
    {
        Field f = new() { Charges = 0 };
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Empty(f.Wire);                       // empty by the client's count: set aside at 1 carried

        f.Carried = 0;                              // dropped or sold
        Assert.Null(f.Engine.DueNow());             // the cast pass's look, a second later
        f.Carried = 1;                              // a fresh one bought
        f.Charges = 3;

        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        f.Engine.OnServerLine(SwigLine);
        Assert.DoesNotContain(f.Wire, w => w.StartsWith(".I'm out of"));
    }

    // One more carried is a changed pack as well.
    [Fact]
    public void SetAsideItem_ASecondOneCarried_IsUsedAgain()
    {
        Field f = new() { Charges = 0 };
        f.Engine.OnApproachingRoom(Dunes);
        f.Carried = 2;
        f.Charges = 3;
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
    }

    // "You don't have ..." answers other commands as well: only one naming the item
    // is taken for the answer to our `use`.
    [Fact]
    public void ARefusalNamingSomethingElse_IsNotOurs()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);

        f.Engine.OnServerLine("You don't have 50 gold crowns to drop!");
        f.Advance(60);
        f.Engine.OnApproachingRoom(Dunes);

        Assert.Single(f.Wire);                      // still counted as on
        Assert.Null(f.Engine.DueNow());
    }

    // The client's own count says the item is empty: nothing is sent.
    [Fact]
    public void NoChargesLeft_IsNotSent()
    {
        Field f = new() { Charges = 0 };

        f.Engine.OnApproachingRoom(Dunes);
        Assert.False(f.Engine.FireDue());

        Assert.Empty(f.Wire);
        Assert.Single(f.LogLines, l => l.Contains("has no charges left for buff 300"));
    }

    // With the send gate up a `use` would be dropped, so none is sent or stamped,
    // and it goes out once the gate is down.
    [Fact]
    public void SendGateUp_NothingIsSentOrStamped()
    {
        Field f = new() { SendBlocked = true };
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Null(f.Engine.DueNow());
        Assert.Empty(f.Wire);

        f.SendBlocked = false;
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Single(f.Wire);
    }

    // ----- the lapse line: the game's own word that the buff is off ------------

    // Paradigm's data has the desert's `failspell` and not the block it jumps to, so
    // nothing in it names the spell that does the damage. The user confirmed it is
    // #712 (2026-10-10). The index takes that from the one list of such follow-ons
    // (RoomSpellDamageClassifier), and only where the data gives nothing.
    private RoomHazardIndex.BuffCounter DesertCounter(string set, string tbInfo)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"),
            """ [ { "Map Number": 12, "Room Number": 853, "Name": "Scorching Desert", "Spell": 683 } ] """);
        File.WriteAllText(Path.Combine(dir, "Spells.json"),
            """ [ { "Number": 683, "Abil-0": 148, "AbilVal-0": 2653 }, { "Number": 711, "Dur": 600 } ] """);
        File.WriteAllText(Path.Combine(dir, "Items.json"),
            """ [ { "Number": 283, "Abil-0": 43, "AbilVal-0": 711 } ] """);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), tbInfo);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        RoomHazardIndex index = new(cache);
        index.OnActiveSetChanged(set);
        return Assert.Single(index.HazardForSpell(683)!.BuffCounters);
    }

    private const string ParadigmDesertBlocks = """ [ { "Number": 2653, "Action": "failspell 711 2654:random 2655" } ] """;

    [Fact]
    public void Paradigm_TheDesertsLapseSpell_IsTheConfirmedFollowOn()
    {
        RoomHazardIndex.BuffCounter counter = DesertCounter("para", ParadigmDesertBlocks);

        Assert.Equal(711, counter.BuffSpell);
        Assert.Equal(712, counter.LapseSpell);
        Assert.Equal(1800, counter.DurationSeconds);
    }

    // A set that has the buff-absent block (Stock) is read from its own data: the
    // confirmed follow-on stands in only where the block is missing.
    [Fact]
    public void TheConfirmedFollowOn_StandsInOnlyWhereTheDataNamesNoLapseSpell()
    {
        Assert.Equal(999, DesertCounter("with-block",
            """ [ { "Number": 2653, "Action": "checkspell 711 2654:random 2655" }, { "Number": 2654, "Action": "cast 999" } ] """)
            .LapseSpell);
    }

    // The line shows with the buff believed on: it is off, whatever the clock said.
    // On a walk the `use` goes out at once, as it does on Stock, and the clock starts
    // again from it.
    [Fact]
    public void LapseLine_WithTheBuffBelievedOn_OnAWalk_DrinksAtOnce()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.Advance(100);

        f.Engine.OnServerLine(ThirstLine);

        Assert.Equal(new[] { "use waterskin", "use waterskin" }, f.Wire);
        Assert.Contains(f.LogLines, l => l.StartsWith("buff 300 is off: the room's lapse line showed with 1700 s still on its clock"));
        Assert.Contains(f.LogLines, l => l.StartsWith("re-raised buff 300 with `use waterskin` on lapse prompt"));
        Assert.Contains("in 1800 s", f.Engine.DescribeTracking()[0]);
    }

    // Where the line's own `use` can't go out (here the master switch is off) the
    // buff still counts as off from that line, so once the switch is back the refresh
    // is due under the same rules as any other: here, and at once.
    [Fact]
    public void LapseLine_WithNothingSent_CountsTheBuffAsOff_AndTheRefreshIsDue()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.Advance(100);
        Assert.Null(f.Engine.DueNow());             // 1700 s on the clock

        f.Switch.TurnOff();
        f.Engine.OnServerLine(ThirstLine);
        Assert.Single(f.Wire);
        Assert.Empty(f.Engine.DescribeTracking());  // no longer tracked as on

        f.Switch.TurnOn();
        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
        Assert.True(f.Engine.FireDue());
        Assert.Equal(2, f.Wire.Count);
    }

    // The lapse line's `use` over a kept sneak goes out all the same: the buff is
    // already off. The sneak is reported spent, as for any forced `use`.
    [Fact]
    public void LapseLine_OverAKeptSneak_DrinksAnyway_AndReportsTheSneakSpent()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.SneakKept = true;

        f.Engine.OnServerLine(ThirstLine);

        Assert.Equal(2, f.Wire.Count);
        Assert.Equal(1, f.SneakSpent);
    }

    // The round's cast was already made when the lapse line's `use` went out: the
    // refusal leaves the buff off (not back on the clock the lapse line disproved),
    // and the `use` is due again.
    [Fact]
    public void LapseLine_ThenTheRoundRefusal_LeavesTheBuffOff()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine(SwigLine);
        f.Advance(100);

        f.Engine.OnServerLine(ThirstLine);
        f.Engine.OnServerLine("You have already cast a spell this round!");

        Assert.Equal(("use waterskin", true), f.Engine.DueNow());
    }

    // The game has already said the item is spent: a lapse line doesn't ask again at
    // every tick. The walk is halted, and the room told once.
    [Fact]
    public void LapseLine_AfterARefusalThatHolds_HaltsInsteadOfAskingAgain()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        f.Engine.OnServerLine("There are no more uses in waterskin.");

        f.Engine.OnServerLine(ThirstLine);
        f.Engine.OnApproachingRoom(Dunes);          // armed again by the next step
        f.Engine.OnServerLine(ThirstLine);

        Assert.Equal(new[] { "use waterskin", ".I'm out of waterskins!" }, f.Wire);
        Assert.Contains(f.LogLines, l => l.Contains("lapsed with nothing left to use in waterskin"));
    }

    // ----- the master switch -------------------------------------------------

    [Fact]
    public void SwitchOff_TheDueUseIsNotFired_AndNothingIsStamped()
    {
        Field f = new();
        f.Switch.TurnOff();

        Assert.False(f.Engine.FireDue());
        Assert.Empty(f.Wire);
        Assert.Equal(1, f.Switch.SkippedSinceOff["Hazard counter item"]);

        f.Switch.TurnOn();
        Assert.True(f.Engine.FireDue());
        Assert.Single(f.Wire);
    }

    // ----- a kept sneak --------------------------------------------------------

    // The last call goes out though a sneak is being kept, and says the sneak is
    // spent here, for whatever reacts to a lost sneak.
    [Fact]
    public void LastCall_OverAKeptSneak_GoesOut_AndReportsTheSneakSpent()
    {
        Field f = new() { SneakKept = true };

        f.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Equal(1, f.SneakSpent);
        Assert.Contains(f.LogLines, l => l.StartsWith("forced: `use waterskin` for buff 300 before 1/2 (Scorching Desert)"));
    }

    [Fact]
    public void LastCall_WithNoSneakKept_ReportsNothing()
    {
        Field f = new();
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(new[] { "use waterskin" }, f.Wire);
        Assert.Equal(0, f.SneakSpent);
    }

    // ----- sent by the between-round cast pass -------------------------------

    private sealed class Pass : IDisposable
    {
        public Field Field { get; } = new();
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public SpellsSettings Spells { get; } = new();
        public HealthSettings Health { get; } = new();
        public List<string> SpellsCast { get; } = new();
        public bool AutoHeal;
        public bool AutoBless;
        public bool SneakKept;
        public int CastFired;

        public Pass()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, new LogService());
            Cast.SetWireSender(_ => { });
            Cast.CastSent += SpellsCast.Add;
            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells, readHealth: () => Health, isEnabled: () => AutoHeal, log: new LogService());
            Director.SetAutoBlessGate(() => AutoBless);
            Director.SetStealthMaintenanceDeferGate(() => SneakKept);
            Director.CastFired += () => CastFired++;
            Field.Engine.UseSent += Director.NoteClientUseSent;
            State.MaxHp = 200;
            State.Hp = 200;
            State.HasPromptData = true;
            // Wired last: each prompt value above runs a pass of its own.
            Director.SetClientUseSource(Field.Engine.DueNow, Field.Engine.FireDue, Field.Engine.NoteHeldForSneak);
        }

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    // The hazard buff answers to neither Auto-Heal nor Auto-Bless: with both off the
    // pass still sends it, as the round's one cast, and the cast's followers run.
    [Fact]
    public void Pass_SendsTheDueUse_WithAutoHealAndAutoBlessOff()
    {
        using Pass p = new();

        Assert.Equal("use waterskin", p.Director.Evaluate());

        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(1, p.CastFired);
        Assert.Null(p.Director.Evaluate());         // the round's cast is spent
        Assert.Single(p.Field.Wire);
    }

    // Nothing has to be moving the character: standing in the desert, the pass keeps
    // the buff up.
    [Fact]
    public void Pass_StandingByHand_SendsTheDueUse()
    {
        using Pass p = new();
        p.Field.WalkActive = false;

        Assert.Equal("use waterskin", p.Director.Evaluate());
        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
    }

    // With a sneak being kept the `use` waits like any buff: the pass holds it, the
    // walk is told a cast is waiting for a room with no NPCs, and there it goes out.
    [Fact]
    public void Pass_SneakKept_HoldsTheUse_UntilARoomWithNoNpcs()
    {
        using Pass p = new() { SneakKept = true };
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1750);                      // inside the window, not at the last call

        Assert.Null(p.Director.Evaluate());
        Assert.Empty(p.Field.Wire);
        Assert.True(p.Director.HasSneakHeldCast);
        Assert.Contains(p.Field.LogLines, l => l.StartsWith("waiting for a room with no NPCs"));

        p.SneakKept = false;                        // a room with no NPCs
        Assert.Equal("use waterskin", p.Director.Evaluate());
        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(0, p.Field.SneakSpent);
        Assert.False(p.Director.HasSneakHeldCast);
    }

    // No room with no NPCs turned up before the last call: it goes out where the
    // character stands, sneak or not, and the sneak is reported spent.
    [Fact]
    public void Pass_SneakKept_AtTheLastCall_SendsItAnyway()
    {
        using Pass p = new() { SneakKept = true };
        p.Field.SneakKept = true;
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1790);

        Assert.Equal("use waterskin", p.Director.Evaluate());

        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.Equal(1, p.Field.SneakSpent);
        Assert.Contains(p.Field.LogLines, l => l.StartsWith("forced: `use waterskin` for buff 300 1/2 (Scorching Desert)"));
    }

    // The sneak guard holds for any move in flight with Auto-Sneak on, sneaking or
    // not. A forced `use` by a character whose sneak had already failed spends no
    // sneak, and must not arm what reacts to a sneak lost here: the app asks the
    // guard and the stealth state together.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AForcedUse_ReportsASneakSpent_OnlyWhenTheCharacterWasSneaking(bool stealthed, int spent)
    {
        SneakGuard guard = new(
            autoSneak: () => true, backstabOwed: () => false, moveInFlight: () => true,
            npcHere: () => false, fightingHere: () => false, inCombat: () => false, stealthed: () => stealthed);
        Assert.True(guard.Holds);

        using Pass p = new() { SneakKept = guard.Holds };
        p.Field.SneakKept = guard.Holds && stealthed;    // as AppServices wires it
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1790);

        Assert.Equal("use waterskin", p.Director.Evaluate());
        Assert.Equal(spent, p.Field.SneakSpent);

        Field f = new() { SneakKept = guard.Holds && stealthed };
        f.Engine.OnApproachingRoom(Dunes);
        Assert.Equal(spent, f.SneakSpent);
    }

    // A held `use` that stops being due without the pass sending it (the route turned
    // away from the desert, the master switch went off, a drink by hand) is no longer
    // held, with Auto-Heal and Auto-Bless both off as much as with one on. Left
    // standing, it stopped the walk in every room with no NPCs until the next
    // refresh window.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pass_AHeldUseNoLongerDue_IsNoLongerHeld(bool autoBless)
    {
        using Pass p = new() { SneakKept = true, AutoBless = autoBless };
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1750);
        Assert.Null(p.Director.Evaluate());
        Assert.True(p.Director.HasSneakHeldCast);

        p.Field.RoomsAhead = new() { Oasis };       // no countered room here or ahead any more
        p.SneakKept = false;
        p.Field.Advance(5);
        Assert.Null(p.Director.Evaluate());
        Assert.False(p.Director.HasSneakHeldCast);

        // So a sneaking walk is not held for a cast in a room with no NPCs.
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        using StealthManager stealth = new(router, new PlayerState(), new LogService());
        stealth.SetAutoToggles(() => true, () => false);
        stealth.SetMovementCoordinator(new MovementCoordinator());
        stealth.SetHeldCastCheck(() => p.Director.HasSneakHeldCast);
        router.Dispatch(new LineExtractor.EmittedLine(
            "Sneaking...", Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        Assert.True(stealth.ReadyToMoveSneaking());
        Assert.False(stealth.IsHoldingForCast);
    }

    // Nor once the step's own last call has sent it: the round's cast is spent, so no
    // pass would run to say so.
    [Fact]
    public void Pass_AHeldUseSentAtTheStep_IsNoLongerHeld()
    {
        using Pass p = new() { SneakKept = true, AutoBless = true };
        p.Field.Engine.OnServerLine(SwigLine);
        p.Field.Advance(1750);
        p.Director.Evaluate();
        Assert.True(p.Director.HasSneakHeldCast);

        p.Field.Advance(40);                        // the last call
        p.Field.Engine.OnApproachingRoom(Dunes);

        Assert.Equal(new[] { "use waterskin" }, p.Field.Wire);
        Assert.False(p.Director.HasSneakHeldCast);
    }

    // An emergency heal that is due takes the round ahead of it.
    [Fact]
    public void Pass_AnEmergencyHealGoesFirst()
    {
        using Pass p = new() { AutoHeal = true };
        p.Spells.EmergencyHealSpell = "lastresort";
        p.Health.EmergencyHealTrigger = 20;
        p.State.Hp = 20;                            // 10% of 200

        p.Director.Evaluate();

        Assert.Equal(new[] { "lastresort" }, p.SpellsCast);
        Assert.Empty(p.Field.Wire);
    }

    // A `use` sent by another road (the step into the desert) is the round's cast
    // all the same: nothing else is cast in that round.
    [Fact]
    public void UseSentAtTheStep_TakesTheRoundsCast()
    {
        using Pass p = new() { AutoHeal = true };
        p.Spells.MinorHealSpell = "heal";
        p.Health.MinorHealCombatTrigger = 90;
        p.Field.RoomsAhead = new() { Oasis };       // nothing on offer to the pass itself

        p.Field.Engine.OnApproachingRoom(Dunes);
        p.State.Hp = 100;                           // a minor heal is due

        Assert.Equal(1, p.CastFired);
        Assert.Empty(p.SpellsCast);
    }

    // ----- the step into the desert, with Auto-Sneak on ------------------------

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Oasis",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Scorching Desert",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // The walker at the oasis, one step from the desert, wired as the app wires it:
    // the hazard counter's last call in the ready check ahead of the sneak check, its
    // step hold on the walker's own coordinator, and a `use` followed by the re-sneak.
    private sealed class Crossing : IDisposable
    {
        public Field Field { get; } = new();
        public List<string> Wire { get; } = new();
        public AutoWalkManager Walker { get; }
        private readonly MessageRouter _router = new();
        private readonly StealthManager _stealth;

        public Crossing(string root, bool autoSneak)
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), RoomsJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            RoomTracker tracker = new(graph);
            tracker.SetLocated(Oasis);
            MovementCoordinator coordinator = new();
            Walker = new AutoWalkManager(graph, new BfsMapper(graph), tracker, coordinator);

            DefaultPatterns.Seed(_router);
            _stealth = new StealthManager(_router, new PlayerState(), new LogService());
            _stealth.SetAutoToggles(() => autoSneak, () => false);
            _stealth.SetEngineDrivingCheck(() => true);
            _stealth.SetMovementCoordinator(coordinator);

            Field.AutoSneak = autoSneak;
            Field.RoomsAhead = new() { Oasis, Dunes };
            Field.Engine.SetWireSender(Send);
            Field.Engine.SetStepHold(coordinator, (after, run) => Field.Timers.Add((after, run)));
            Field.Engine.UseSent += _stealth.ReSneakAfterCast;   // what CastFired does in the app
            _stealth.SetWireSender(Send);
            Walker.SetWireSender(Send);
            Walker.SetMoveReadyCheck(() =>
            {
                Field.Engine.OnApproachingRoom(Dunes);
                return !Field.Engine.HoldingStep && _stealth.ReadyToMoveSneaking();
            });
            Walker.SetApproachRoomHook(Field.Engine.OnApproachingRoom);
            Walker.SetPreMoveHook(_stealth.RequestPreMoveStealth);
            if (autoSneak) Feed("Sneaking...");
        }

        private void Send(byte[] bytes)
        {
            string command = Encoding.Latin1.GetString(bytes).TrimEnd('\r');
            Wire.Add(command);
            _stealth.NoteCommandSent(command, () => false, items: null);
        }

        // A line from the game, to the provisioner and to everything on the router.
        public void Feed(string line)
        {
            Field.Engine.OnServerLine(line);
            _router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose() => _stealth.Dispose();
    }

    private const string RoundRefusal = "You have already cast a spell this round!";

    // The last call is asked ahead of the sneak check, so the `sn` behind the `use`
    // is the held kind. The step waits for both answers and goes out sneaked, with
    // the buff on.
    [Fact]
    public void Walker_AutoSneakOn_UseThenSn_BothAnswered_ThenTheStep()
    {
        using Crossing c = new(_root, autoSneak: true);

        Assert.True(c.Walker.WalkTo(Dunes));
        Assert.Equal(new[] { "use waterskin", "sn" }, c.Wire);

        c.Feed("Attempting to sneak...");
        Assert.Equal(new[] { "use waterskin", "sn" }, c.Wire);   // the drink not yet answered
        c.Feed(SwigLine);

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, c.Wire);
    }

    // The round's cast was already made, so the `use` is refused. It is not asked
    // again at each answered `sn` (a `use` and a fresh sneak roll per refusal, as it
    // was): the step waits out the round, the cast pass sends the `use` with the next
    // round's cast, and the step follows its answers.
    [Fact]
    public void Walker_AutoSneakOn_RoundRefusal_WaitsOutTheRound_ThenOneUseAndTheStep()
    {
        using Crossing c = new(_root, autoSneak: true);
        Assert.True(c.Walker.WalkTo(Dunes));

        for (int i = 0; i < 5; i++)
        {
            c.Feed(RoundRefusal);
            c.Feed("Attempting to sneak...");
        }
        Assert.Equal(new[] { "use waterskin", "sn" }, c.Wire);

        Assert.True(c.Field.Engine.FireDue());   // the pass, with the next round's cast
        c.Feed(SwigLine);
        c.Feed("Attempting to sneak...");

        Assert.Equal(new[] { "use waterskin", "sn", "use waterskin", "sn", "n" }, c.Wire);
    }

    // With Auto-Sneak off nothing else holds the step, and it went in on the heels of
    // a `use` that might be refused. It waits for the drink's answer now.
    [Fact]
    public void Walker_AutoSneakOff_TheStepWaitsForTheDrink()
    {
        using Crossing c = new(_root, autoSneak: false);

        Assert.True(c.Walker.WalkTo(Dunes));
        Assert.Equal(new[] { "use waterskin" }, c.Wire);

        c.Feed(SwigLine);
        Assert.Equal(new[] { "use waterskin", "n" }, c.Wire);
    }

    [Fact]
    public void Walker_AutoSneakOff_RoundRefusal_TheStepIsHeld_UntilTheNextRoundsUseIsAnswered()
    {
        using Crossing c = new(_root, autoSneak: false);
        Assert.True(c.Walker.WalkTo(Dunes));

        c.Feed(RoundRefusal);
        Assert.Equal(new[] { "use waterskin" }, c.Wire);         // not walked in with the buff off

        Assert.True(c.Field.Engine.FireDue());
        Assert.Equal(new[] { "use waterskin", "use waterskin" }, c.Wire);
        c.Feed(SwigLine);

        Assert.Equal(new[] { "use waterskin", "use waterskin", "n" }, c.Wire);
    }
}
