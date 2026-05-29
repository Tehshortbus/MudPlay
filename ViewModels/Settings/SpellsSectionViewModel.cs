using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Spells" tab stub — which spell to cast for each role (heal /
/// regen / utility / cure / 10 bless slots). Per-character; consumed by
/// the Phase 13 CastingDirector and the HealthManager's heal-cast
/// thresholds (which live on the Health tab).
/// </summary>
public sealed class SpellsSectionViewModel : StubSectionViewModel
{
    public override string Id => "spells";
    public override string Title => "Spells";
    public override string PhaseTag => "Phase 13 PR 13.D (CastingDirector — self-cast utility / cure / buff)";
    public override string Description =>
        "Spell picks per role. Heal HP threshold lives on Health → Heal (rest) / Heal (combat); this tab is just " +
        "the picker. Bless slots take 10 entries to cover every class's stacked-buff playstyle.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Healing / regeneration", new[]
        {
            new StubField("Heal HP",        StubFieldKind.Combo,   "Primary heal — fires when Health → Heal threshold trips."),
            new StubField("HP Regen",       StubFieldKind.Combo,   "Auto-regen utility (e.g. troll-skin) when downtime is available."),
            new StubField("Mana Regen",     StubFieldKind.Combo,   "Auto-regen utility for the mana pool."),
            new StubField("When HP full",   StubFieldKind.Combo,   "Cast this when HP is full and we're between actions (e.g. room spell, debuff)."),
            new StubField("When Mana full", StubFieldKind.Combo,   "Cast this when MA is full and we're between actions."),
            new StubField("Min. rate per click", StubFieldKind.Numeric, "Cap on how often a cast is allowed to retrigger inside the same heal window."),
        }),
        new StubGroup("Other spells", new[]
        {
            new StubField("Freedom (cure paralyze)", StubFieldKind.Combo, "Cure-paralyze spell — canonically `freedom` in MajorMUD."),
            new StubField("Cure poison",             StubFieldKind.Combo, "Cure-poison spell."),
            new StubField("Cure disease",            StubFieldKind.Combo, "Cure-disease spell."),
            new StubField("Cure blindness",          StubFieldKind.Combo, "Cure-blindness spell."),
            new StubField("Room light",              StubFieldKind.Combo, "Cast when entering a dark room (pairs with Other → Provide light in dimly lit rooms)."),
        }),
        new StubGroup("Bless spells (10 slots)", new[]
        {
            new StubField("Bless 1",  StubFieldKind.Combo, "Self-buff slot #1 — recast when not active."),
            new StubField("Bless 2",  StubFieldKind.Combo, "Self-buff slot #2."),
            new StubField("Bless 3",  StubFieldKind.Combo, "Self-buff slot #3."),
            new StubField("Bless 4",  StubFieldKind.Combo, "Self-buff slot #4."),
            new StubField("Bless 5",  StubFieldKind.Combo, "Self-buff slot #5."),
            new StubField("Bless 6",  StubFieldKind.Combo, "Self-buff slot #6."),
            new StubField("Bless 7",  StubFieldKind.Combo, "Self-buff slot #7."),
            new StubField("Bless 8",  StubFieldKind.Combo, "Self-buff slot #8."),
            new StubField("Bless 9",  StubFieldKind.Combo, "Self-buff slot #9."),
            new StubField("Bless 10", StubFieldKind.Combo, "Self-buff slot #10."),
        }),
    };
}
