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
            UtilizeDisarmTrapsIfAble = false,
            MaxTrapSearchAttempts = 30,
            MaxTrapDisarmAttempts = 8,
        };

        string json = JsonSerializer.Serialize(src);
        OtherSettings? back = JsonSerializer.Deserialize<OtherSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(7, back!.MaxSuicideLivesThreshold);
        Assert.False(back.UtilizeDisarmTrapsIfAble);
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
        // Trap-disarm master gate defaults ON — preserves the
        // walker's disarm-before-walk behavior out of the box (the
        // "if able" capability check still gates whether it actually
        // attempts). @trap auto-disarm attempt caps — user-spec
        // defaults: 20 search retries, 5 disarm retries.
        Assert.True(dto.UtilizeDisarmTrapsIfAble);
        Assert.Equal(20, dto.MaxTrapSearchAttempts);
        Assert.Equal(5,  dto.MaxTrapDisarmAttempts);
    }
}
