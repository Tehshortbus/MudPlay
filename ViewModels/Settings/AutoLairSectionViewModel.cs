using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Auto-Lair" tab stub — global tuning for the Phase 7 scheduler.
/// Marked-lair list + per-lair timer overrides are per-character and
/// live in the profile (LairTimerStore), not in this tab.
/// </summary>
public sealed class AutoLairSectionViewModel : StubSectionViewModel
{
    public override string Id => "auto-lair";
    public override string Title => "Auto-Lair";
    public override string PhaseTag => "Phase 7 PR 7.19 / 7.20 (AutoLairScheduler)";
    public override string Description =>
        "Global tuning for the Auto-Lair scheduler. The marked-lair list itself is per-character (managed from " +
        "the Navigation window's right-click context menu); this tab is the heuristic + weighting knobs the " +
        "scheduler reads each tick.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Routing heuristic", new[]
        {
            new StubField("Strategy", StubFieldKind.Combo, "Default (balanced) / Throughput / Custom."),
            new StubField("Idle penalty weight", StubFieldKind.Numeric, "Higher = prefer ready-now lairs over idling at a wait-room.", "×"),
            new StubField("Min mob-up window before counting as wasted respawn",
                          StubFieldKind.Numeric, "Grace period before slipping a tick counts against the score.", "s"),
        }),
        new StubGroup("Travel cost calibration", new[]
        {
            new StubField("Estimated seconds per hop", StubFieldKind.Numeric, "Initial guess; auto-calibrated once a session has produced per-step timings.", "s"),
            new StubField("Auto-learn respawn timers", StubFieldKind.Check,   "Refine per-lair timers from the observed kill → respawn cadence."),
        }),
    };
}
