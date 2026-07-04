namespace FujinTerm.Models.GameData;

// Per-character / per-BBS / global override layered on top of an MDB item
// row. Persisted under the chosen tier via
// SettingsResolver.WriteGameDataAt with table = "Items" and record-id =
// the WCC No string. The Game Data Browser → Items tab merges overrides
// on top of the MDB Items.json base; the editor surface mirrors MegaMUD's
// Game Item Details dialog.
//
// Deliberately not overridable — every BBS supplies a concrete MDB (stock
// or custom-realm), so the MDB is the canonical source of truth for the
// item's static stats. No override layer for ItemType, Worn slot,
// ArmourType, ArmourClass, DamageResist, Encumbrance, Price, Currency,
// StrReq, Accuracy, Speed, or ability-bonus rows — read those from the
// MDB row.
//
// What IS overridable — automation behaviour (the 11 Options checkboxes
// from the MegaMUD details dialog) plus carry policy (Min. to keep / Max
// to get / If needed, do) and display name. All fields nullable so a
// partial-tier override only carries the keys the user actually set — the
// resolver overlays them onto the next-lower tier's values, preserving
// lower-tier values for fields the user didn't touch.
//
// The Options bitfield is decoded from the realm's Items.md binary
// (analogous to the MonsterOverlay seed pipeline) and shipped under
// Defaults/ItemOverlay.{realm}.seed.json as the Defaults baseline; user
// edits land at higher tiers via the resolver.
//
// Uses init-only properties (rather than the positional-record syntax) so
// the resolver's new T() requirement is satisfied.
public sealed record ItemOverlay
{
    // Display name override; null keeps the MDB value.
    public string? Name { get; init; }

    // ----- Options flags (11 in total, matching MegaMUD's dialog) -----

    // Auto-collect this item from rooms during walk/loop.
    public bool? AutoCollect { get; init; }

    // Auto-discard this item when picked up (drops to the room floor immediately).
    public bool? AutoDiscard { get; init; }

    // Treat this item as a search target for auto-find behaviour.
    public bool? AutoFind { get; init; }

    // Auto-open this item (containers, scrolls, etc.) when acquired.
    public bool? AutoOpen { get; init; }

    // Auto-buy this item from shops when wealth permits.
    public bool? AutoBuy { get; init; }

    // Auto-sell this item at the configured shop.
    public bool? AutoSell { get; init; }

    // Auto-stash this item at the configured stash room.
    public bool? AutoStash { get; init; }

    // Cannot be taken — the combat / loot engines treat this as quest-bound.
    public bool? CannotBeTaken { get; init; }

    // Must keep at least MinToKeep of this item; engines won't drop below.
    public bool? MustHaveMinimum { get; init; }

    // Loyal item — never drop / discard / lose to PvP.
    public bool? LoyalItem { get; init; }

    // ----- Carry policy -----

    // Minimum count to retain in inventory. Stored as the raw string so
    // the MegaMUD-parity "None" sentinel round-trips cleanly alongside
    // numeric values. null = inherit the lower tier's value.
    public string? MinToKeep { get; init; }

    // Maximum count to acquire. "All" is a legit MegaMUD sentinel so we
    // store the raw string here as well.
    public string? MaxToGet { get; init; }
}
