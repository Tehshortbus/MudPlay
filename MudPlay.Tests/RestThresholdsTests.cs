using MudPlay.Game.Health;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Rest triggers/targets read against the DEFAULT gear set's max, capped at the
// current gear's real (stat-screen) max so a stale-high live max or a rest-set swap
// can't strand the rest below reach (report paradigm-20260902-052036).
public sealed class RestThresholdsTests
{
    [Fact]
    public void PercentageAnchorsToDefaultSetMax()
    {
        // rest if < 80%, to 95%, of the DEFAULT set's 200 max (worn gear differs).
        (int trigger, int max) = RestThresholds.Resolve(
            ThresholdMode.Percentage, 80, 95,
            defaultMax: 200, realMax: 200, liveMax: 260);
        Assert.Equal(160, trigger);
        Assert.Equal(190, max);
    }

    [Fact]
    public void CapsTargetAtRealMax_WhenDefaultExceedsIt()
    {
        // The stuck-rest case: live max ratcheted high (230), default set max 230, but
        // the current (rest) gear's real max is only 205 — the 95% target must cap at
        // 205 so HP=205 counts as fully rested instead of chasing 218 forever.
        (int trigger, int max) = RestThresholds.Resolve(
            ThresholdMode.Percentage, 80, 95,
            defaultMax: 230, realMax: 205, liveMax: 230);
        Assert.Equal(184, trigger);   // 80% of 230 = 184, under the 205 cap
        Assert.Equal(205, max);       // 95% of 230 = 218, capped at 205
    }

    [Fact]
    public void CapsTargetAtLiveMax_WhenStatScreenMaxWentStaleHigh()
    {
        // report paradigm-20260903-110346: a medi/pre-rest swap lowered the pool, so the
        // stat-screen max (realMax) is stale-HIGH (255) while the live gear-swap-aware
        // max (liveMax=180, from EquipmentMaxPoolSync) is the true reachable ceiling. The
        // target must cap at 180 so HP=180 counts as fully rested — else the gate never
        // clears and the walker parks "Resting (Low HP)" at full.
        (int trigger, int max) = RestThresholds.Resolve(
            ThresholdMode.Percentage, 80, 95,
            defaultMax: 255, realMax: 255, liveMax: 180);
        Assert.Equal(180, max);       // capped at the live ceiling, not the stale-high 255
        Assert.Equal(180, trigger);   // 80% of 255 = 204, capped at 180
    }

    [Fact]
    public void FallsBackToRealMax_ThenLiveMax_WhenDefaultUnknown()
    {
        // No default-set value → anchor to the authoritative real max.
        (int _, int viaReal) = RestThresholds.Resolve(
            ThresholdMode.Percentage, 80, 95, defaultMax: 0, realMax: 205, liveMax: 230);
        Assert.Equal(195, viaReal);   // 95% of 205

        // Neither known (no stat screen yet) → the live ratcheted max, uncapped.
        (int _, int viaLive) = RestThresholds.Resolve(
            ThresholdMode.Percentage, 80, 95, defaultMax: 0, realMax: 0, liveMax: 230);
        Assert.Equal(218, viaLive);   // 95% of 230 = 218.5 → 218 (round-to-even), no real cap
    }

    [Fact]
    public void ResolveValue_AnchorsSingleThresholdToDefault_CappedAtReal()
    {
        // A flee/heal/hang trigger: 30% of the DEFAULT set's 230, capped at real 205.
        Assert.Equal(69, RestThresholds.ResolveValue(
            ThresholdMode.Percentage, 30, defaultMax: 230, realMax: 205, liveMax: 230));
        // A high trigger caps at the real max rather than exceeding reach.
        Assert.Equal(205, RestThresholds.ResolveValue(
            ThresholdMode.Percentage, 95, defaultMax: 230, realMax: 205, liveMax: 230));
    }

    [Fact]
    public void AbsoluteMode_PassesValueThrough_ButStillCapsAtRealMax()
    {
        (int trigger, int max) = RestThresholds.Resolve(
            ThresholdMode.Absolute, 100, 250, defaultMax: 300, realMax: 205, liveMax: 300);
        Assert.Equal(100, trigger);   // absolute, under the cap
        Assert.Equal(205, max);       // absolute 250 capped at the 205 real max
    }
}
