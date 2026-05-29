using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Other" tab stub — the misc bucket per the master plan's filtered
/// list of MegaMUD Other-1 / Other-2 toggles. Phase tags vary per item
/// because the consumers live across phases 7, 8, and 13. "Show combat
/// round totals" lives on the BBS + Display tab now, not here.
/// </summary>
public sealed class OtherSectionViewModel : StubSectionViewModel
{
    public override string Id => "other";
    public override string Title => "Other";
    public override string PhaseTag => "Phase 7 / 11 / 13 (per-toggle — see tooltips)";
    public override string Description =>
        "Catch-all for auto-action toggles, walker behaviour flags, log retention, and ignore-this-ailment knobs " +
        "that don't fit the other tabs. Each toggle's tooltip names the owning phase / engine.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Locks, traps, walker behaviour", new[]
        {
            new StubField("Pick locks instead of bashing",   StubFieldKind.Check, "Phase 13 — walker prefers lockpicking when the skill is trained."),
            new StubField("Attempt to disarm traps",         StubFieldKind.Check, "Phase 7 PR 7.22 — walker pauses at trapped exits and tries disarm."),
            new StubField("Auto-train",                      StubFieldKind.Check, "Phase 13 — auto-spend CP at a trainer when allocations are pending."),
            new StubField("Teleport to avoid combat instead of hanging", StubFieldKind.Check,
                          "Phase 7 — when fleeing, use sys-goto (stock) or a town token (paradigm) instead of dropping the line."),
            new StubField("Allow hangup when not AFK",       StubFieldKind.Check, "Phase 13 — gate hangup unless AFK Mode is on."),
            new StubField("Allow hangup in all-off mode",    StubFieldKind.Check, "Phase 13 — gate hangup when every Auto-* toggle is off."),
            new StubField("Hangup if naked",                 StubFieldKind.Check, "Phase 13 — recovery safety, disconnect if equipment got lost."),
            new StubField("Search rooms if item needed",     StubFieldKind.Check, "Phase 7 — walker auto-searches when item-collect requires it."),
            new StubField("Go backwards if running",         StubFieldKind.Check, "Phase 13 — flee direction prefers retracing rather than pushing forward."),
            new StubField("Backwards if warning",            StubFieldKind.Check, "Phase 13 — same direction logic but triggered by warning-state instead of HP."),
            new StubField("Break combat before running",     StubFieldKind.Check, "Phase 13 — stop swinging before issuing the flee command."),
            new StubField("Don't move unless sneaking",      StubFieldKind.Check, "Phase 7 — walker pause-gate when stealth drops."),
            new StubField("Provide light in dimly lit rooms", StubFieldKind.Check, "Phase 7 — pairs with Spells → Room light."),
        }),
        new StubGroup("Ignored ailments", new[]
        {
            new StubField("Ignore poison",    StubFieldKind.Check, "Phase 13 — don't auto-cure; let it wear off."),
            new StubField("Ignore blindness", StubFieldKind.Check, "Phase 13 — don't auto-cure; let it wear off."),
            new StubField("Ignore confusion", StubFieldKind.Check, "Phase 13 — don't auto-cure; let it wear off."),
        }),
        new StubGroup("Auto-engage on connect", new[]
        {
            new StubField("Auto-Combat on",       StubFieldKind.Check, "Phase 13 PR 13.A — flips CombatManager on at logon."),
            new StubField("Auto-Rest on",         StubFieldKind.Check, "Phase 13 PR 13.B — flips HealthManager rest on at logon."),
            new StubField("Auto-Heal on",         StubFieldKind.Check, "Phase 13 PR 13.D — flips CastingDirector self-heal on at logon."),
            new StubField("Bless while resting",  StubFieldKind.Check, "Phase 13 PR 13.D — CastingDirector recasts party-buffs during downtime."),
            new StubField("Bless during combat",  StubFieldKind.Check, "Phase 13 PR 13.D — extends bless casting into active rounds."),
        }),
        new StubGroup("Retry counts", new[]
        {
            new StubField("Attempt bash N times",      StubFieldKind.Numeric, "Phase 7 — retry cap on door / chest bash."),
            new StubField("Attempt pick-lock N times", StubFieldKind.Numeric, "Phase 7 — retry cap on lockpicking."),
            new StubField("Attempt disarm N times",    StubFieldKind.Numeric, "Phase 7 PR 7.22 — retry cap on trap disarm before falling back."),
        }),
        new StubGroup("Commands + retention", new[]
        {
            new StubField("Command splitter character",     StubFieldKind.Text,    "Splits multi-command input (default `;`)."),
            new StubField("Game entry command",             StubFieldKind.Text,    "One-shot sent on first prompt after logon (e.g. `set wimpy 30`)."),
            new StubField("Game exit command",              StubFieldKind.Text,    "Sent before disconnect (e.g. `bye`)."),
            new StubField("Backscroll buffer size",         StubFieldKind.Numeric, "Phase 1 — lines retained in the in-memory ring.", "lines"),
            new StubField("Inactive player cleanup window", StubFieldKind.Numeric, "Phase 5 PR 5.19 — drop Players-tab records last seen this many days ago.", "days"),
            new StubField("Debug log retention",            StubFieldKind.Numeric, "Phase 0 — prune Data/Logs/ entries older than this on app launch.", "days"),
        }),
    };
}
