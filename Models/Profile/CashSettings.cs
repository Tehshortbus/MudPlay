namespace FujinTerm.Models.Profile;

// Per-character "Cash" settings — drives Game.Cash.CashManager's per-currency
// pickup / discard behaviour and auto-deposit trigger. Stored as the "Cash"
// entry in CharacterProfile.Settings.
//
// v1 ships per-currency Policy + a single AutoDepositIfWealthExceeds threshold.
// Encumbrance gates, cascade drop-smaller-for-larger, and the walker-driven
// auto-deposit reroute land as follow-up work on this engine — the foundation
// here lets the user smoke-test the per-currency pickup path end-to-end.
public sealed class CashSettings
{
    // Per-currency pickup behavior.
    public CashPolicy CopperPolicy { get; set; } = CashPolicy.Ignore;
    public CashPolicy SilverPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy GoldPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy PlatinumPolicy { get; set; } = CashPolicy.Collect;
    public CashPolicy RunicPolicy { get; set; } = CashPolicy.Collect;

    // Auto-deposit trigger — fire when total held wealth (in the realm's
    // canonical unit, typically gold-equivalent) exceeds this value. 0 disables
    // the trigger. v1 fires the CashManager.AutoDepositRequested event;
    // subscribers wire the walker reroute themselves until the full
    // snapshot-pause-walk-deposit-resume flow ships.
    public long AutoDepositIfWealthExceeds { get; set; }

    // Auto-deposit trigger — fire when the total number of physical coins held
    // (summed across every denomination, regardless of each coin's value)
    // exceeds this count. 0 disables the trigger. Independent of
    // AutoDepositIfWealthExceeds: either gate firing triggers the deposit
    // (OR logic), and the single-fire guard re-arms only once BOTH gates fall
    // back below their thresholds.
    public long AutoDepositIfCoinsExceed { get; set; }

    // Bank room key — used by the (follow-up) auto-deposit walker reroute to
    // know where to walk. Sourced from the Shops table where ShopType == 7
    // (bank). v1 just stores it; the reroute itself is unwired.
    public string BankRoomKey { get; set; } = string.Empty;

    // ----- Per-currency minimum to keep on hand ---------------------
    // The floor the character keeps after offloading coin, applied to
    // BOTH banking (auto-deposit) and stashing. StashRoomManager reads
    // these at entry into a marked stash room: held - keep is the
    // amount dumped via `hide N <coin>`; the auto-deposit reroute uses
    // the same floor for `deposit`. Defaults all 0 = offload everything.

    // Copper to keep on hand when depositing / stashing. Default 0 — offload all.
    public long KeepCopperOnHand { get; set; }

    // Silver to keep on hand when depositing / stashing. Default 0 — offload all.
    public long KeepSilverOnHand { get; set; }

    // Gold to keep on hand when depositing / stashing. Default 0 — offload all.
    public long KeepGoldOnHand { get; set; }

    // Platinum to keep on hand when depositing / stashing. Default 0 — offload all.
    public long KeepPlatinumOnHand { get; set; }

    // Runic to keep on hand when depositing / stashing. Default 0 — offload all.
    public long KeepRunicOnHand { get; set; }

    // Total copper-farthing value of the per-denomination keep-on-hand floors —
    // the slice of wealth the character keeps after offloading coin. Uses the
    // MajorMUD ratio ladder (silver = 10, gold = 100, platinum = 10 000,
    // runic = 1 000 000 copper). Shared by the auto-deposit reroute (the bank
    // dep amount) and the @deposit-all remote command (the deposit / withdraw
    // target).
    public long KeepOnHandCopper() =>
        KeepCopperOnHand
        + KeepSilverOnHand * 10
        + KeepGoldOnHand * 100
        + KeepPlatinumOnHand * 10_000
        + KeepRunicOnHand * 1_000_000;

    // ----- Coin encumbrance gate + cascade ---------------------------
    // The "Cash + Items" tab exposes these; CashManager.CollectCoins gates coin
    // pickups against the bracket boundary they name.

    // Skip a coin pickup that would push the character into the Light
    // encumbrance bracket.
    public bool SkipCollectIfMakesLight { get; set; }

    // Skip a coin pickup that would push past Light → Medium.
    public bool SkipCollectIfMakesMedium { get; set; }

    // Skip a coin pickup that would push past Medium → Heavy.
    public bool SkipCollectIfMakesHeavy { get; set; }

    // Defer pickups until the room's combat finishes before sending gets.
    // Shared by CashManager and the AutoGetItemsManager item engine.
    public bool CollectAfterCombatFinished { get; set; }

    // When a Collect-flagged currency would push past an encumbrance gate, drop
    // just enough lower-value Collect-flagged held coin to make room. Never
    // sacrifices Ignore-flagged coin.
    public bool DropSmallerForLarger { get; set; }

    // ----- Item encumbrance gate -------------------------------------
    // Independent of the coin gate above: these cap the AutoGetItemsManager
    // ground-item pickups at the named bracket boundary. The hard capacity cap
    // (never grab an item that would exceed MaxWeight) is always on regardless
    // of these; the flags add the optional tighter bracket ceilings.

    // Skip a ground-item pickup that would push the character into Light.
    public bool SkipGetItemIfMakesLight { get; set; }

    // Skip a ground-item pickup that would push past Light → Medium.
    public bool SkipGetItemIfMakesMedium { get; set; }

    // Skip a ground-item pickup that would push past Medium → Heavy.
    public bool SkipGetItemIfMakesHeavy { get; set; }
}

// Per-currency pickup decision.
public enum CashPolicy
{
    // Don't touch — leave on the ground.
    Ignore,

    // Pick up via get all <coin>.
    Collect,

    // If we already hold any of this currency, drop it. Doesn't pick up new piles.
    Discard,
}
