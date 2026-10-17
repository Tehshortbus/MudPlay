using System.Text.Json;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// AutoModeController — the master switch. Pins KillSwitchEngaged as the one
// "all autos off" signal, distinct from AllWiredOff (also true for a character
// played by hand who never ticked a toggle): unticking the toggles one by one is
// not the switch, and the switch engages with every toggle already off.
public sealed class AutoModeControllerTests
{
    private static ProfileService BlankProfile()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        return profile;
    }

    private static void WriteAutoMode(ProfileService profile, AutoActionDefaults mode,
                                      AutoActionDefaults? baseModes = null)
    {
        CharacterProfile current = profile.Current!;
        current.Settings ??= new();
        current.Settings["General"] =
            JsonSerializer.SerializeToElement(new GeneralSettings { AutoMode = mode, AutoModeBase = baseModes });
    }

    private static AutoActionDefaults ReadAutoMode(ProfileService profile) =>
        JsonSerializer.Deserialize<GeneralSettings>(profile.Current!.Settings!["General"].GetRawText())!.AutoMode;

    private static AutoActionDefaults AllOff() => new()
    {
        AutoCombat = false, AutoNuke = false, AutoHeal = false, AutoRest = false,
        AutoBless = false, AutoLight = false, AutoGetItems = false,
        AutoGetCash = false, AutoSneak = false, AutoHide = false, AutoSearch = false,
    };

    private static AutoActionDefaults Only(Action<AutoActionDefaults> set)
    {
        AutoActionDefaults mode = AllOff();
        set(mode);
        return mode;
    }

    [Fact]
    public void ManualPlay_AllTogglesOff_SwitchStillOn()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);

        Assert.True(controller.AllWiredOff);
        Assert.False(controller.KillSwitchEngaged);
        Assert.False(controller.Blocks("Test"));
    }

    [Fact]
    public void Press_WithTogglesOn_SwitchesOffAndClearsThem()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        controller.ToggleAll();

        Assert.True(controller.AllWiredOff);
        Assert.True(controller.KillSwitchEngaged);
    }

    [Fact]
    public void SecondPress_SwitchesOnAndRestoresTheToggles()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => { m.AutoCombat = true; m.AutoSneak = true; }));
        AutoModeController controller = new(profile);
        controller.ToggleAll();

        controller.ToggleAll();

        Assert.False(controller.KillSwitchEngaged);
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoSneak);
        Assert.False(mode.AutoHeal);
    }

    // The ruling's second half: toggles unticked by hand are not the switch, so
    // an off must still engage it.
    [Fact]
    public void TurnOff_WithEveryToggleAlreadyOff_StillEngages()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOff("test");

        Assert.True(controller.KillSwitchEngaged);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void TurnOff_Twice_IsOneSwitch()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOff("test");
        controller.TurnOff("test");
        controller.TurnOn("test");

        // The second off must not overwrite what the first remembered.
        Assert.True(ReadAutoMode(profile).AutoCombat);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void TurnOn_NothingRemembered_SwitchesOnTheBaseModes()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => { m.AutoCombat = true; m.AutoRest = true; }));
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoRest);
        Assert.False(mode.AutoNuke);
        Assert.False(controller.KillSwitchEngaged);
        // The switch never went off, so nothing is told it came back on.
        Assert.Empty(events);
    }

    // "if i unticked every auto mode, then toggled off the master switch, then the
    // master switch back on, only the master switch should come back on" (user,
    // 2026-10-10): an off with nothing ticked remembers nothing ticked, and that is
    // not the same as nothing being remembered.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurnOn_AfterAnOffWithEveryToggleOff_OnlyTheSwitchComesBackOn(bool byButton)
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => m.AutoHeal = true));
        AutoModeController controller = new(profile);

        if (byButton) { controller.ToggleAll(); controller.ToggleAll(); }
        else { controller.TurnOff("test"); controller.TurnOn("test"); }

        Assert.False(controller.KillSwitchEngaged);
        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));
    }

    // The same ruling against the base-modes settle. A Stop, a walk-to's arrival
    // or a loop restarted by a reconnect used to settle the toggles into the base
    // modes under the switch, and switch-on, which keeps what was ticked
    // meanwhile, then brought the base autos on.
    [Theory]
    [InlineData("stopped by the user")]
    [InlineData("loop start")]
    [InlineData("walk-to end")]
    public void ReconcileToBase_SwitchOff_TicksNothing_SoOnlyTheSwitchComesBackOn(string reason)
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => { m.AutoCombat = true; m.AutoHeal = true; }));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");

        Assert.Null(controller.ReconcileToBase(reason));
        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));

        controller.TurnOn("test");

        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));
        Assert.Equal(1, controller.SkippedSinceOff["Base-modes reset"]);
    }

    // A character with no base modes yet adopts its live toggles as the base the
    // first time. Not under the switch: live is all-off then, and the base would
    // be saved as "nothing".
    [Fact]
    public void ReconcileToBase_SwitchOff_DoesNotAdoptTheSwitchedOffTogglesAsTheBase()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");

        Assert.Null(controller.ReconcileToBase("stopped by the user"));

        GeneralSettings general =
            JsonSerializer.Deserialize<GeneralSettings>(profile.Current!.Settings!["General"].GetRawText())!;
        Assert.Null(general.AutoModeBase);
    }

    [Fact]
    public void ReconcileToBase_SwitchOn_SettlesTheTogglesIntoTheBase()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        AutoModeReconcileResult? result = controller.ReconcileToBase("loop start");

        Assert.True(result is { LiveChanged: true });
        Assert.True(ReadAutoMode(profile).AutoCombat);
        // Settled already: nothing to do the second time.
        Assert.Null(controller.ReconcileToBase("loop start"));
    }

    // The pyramid climb unticks toggles for its first floors. An off / on during
    // the climb gives back what was ticked then, never the base modes, and a
    // climb that ends while the switch is off hands its toggles to the switch.
    [Fact]
    public void TurnOn_AcrossAClimbsUnticking_GivesBackWhatTheClimbOwes_NotBaseModes()
    {
        ProfileService profile = BlankProfile();
        // Mid-climb: the climb has unticked Combat and Rest, Heal is still on.
        WriteAutoMode(profile, Only(m => m.AutoHeal = true),
            baseModes: Only(m => { m.AutoCombat = true; m.AutoRest = true; m.AutoNuke = true; }));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");

        // The climb ends while the switch is off.
        controller.RememberForSwitchOn(Only(m => { m.AutoCombat = true; m.AutoRest = true; }));
        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoHeal);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoRest);
        Assert.False(mode.AutoNuke);
    }

    // A feature giving toggles back under the switch (a Run's "Combat off" ending,
    // a detour's combat hold: one toggle; Sprint Mode ending: its four; the
    // pyramid climb: its eight). With the switch off nothing is ticked, so the
    // toolbar shows nothing on, and exactly the owed ones come back with it.
    public static IEnumerable<object[]> RestoresUnderTheSwitch() => new[]
    {
        new object[] { "Run + Combat off", new[] { "Combat" } },
        new object[] { "Detour combat hold", new[] { "Combat" } },
        new object[] { "Sprint Mode ended", new[] { "Combat", "GetItems", "Search", "GetCash" } },
        new object[] { "Sprint Mode ended", new[] { "GetItems" } },
        new object[] { "Pyramid run-through over",
            new[] { "Combat", "Nuke", "Rest", "Light", "GetItems", "GetCash", "Hide", "Search" } },
    };

    private static void Tick(AutoActionDefaults mode, string[] names)
    {
        foreach (string name in names)
            typeof(AutoActionDefaults).GetProperty("Auto" + name)!.SetValue(mode, true);
    }

    [Theory]
    [MemberData(nameof(RestoresUnderTheSwitch))]
    public void KeepsForSwitchOn_SwitchOff_TicksNothingNow_AndTheOwedOnesComeBackWithTheSwitch(string from, string[] owed)
    {
        ProfileService profile = BlankProfile();
        // The feature had these unticked when the switch went off; Heal was on.
        WriteAutoMode(profile, Only(m => m.AutoHeal = true), baseModes: Only(m => m.AutoBless = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");

        Assert.True(controller.KeepsForSwitchOn(from, m => Tick(m, owed)));
        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));

        controller.TurnOn("test");

        AutoActionDefaults expected = Only(m => { m.AutoHeal = true; Tick(m, owed); });
        Assert.True(ReadAutoMode(profile).SameAs(expected));
    }

    [Fact]
    public void KeepsForSwitchOn_SwitchOn_LeavesTheRestoreToTheCaller_AndRemembersNothing()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: AllOff());
        AutoModeController controller = new(profile);

        Assert.False(controller.KeepsForSwitchOn("Sprint Mode ended", m => m.AutoCombat = true));
        controller.TurnOff("test");
        controller.TurnOn("test");

        Assert.False(ReadAutoMode(profile).AutoCombat);
    }

    // Only the user's own press counts as someone being at the keyboard; a party
    // member's `@auto-all` and the local API do not. Read by the listeners as the
    // switch is switched, so it has to be set by then.
    [Theory]
    [InlineData(AutoModeController.ByUserPress, true)]
    [InlineData("@auto-all from Tank", false)]
    [InlineData("@auto-all from (local api)", false)]
    public void SwitchedByUserPress_IsKnownToTheListeners_OnBothEdges(string by, bool expected)
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        List<bool> seen = new();
        controller.KillSwitchToggled += _ => seen.Add(controller.SwitchedByUserPress);

        controller.TurnOff(by);
        controller.TurnOn(by);

        Assert.Equal(new[] { expected, expected }, seen);
    }

    // Off by the user's press, back on by a party member: the switch-on is remote.
    [Fact]
    public void SwitchedByUserPress_FollowsTheLastSwitch()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        controller.ToggleAll(AutoModeController.ByUserPress);
        Assert.True(controller.SwitchedByUserPress);
        controller.TurnOn("@auto-all from Tank");
        Assert.False(controller.SwitchedByUserPress);
    }

    [Fact]
    public void RememberForSwitchOn_WithTheSwitchOn_DoesNothing()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: AllOff());
        AutoModeController controller = new(profile);

        controller.RememberForSwitchOn(Only(m => m.AutoCombat = true));
        controller.TurnOff("test");
        controller.TurnOn("test");

        Assert.False(ReadAutoMode(profile).AutoCombat);
    }

    [Fact]
    public void TurnOn_SwitchAlreadyOnWithAToggleOn_ChangesNothing()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoLight = true), baseModes: Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoLight);
        Assert.False(mode.AutoCombat);
    }

    [Fact]
    public void TurnOn_KeepsAToggleTickedByHandWhileOff()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");
        WriteAutoMode(profile, Only(m => m.AutoSearch = true));

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoSearch);
    }

    [Fact]
    public void Blocks_OnlyWhileOff_AndCountsPerSystem()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        Assert.False(controller.Blocks("Triggers"));
        Assert.Empty(controller.SkippedSinceOff);

        controller.TurnOff("test");
        Assert.True(controller.Blocks("Triggers", "one"));
        Assert.True(controller.Blocks("Triggers", "two"));
        Assert.True(controller.Blocks("Polls"));

        Assert.Equal(2, controller.SkippedSinceOff["Triggers"]);
        Assert.Equal(1, controller.SkippedSinceOff["Polls"]);
        Assert.Equal("Polls 1, Triggers 2", controller.DescribeSkipped());

        // A new off starts a new count.
        controller.TurnOn("test");
        controller.TurnOff("test");
        Assert.Empty(controller.SkippedSinceOff);
    }

    [Fact]
    public void TurnOff_AsksWhatWasInFlightBeforeItChangesAnything()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        bool? engagedWhenAsked = null;
        controller.DescribeInFlight = () => { engagedWhenAsked = controller.KillSwitchEngaged; return "a loop"; };

        controller.TurnOff("test");

        Assert.False(engagedWhenAsked);
    }

    // "the reconnect should not turn autos back on in the situation that the user
    // was connected, then hung up and turned them off, then reconnect" (user,
    // 2026-10-09): with the switch off the re-enable boxes change nothing and the
    // switch stays off.
    [Fact]
    public void ReEnableOnReconnect_SwitchOff_ChangesNothing_AndLeavesTheSwitchOff()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        bool done = controller.ReEnableOnReconnect(new GeneralSettings
        {
            ReEnableAutoCombatOnReconnect = true,
            ReEnableAutoHealRestOnReconnect = true,
        });

        Assert.False(done);
        Assert.True(controller.KillSwitchEngaged);
        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));
        Assert.Empty(events);

        // What the user had is still what comes back when they switch it on.
        controller.TurnOn("test");
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.False(mode.AutoHeal);
    }

    [Fact]
    public void ReEnableOnReconnect_SwitchOn_TicksTheOptedInToggles()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);

        bool done = controller.ReEnableOnReconnect(new GeneralSettings
        {
            ReEnableAutoCombatOnReconnect = true,
            ReEnableAutoHealRestOnReconnect = true,
        });

        Assert.True(done);
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoHeal);
        Assert.True(mode.AutoRest);
        Assert.False(mode.AutoNuke);
    }

    [Fact]
    public void ResetSnapshot_ClearsTheSwitch_AndSaysSo()
    {
        // Profile-load boundary: a freshly loaded character starts with the
        // switch on, and whatever it held is released.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.ToggleAll();
        List<bool> switched = new();
        controller.KillSwitchToggled += switched.Add;
        int resets = 0;
        controller.ResetByProfileLoad += () => resets++;

        controller.ResetSnapshot();

        Assert.False(controller.KillSwitchEngaged);
        // Its own signal: the switch-on work (re-running engines, settling with the
        // party) must not run in the middle of a profile load.
        Assert.Equal(1, resets);
        Assert.Empty(switched);

        // A second reset with the switch already on says nothing.
        controller.ResetSnapshot();
        Assert.Equal(1, resets);
    }
}
