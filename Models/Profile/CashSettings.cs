namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Cash" settings — drives
/// <see cref="Game.Cash.CashManager"/>'s per-currency pickup /
/// discard behaviour and auto-deposit trigger. Stored as the
/// <c>"Cash"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 ships per-currency <see cref="Policy"/> + a single
/// <see cref="AutoDepositIfWealthExceeds"/> threshold. Encumbrance
/// gates, cascade drop-smaller-for-larger, and the walker-driven
/// auto-deposit reroute land as follow-up PRs on this engine — the
/// foundation here lets the user smoke-test the per-currency pickup
/// path end-to-end.
/// </para>
/// </remarks>
public sealed class CashSettings
{
    /// <summary>Per-currency pickup behavior.</summary>
    public CashPolicy CopperPolicy { get; set; } = CashPolicy.Ignore;
    /// <inheritdoc cref="CopperPolicy"/>
    public CashPolicy SilverPolicy { get; set; } = CashPolicy.Collect;
    /// <inheritdoc cref="CopperPolicy"/>
    public CashPolicy GoldPolicy { get; set; } = CashPolicy.Collect;
    /// <inheritdoc cref="CopperPolicy"/>
    public CashPolicy PlatinumPolicy { get; set; } = CashPolicy.Collect;
    /// <inheritdoc cref="CopperPolicy"/>
    public CashPolicy RunicPolicy { get; set; } = CashPolicy.Collect;

    /// <summary>
    /// Auto-deposit trigger — fire when total held wealth (in the
    /// realm's canonical unit, typically gold-equivalent) exceeds
    /// this value. <c>0</c> disables the trigger. v1 fires the
    /// <see cref="Game.Cash.CashManager.AutoDepositRequested"/>
    /// event; subscribers wire the walker reroute themselves until
    /// the full snapshot-pause-walk-deposit-resume flow ships.
    /// </summary>
    public long AutoDepositIfWealthExceeds { get; set; }

    /// <summary>
    /// Bank room key — used by the (follow-up) auto-deposit walker
    /// reroute to know where to walk. Sourced from the Phase 5 Shops
    /// table where <c>ShopType == 7</c> (bank). v1 just stores it;
    /// the reroute itself is unwired.
    /// </summary>
    public string BankRoomKey { get; set; } = string.Empty;

    // ----- Per-currency minimum to keep on hand ---------------------
    // The floor the character keeps after offloading coin, applied to
    // BOTH banking (auto-deposit) and stashing. StashRoomManager reads
    // these at entry into a marked stash room: held - keep is the
    // amount dumped via `hide N <coin>`; the auto-deposit reroute uses
    // the same floor for `deposit`. Defaults all 0 = offload everything.

    /// <summary>Copper to keep on hand when depositing / stashing.
    /// Default 0 — offload all.</summary>
    public long KeepCopperOnHand { get; set; }

    /// <summary>Silver to keep on hand when depositing / stashing.
    /// Default 0 — offload all.</summary>
    public long KeepSilverOnHand { get; set; }

    /// <summary>Gold to keep on hand when depositing / stashing.
    /// Default 0 — offload all.</summary>
    public long KeepGoldOnHand { get; set; }

    /// <summary>Platinum to keep on hand when depositing / stashing.
    /// Default 0 — offload all.</summary>
    public long KeepPlatinumOnHand { get; set; }

    /// <summary>Runic to keep on hand when depositing / stashing.
    /// Default 0 — offload all.</summary>
    public long KeepRunicOnHand { get; set; }

    // ----- Encumbrance + cascade (persisted; engines deferred) -------
    // These knobs are visible in the Settings → Cash tab for MudProxy
    // parity but their engines haven't shipped yet (the original
    // CashManager audit deferred them). When the engines land they
    // pick these up from the DTO with no schema change.

    /// <summary>Skip a pickup that would push the character into the
    /// Light encumbrance bracket. Engine deferred.</summary>
    public bool SkipCollectIfMakesLight { get; set; }

    /// <summary>Skip a pickup that would push past Light → Medium.
    /// Engine deferred.</summary>
    public bool SkipCollectIfMakesMedium { get; set; }

    /// <summary>Skip a pickup that would push past Medium → Heavy.
    /// Engine deferred.</summary>
    public bool SkipCollectIfMakesHeavy { get; set; }

    /// <summary>Defer pickups until the current combat round ends so
    /// the pre-attack roll isn't lost. Engine deferred.</summary>
    public bool CollectAfterCombatFinished { get; set; }

    /// <summary>When a Collect-flagged currency would push past an
    /// encumbrance gate, drop just enough lower-value Collect-flagged
    /// held coin to make room. Never sacrifices Ignore-flagged coin.
    /// Engine deferred.</summary>
    public bool DropSmallerForLarger { get; set; }
}

/// <summary>Per-currency pickup decision.</summary>
public enum CashPolicy
{
    /// <summary>Don't touch — leave on the ground.</summary>
    Ignore,

    /// <summary>Pick up via <c>get all &lt;coin&gt;</c>.</summary>
    Collect,

    /// <summary>If we already hold any of this currency, drop it.
    /// Doesn't pick up new piles.</summary>
    Discard,
}
