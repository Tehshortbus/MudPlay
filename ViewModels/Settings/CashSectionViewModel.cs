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
            new StubField("Copper",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Silver",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Gold",     StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Platinum", StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Runic",    StubFieldKind.Combo, "Collect / Ignore / Discard."),
        }),
        new StubGroup("Encumbrance gates", new[]
        {
            new StubField("Don't collect if it makes you Light",  StubFieldKind.Check, "Skip pickups that would push the character into the Light category."),
            new StubField("Don't collect if it makes you Medium", StubFieldKind.Check, "Skip pickups that would push past Light → Medium."),
            new StubField("Don't collect if it makes you Heavy",  StubFieldKind.Check, "Skip pickups that would push past Medium → Heavy."),
            new StubField("Collect after combat finished", StubFieldKind.Check, "Defer pickups until the round ends (avoids losing pre-attack rolls)."),
            new StubField("Drop smaller currency to make room for larger Collect-flagged coin", StubFieldKind.Check,
                          "Cascade — drops just enough lower-value Collect-flagged held coin; never sacrifices Ignore-flagged."),
        }),
        new StubGroup("Auto-deposit / sell", new[]
        {
            new StubField("Auto-deposit if wealth exceeds", StubFieldKind.Text, "Triggers when total wealth crosses this."),
            new StubField("Auto-deposit if coins exceed",   StubFieldKind.Text, "Triggers when total coin count crosses this."),
            new StubField("Minimum cash to keep on hand",   StubFieldKind.Text, "Don't deposit below this floor."),
            new StubField("Banking done at",                StubFieldKind.Combo, "Picked from the Phase 5 Shops table where ShopType == 7."),
        }),
        // Runic currency naming moved to the BBS + Display tab — it's a
        // per-BBS / per-realm label, not a per-character preference.
    };
}
