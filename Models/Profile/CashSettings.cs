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

    /// <summary>
    /// Minimum cash to keep on hand after a deposit. Honoured by the
    /// (follow-up) auto-deposit flow. v1 just stores the value.
    /// </summary>
    public long MinimumCashOnHand { get; set; }
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
