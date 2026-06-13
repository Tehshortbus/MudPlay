namespace FujinTerm.Models.GameData;

/// <summary>
/// Per-character / per-BBS / global override layered on top of an
/// MDB item row. Persisted under the chosen tier via
/// <see cref="Services.SettingsResolver.WriteGameDataAt{T}"/> with
/// table = <c>"Items"</c> and record-id = the WCC No string. The
/// Game Data Browser → Items tab merges overrides on top of the
/// MDB <c>Items.json</c> base; the editor surface mirrors
/// MegaMUD's Game Item Details dialog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not overridable</b> — every BBS supplies a
/// concrete MDB (stock or custom-realm), so the MDB is the
/// canonical source of truth for the item's static stats. No
/// override layer for ItemType, Worn slot, ArmourType, ArmourClass,
/// DamageResist, Encumbrance, Price, Currency, StrReq, Accuracy,
/// Speed, or ability-bonus rows — read those from the MDB row.
/// </para>
/// <para>
/// <b>What IS overridable</b> — automation behaviour (the 11 Options
/// checkboxes from the MegaMUD details dialog) plus carry policy
/// (Min. to keep / Max to get / If needed, do) and display name.
/// All fields nullable so a partial-tier override only carries the
/// keys the user actually set — the resolver overlays them onto the
/// next-lower tier's values, preserving lower-tier values for
/// fields the user didn't touch.
/// </para>
/// <para>
/// The Options bitfield is decoded from the realm's
/// <c>Items.md</c> binary (analogous to the
/// <see cref="MonsterOverlay"/> seed pipeline) and shipped under
/// <c>Defaults/ItemOverlay.{realm}.seed.json</c> as the Defaults
/// baseline; user edits land at higher tiers via the resolver.
/// </para>
/// <para>
/// Uses init-only properties (rather than the positional-record
/// syntax) so the resolver's <c>new T()</c> requirement is satisfied.
/// </para>
/// </remarks>
public sealed record ItemOverlay
{
    /// <summary>User-facing display name override; <c>null</c> keeps the MDB value.</summary>
    public string? Name { get; init; }

    // ----- Options flags (11 in total, matching MegaMUD's dialog) -----

    /// <summary>Auto-collect this item from rooms during walk/loop.</summary>
    public bool? AutoCollect { get; init; }

    /// <summary>Auto-discard this item when picked up (drops to the room floor immediately).</summary>
    public bool? AutoDiscard { get; init; }

    /// <summary>Treat this item as a search target for auto-find behaviour.</summary>
    public bool? AutoFind { get; init; }

    /// <summary>Auto-open this item (containers, scrolls, etc.) when acquired.</summary>
    public bool? AutoOpen { get; init; }

    /// <summary>Auto-buy this item from shops when wealth permits.</summary>
    public bool? AutoBuy { get; init; }

    /// <summary>Auto-sell this item at the configured shop.</summary>
    public bool? AutoSell { get; init; }

    /// <summary>Auto-stash this item at the configured stash room.</summary>
    public bool? AutoStash { get; init; }

    /// <summary>Cannot be taken — the combat / loot engines treat this as quest-bound.</summary>
    public bool? CannotBeTaken { get; init; }

    /// <summary>Must keep at least <see cref="MinToKeep"/> of this item; engines won't drop below.</summary>
    public bool? MustHaveMinimum { get; init; }

    /// <summary>Loyal item — never drop / discard / lose to PvP.</summary>
    public bool? LoyalItem { get; init; }

    // ----- Carry policy -----

    /// <summary>
    /// Minimum count to retain in inventory. Stored as the raw string so
    /// the MegaMUD-parity "None" sentinel round-trips cleanly alongside
    /// numeric values. <c>null</c> = inherit the lower tier's value.
    /// </summary>
    public string? MinToKeep { get; init; }

    /// <summary>
    /// Maximum count to acquire. "All" is a legit MegaMUD sentinel so we
    /// store the raw string here as well.
    /// </summary>
    public string? MaxToGet { get; init; }
}
