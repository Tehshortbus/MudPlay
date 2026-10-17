using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// OtherSettings' defaults: what a profile with no Other entry runs on. The
// engine-side effect of the suicide-threshold knob is covered in
// RemoteCommandManagerTests.
public sealed class OtherSettingsDefaultsTests
{
    [Fact]
    public void OtherSettings_Default_MatchesSpec()
    {
        // Per user direction: default 5. Range 0..9 (range enforced by
        // the spinner + Math.Clamp in OtherSectionViewModel.Apply +
        // AppServices.ApplyOtherFromActiveProfile). 0 disables the
        // block entirely.
        OtherSettings dto = new();
        Assert.Equal(5, dto.MaxSuicideLivesThreshold);
        // Trap-disarm master gate defaults ON — preserves the
        // walker's disarm-before-walk behavior out of the box (the
        // "if able" capability check still gates whether it actually
        // attempts). The @trap auto-disarm attempt cap defaults to 5.
        Assert.True(dto.UtilizeDisarmTrapsIfAble);
        Assert.Equal(5,  dto.MaxTrapDisarmAttempts);
    }
}
