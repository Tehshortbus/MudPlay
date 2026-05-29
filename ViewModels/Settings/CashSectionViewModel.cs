using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Cash" tab stub — per-coin Collect / Ignore / Discard policy,
/// encumbrance gates, and the auto-deposit triggers. Bank room comes
/// from the active game-data Shops table (ShopType=7).
/// </summary>
public sealed class CashSectionViewModel : StubSectionViewModel
{
    public override string Id => "cash";
    public override string Title => "Cash";
    public override string PhaseTag => "Phase 13 PR 13.E (CashManager)";
    public override string Description =>
        "Per-currency pickup behaviour, encumbrance gating with the master-plan formula, and the auto-deposit / " +
        "bank-routing rules. Stash rooms / auto-withdrawal / toll-aware withdraw are explicitly out of scope for v1.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Per-currency policy", new[]
        {
            new StubField("Platinum", StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Gold",     StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Silver",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Copper",   StubFieldKind.Combo, "Collect / Ignore / Discard."),
            new StubField("Runic",    StubFieldKind.Combo, "Per-realm name is overridable per character (some realms relabel runic)."),
        }),
        new StubGroup("Encumbrance gates", new[]
        {
            new StubField("Heavy gate",  StubFieldKind.Numeric, "Skip pickups that would push past Heavy. Formula `(threshold * maxWeight - 1) / 100`.", "%"),
            new StubField("Medium gate", StubFieldKind.Numeric, "Skip pickups that would push past Medium.", "%"),
            new StubField("Cascade drop smaller coins to make room for larger Collect-flagged coin",
                          StubFieldKind.Check,
                          "Drops just enough lower-value Collect-flagged currency; never sacrifices Ignore-flagged."),
        }),
        new StubGroup("Auto-deposit triggers", new[]
        {
            new StubField("Deposit when wealth exceeds", StubFieldKind.Numeric, "Whichever of wealth / coin-count triggers fires first.", "gold-equiv"),
            new StubField("Deposit when coins exceed",   StubFieldKind.Numeric, "Total coin count threshold.", "coins"),
            new StubField("Bank room",                   StubFieldKind.Combo,   "Picked from the Phase 5 Shops table where ShopType == 7."),
            new StubField("Resume previous activity after deposit", StubFieldKind.Check,
                          "Loop / walk-to / Auto-Lair snapshots resume from the trigger room (FujinTerm spec — diverges from MudProxy's abort)."),
        }),
    };
}
