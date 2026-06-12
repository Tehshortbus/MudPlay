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
            DoNotAnnouncePoison    = true,
            DoNotAnnounceBlindness = true,
            DoNotAnnounceConfusion = true,
            DoNotAnnounceDiseased  = true,
            UtilizeDisarmTrapsIfAble = false,
            MaxTrapSearchAttempts = 30,
            MaxTrapDisarmAttempts = 8,
            BlessWhileResting = false,
            BlessDuringCombat = false,
            RunDirection = RunDirection.Forward,
            BreakBeforeFleeing = false,
        };

        string json = JsonSerializer.Serialize(src);
        OtherSettings? back = JsonSerializer.Deserialize<OtherSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(7, back!.MaxSuicideLivesThreshold);
        Assert.True(back.IgnorePoison);
        Assert.True(back.IgnoreBlindness);
        Assert.True(back.IgnoreConfusion);
        Assert.True(back.IgnoreDiseased);
        Assert.True(back.DoNotAnnouncePoison);
        Assert.True(back.DoNotAnnounceBlindness);
        Assert.True(back.DoNotAnnounceConfusion);
        Assert.True(back.DoNotAnnounceDiseased);
        Assert.False(back.UtilizeDisarmTrapsIfAble);
        Assert.Equal(30, back.MaxTrapSearchAttempts);
        Assert.Equal(8,  back.MaxTrapDisarmAttempts);
        Assert.False(back.BlessWhileResting);
        Assert.False(back.BlessDuringCombat);
        Assert.Equal(RunDirection.Forward, back.RunDirection);
        Assert.False(back.BreakBeforeFleeing);
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
        // Say-announce defaults ON (do-not-announce UNCHECKED) — catching
        // a curable ailment broadcasts ".@poisoned" etc. by default.
        Assert.False(dto.DoNotAnnouncePoison);
        Assert.False(dto.DoNotAnnounceBlindness);
        Assert.False(dto.DoNotAnnounceConfusion);
        Assert.False(dto.DoNotAnnounceDiseased);
        // Trap-disarm master gate defaults ON — preserves the
        // walker's disarm-before-walk behavior out of the box (the
        // "if able" capability check still gates whether it actually
        // attempts). @trap auto-disarm attempt caps — user-spec
        // defaults: 20 search retries, 5 disarm retries.
        Assert.True(dto.UtilizeDisarmTrapsIfAble);
        Assert.Equal(20, dto.MaxTrapSearchAttempts);
        Assert.Equal(5,  dto.MaxTrapDisarmAttempts);
        // Party-bless gating defaults ON — the bless engine may cast on
        // party members both while resting and during combat unless the
        // user opts out.
        Assert.True(dto.BlessWhileResting);
        Assert.True(dto.BlessDuringCombat);
        // Flee defaults: retrace the way we came (Backward) and break
        // combat before the first flee move — both the safer choice.
        Assert.Equal(RunDirection.Backward, dto.RunDirection);
        Assert.True(dto.BreakBeforeFleeing);
    }
}
