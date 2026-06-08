using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the persistable shape of <see cref="OtherSettings"/>.
/// The engine-side effect of the suicide-threshold knob is covered in
/// <see cref="RemoteCommandManagerTests"/>; this file pins the DTO
/// schema + defaults so a JSON-format regression fails loudly.
/// </summary>
public sealed class OtherSectionViewModelTests
{
    [Fact]
    public void OtherSettings_RoundTripsThroughJson()
    {
        OtherSettings src = new()
        {
            MaxSuicideLivesThreshold = 7,
            IgnorePoison    = true,
            IgnoreBlindness = true,
            IgnoreConfusion = true,
            IgnoreDiseased  = true,
            GameEntryCommand = "enter",
            GameExitCommand  = "bye",
            MaxTrapSearchAttempts = 30,
            MaxTrapDisarmAttempts = 8,
        };

        string json = JsonSerializer.Serialize(src);
        OtherSettings? back = JsonSerializer.Deserialize<OtherSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(7, back!.MaxSuicideLivesThreshold);
        Assert.True(back.IgnorePoison);
        Assert.True(back.IgnoreBlindness);
        Assert.True(back.IgnoreConfusion);
        Assert.True(back.IgnoreDiseased);
        Assert.Equal("enter", back.GameEntryCommand);
        Assert.Equal("bye",   back.GameExitCommand);
        Assert.Equal(30, back.MaxTrapSearchAttempts);
        Assert.Equal(8,  back.MaxTrapDisarmAttempts);
    }

    // Note: the per-character "Phase 9 diagnostic toggle" tests
    // (default-off + round-trip) were removed when the Verbose +
    // WriteCombatRoundTrace fields graduated out of OtherSettings and
    // into the Log pane menu's "Combat diagnostics" umbrella (session-
    // only, see Services/LogDiagnosticState). Coverage for that
    // umbrella lives next to its consumers (RoundDamageTracker tests
    // for the trace path, LogPaneViewModel tests for the binding).

[Fact]
    public void OtherSettings_Default_MatchesPhase6Spec()
    {
        // Per user direction: default 5. Range 0..9 (range enforced by
        // the spinner + Math.Clamp in OtherSectionViewModel.Apply +
        // AppServices.ApplyOtherFromActiveProfile). 0 disables the
        // block entirely.
        OtherSettings dto = new();
        Assert.Equal(5, dto.MaxSuicideLivesThreshold);
        // Ignore-ailment toggles default UNCHECKED — most parties want
        // to pause on every ailment. Toggle ON when the party agrees
        // to push through a specific tick (e.g. boss runs).
        Assert.False(dto.IgnorePoison);
        Assert.False(dto.IgnoreBlindness);
        Assert.False(dto.IgnoreConfusion);
        Assert.False(dto.IgnoreDiseased);
        // Game-menu commands default to MajorMUD's standard picks:
        // E to enter the realm, =x to log off from the main menu.
        Assert.Equal("E",  dto.GameEntryCommand);
        Assert.Equal("=x", dto.GameExitCommand);
        // @trap auto-disarm attempt caps — user-spec defaults:
        // 20 search retries, 5 disarm retries.
        Assert.Equal(20, dto.MaxTrapSearchAttempts);
        Assert.Equal(5,  dto.MaxTrapDisarmAttempts);
    }
}
