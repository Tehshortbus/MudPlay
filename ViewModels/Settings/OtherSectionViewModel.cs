using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Other" tab stub — the misc bucket per the master plan's filtered
/// list of MegaMUD Other-1 / Other-2 toggles. Phase tags vary per item
/// because the consumers live across phases 7, 8, and 13.
/// </summary>
public sealed class OtherSectionViewModel : StubSectionViewModel
{
    public override string Id => "other";
    public override string Title => "Other";
    public override string PhaseTag => "Phase 7 / 8 / 13 (per-toggle — see tooltips)";
    public override string Description =>
        "Catch-all for auto-action toggles, walker behaviour flags, log retention, and display knobs that don't " +
        "fit the other tabs. Each toggle's tooltip names the owning phase / engine.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Locks, traps, walker behaviour", new[]
        {
            new StubField("Pick locks instead of bashing",        StubFieldKind.Check, "Phase 13 — walker prefers lockpicking when the skill is trained."),
            new StubField("Attempt to disarm traps",              StubFieldKind.Check, "Phase 7 PR 7.22 — walker pauses at trapped exits and tries disarm."),
            new StubField("Attempt to lock-pick traps",           StubFieldKind.Check, "Phase 7 PR 7.22 — falls back to lockpick if disarm fails."),
            new StubField("Search rooms while running",           StubFieldKind.Check, "Phase 7 — walker emits `search` between hops."),
            new StubField("Don't move unless sneaking",           StubFieldKind.Check, "Phase 7 — walker pause-gate when stealth drops."),
        }),
        new StubGroup("Auto-engage on connect", new[]
        {
            new StubField("Auto-Combat on",  StubFieldKind.Check, "Phase 13 PR 13.A — flips CombatManager on at logon."),
            new StubField("Auto-Rest on",    StubFieldKind.Check, "Phase 13 PR 13.B — flips HealthManager rest on at logon."),
            new StubField("Auto-Heal on",    StubFieldKind.Check, "Phase 13 PR 13.D — flips CastingDirector self-heal on at logon."),
            new StubField("Bless while resting", StubFieldKind.Check, "Phase 13 PR 13.D — CastingDirector recasts party-buffs during downtime."),
        }),
        new StubGroup("Display + retention", new[]
        {
            new StubField("Backscroll buffer size",          StubFieldKind.Numeric, "Phase 1 — lines retained in the in-memory ring.", "lines"),
            new StubField("Inactive player cleanup window",  StubFieldKind.Numeric, "Phase 5 PR 5.19 — drop Players-tab records last seen this many days ago.", "days"),
            new StubField("Debug log retention",             StubFieldKind.Numeric, "Phase 0 — prune Data/Logs/ entries older than this on app launch.", "days"),
            new StubField("Combat round totals display",     StubFieldKind.Check,   "Phase 8 — append the per-round damage roll-up to the canvas."),
        }),
    };
}
