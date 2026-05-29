using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Cash" tab stub — per-currency collect policy, three-tier
/// encumbrance gates (Light / Medium / Heavy), auto-deposit triggers,
/// and the bank-room lookup. Currency labels read from the active
/// realm at runtime (copper farthings / silver nobles / platinum
/// crowns / etc.) — the labels listed below are the generic categories.
/// </summary>
public sealed class CashSectionViewModel : StubSectionViewModel
{
    public override string Id => "cash";
    public override string Title => "Cash";
    public override string PhaseTag => "Phase 13 PR 13.E (CashManager)";
    public override string Description =>
        "Per-currency pickup behaviour with three-tier encumbrance gating (Light / Medium / Heavy), auto-deposit " +
        "rules, and bank routing. Bank rooms come from the Phase 5 Shops table where ShopType == 7. Stash rooms / " +
        "auto-withdrawal / toll-aware withdraw are explicitly out of scope for v1.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Per-currency policy", new[]
        {
            new StubField("Platinum (or realm equivalent)", StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Gold",     StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Silver",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Copper",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Runic",    StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Labels above render the realm-specific name (copper farthings / silver nobles / etc.) at runtime.",
                          StubFieldKind.Note, "Currency naming note."),
        }),
        new StubGroup("Encumbrance gates", new[]
        {
            new StubField("Don't collect if it makes you Light",  StubFieldKind.Check, "Skip pickups that would push the character into the Light category."),
            new StubField("Don't collect if it makes you Medium", StubFieldKind.Check, "Skip pickups that would push past Light → Medium."),
            new StubField("Don't collect if it makes you Heavy",  StubFieldKind.Check, "Skip pickups that would push past Medium → Heavy."),
            new StubField("Encumbrance % threshold", StubFieldKind.Numeric, "Formula `(threshold * maxWeight - 1) / 100` matches the game's rounding inverse.", "%"),
            new StubField("Collect after combat finished", StubFieldKind.Check, "Defer pickups until the round ends (avoids losing pre-attack rolls)."),
            new StubField("Drop smaller currency to make room for larger Collect-flagged coin", StubFieldKind.Check,
                          "Cascade — drops just enough lower-value Collect-flagged held coin; never sacrifices Ignore-flagged."),
        }),
        new StubGroup("Auto-deposit / sell", new[]
        {
            new StubField("Auto-deposit if wealth exceeds", StubFieldKind.Numeric, "Triggers when total wealth crosses this.", "gold-equiv"),
            new StubField("Auto-deposit if coins exceed",   StubFieldKind.Numeric, "Triggers when total coin count crosses this.", "coins"),
            new StubField("Minimum cash to keep on hand",   StubFieldKind.Numeric, "Don't deposit below this floor.", "gold-equiv"),
            new StubField("Banking done at",                StubFieldKind.Combo,   "Picked from the Phase 5 Shops table where ShopType == 7."),
            new StubField("Resume previous activity after deposit", StubFieldKind.Check,
                          "Loop / walk-to / Auto-Lair snapshots resume from the trigger room."),
        }),
        new StubGroup("Miscellaneous", new[]
        {
            new StubField("Name of runic currency", StubFieldKind.Text, "Per-character override — some realms relabel runics."),
        }),
    };
}
