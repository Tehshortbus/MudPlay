using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Combat" tab stub — auto-attack target selection, weapon-swap matrix,
/// multi-attack room spells, and the per-character backstab toggle.
/// </summary>
public sealed class CombatSectionViewModel : StubSectionViewModel
{
    public override string Id => "combat";
    public override string Title => "Combat";
    public override string PhaseTag => "Phase 13 PR 13.A (CombatManager)";
    public override string Description =>
        "How the auto-combat engine picks targets, swaps weapons across rounds, and decides when to fire a room " +
        "spell over a single-target attack. Backstab is off by default and explicitly opt-in per character.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Target order", new[]
        {
            new StubField("Strategy", StubFieldKind.Combo, "Forward / Reverse / Attack-last-party / Attack-last-absolute."),
            new StubField("Polite attacks (skip rooms with non-party players in combat)",
                          StubFieldKind.Check,
                          "Detects non-party combat via the Phase 5 Players table cross-check."),
        }),
        new StubGroup("Weapon swap", new[]
        {
            new StubField("Primary weapon",     StubFieldKind.Combo, "Equipped in the main hand during normal rounds."),
            new StubField("Off-hand",           StubFieldKind.Combo, "Off-hand weapon / shield."),
            new StubField("Alternate weapon",   StubFieldKind.Combo, "Swapped in for the BS / pre-BS round."),
            new StubField("Alternate off-hand", StubFieldKind.Combo, "Off-hand counterpart for the alternate set."),
        }),
        new StubGroup("Multi-attack room spells", new[]
        {
            new StubField("Min enemies to cast", StubFieldKind.Numeric, "Only fire room spells when at least N hostiles are present."),
            new StubField("Max consecutive casts", StubFieldKind.Numeric, "Cap on how many room spells fire back-to-back."),
            new StubField("Required mana floor",  StubFieldKind.Numeric, "Skip the cast when current MA would drop below this.", "%"),
        }),
        new StubGroup("Backstab", new[]
        {
            new StubField("Enable backstab (DoBS)", StubFieldKind.Check, "Off by default even for stealth classes. Phase 13 PR 13.F enforces stealth-window gating."),
            new StubField("Backstab if HP above",   StubFieldKind.Numeric, "Skip the BS attempt when wounded.", "%"),
        }),
    };
}
