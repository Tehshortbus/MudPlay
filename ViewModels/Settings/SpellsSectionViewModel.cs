using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Spells" tab stub — self-cast decisions. Heal / cure / buff which
/// spell, fired at which threshold. Party-cast equivalents live on the
/// Party tab; the Phase 13 CastingDirector consumes both.
/// </summary>
public sealed class SpellsSectionViewModel : StubSectionViewModel
{
    public override string Id => "spells";
    public override string Title => "Spells";
    public override string PhaseTag => "Phase 13 PR 13.D (CastingDirector — self-cast)";
    public override string Description =>
        "Self-cast configuration. Pick which spell to use for self-heal, self-cure, and each self-buff slot, plus " +
        "the HP / ailment thresholds that trigger them. Party-cast equivalents live on the Party tab.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Self-heal", new[]
        {
            new StubField("Cast heal when HP below", StubFieldKind.Numeric, "Self-heal trigger threshold.", "%"),
            new StubField("Heal spell",              StubFieldKind.Combo,   "Picked from the active game-data set's Spells table (Phase 5)."),
        }),
        new StubGroup("Self-cure priority", new[]
        {
            new StubField("Paralyze",   StubFieldKind.Combo, "Cure-paralyze spell (highest priority by default)."),
            new StubField("Poison",     StubFieldKind.Combo, "Cure-poison spell."),
            new StubField("Disease",    StubFieldKind.Combo, "Cure-disease spell."),
            new StubField("Blindness",  StubFieldKind.Combo, "Cure-blindness spell."),
            new StubField("Confusion",  StubFieldKind.Combo, "Cure-confusion spell."),
            new StubField("Cures fire in the order shown — drag to reorder once Phase 13 ships.",
                          StubFieldKind.Note, "Note about ordering."),
        }),
        new StubGroup("Self-buff slots", new[]
        {
            new StubField("Buff 1", StubFieldKind.Combo, "Self-buff spell #1 — recast when not active."),
            new StubField("Buff 2", StubFieldKind.Combo, "Self-buff spell #2."),
            new StubField("Buff 3", StubFieldKind.Combo, "Self-buff spell #3."),
            new StubField("Buff 4", StubFieldKind.Combo, "Self-buff spell #4."),
            new StubField("Buff 5", StubFieldKind.Combo, "Self-buff spell #5."),
        }),
    };
}
