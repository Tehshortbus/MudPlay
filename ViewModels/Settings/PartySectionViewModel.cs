using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Party" tab stub — party-cast spell decisions, `par` polling, the
/// auto-Exp-Reset broadcast, and the wait-for-party-members reconnect
/// grace window.
/// </summary>
public sealed class PartySectionViewModel : StubSectionViewModel
{
    public override string Id => "party";
    public override string Title => "Party";
    public override string PhaseTag => "Phase 6 (PartyManager) + Phase 13 PR 13.D (CastingDirector — party)";
    public override string Description =>
        "Party-cast configuration plus the per-character party knobs the PartyManager consumes (rank, par cadence, " +
        "request-heal-at). Party-heal / cure / buff decisions flow into the same Phase 13 CastingDirector that " +
        "drives self-cast.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Party basics", new[]
        {
            new StubField("Party rank",          StubFieldKind.Numeric, "Where this character sits in the group order."),
            new StubField("`par` poll frequency", StubFieldKind.Numeric, "Master plan default: 5 s.", "s"),
            new StubField("Wait-for-party-members grace", StubFieldKind.Numeric, "How long to hold the group together while a member reconnects.", "s"),
            new StubField("Auto-invite a reconnecting member", StubFieldKind.Check, "Leader-only — auto-invite if they return inside the grace window."),
            new StubField("Auto-Exp-Reset on loop / Auto-Lair start", StubFieldKind.Check, "Sends `@Reset` so the party EXP counter zeroes at the run boundary."),
        }),
        new StubGroup("Party heal", new[]
        {
            new StubField("Party-heal HP threshold", StubFieldKind.Numeric, "Cast group heal when any member drops below this.", "%"),
            new StubField("Min members below to fire", StubFieldKind.Numeric, "Group heal vs. single-target threshold."),
            new StubField("Party-heal spell",        StubFieldKind.Combo,   "Picked from Spells table (Phase 5)."),
            new StubField("Request-heal-at HP",      StubFieldKind.Numeric, "Send the @health prompt to the healer at this HP %.", "%"),
        }),
        new StubGroup("Party cure priority", new[]
        {
            new StubField("Paralyze",  StubFieldKind.Combo, "Cure-paralyze spell to apply to teammates."),
            new StubField("Poison",    StubFieldKind.Combo, "Cure-poison spell."),
            new StubField("Disease",   StubFieldKind.Combo, "Cure-disease spell."),
            new StubField("Blindness", StubFieldKind.Combo, "Cure-blindness spell."),
            new StubField("Confusion", StubFieldKind.Combo, "Cure-confusion spell."),
        }),
        new StubGroup("Party buff slots", new[]
        {
            new StubField("Buff 1", StubFieldKind.Combo, "Party-buff spell #1 (e.g. bless, prayer)."),
            new StubField("Buff 2", StubFieldKind.Combo, "Party-buff spell #2."),
            new StubField("Buff 3", StubFieldKind.Combo, "Party-buff spell #3."),
            new StubField("Buff 4", StubFieldKind.Combo, "Party-buff spell #4."),
            new StubField("Buff 5", StubFieldKind.Combo, "Party-buff spell #5."),
        }),
    };
}
