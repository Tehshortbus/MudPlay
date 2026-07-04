namespace FujinTerm.Models.GameData;

// Per-monster combat-line bundle — the parser-pattern set the future
// combat manager uses to recognise every line a single monster can produce
// in play: damage delivered (to you / to others), the kill confirmation,
// defensive outcomes (armor block + dodge, both perspectives), and
// missed-swing variants.
//
// One record per MonsterRecord Number. Records are NOT deduplicated by
// name — multiple monsters can share a name in the game data (giant rat #1
// vs giant rat #109 are different rows in the Monsters table, possibly
// different level / area / drops). Each monster Number gets its own
// editable record so a custom game can diverge per-monster without
// rippling.
//
// Storage parallels MessageRecord: per-set runtime file at
// Data/game data/{set}/monster-messages.json, universal seed at
// Data/Global/MonsterMessages.seed.json (user-writable; bootstrapped from
// the bundled Defaults/ copy on first launch, generated offline from the
// wcc monster-messages.json export). The seed is never written to; user
// edits write the per-set file (creating it on first save).
//
// Each of the nine line fields is a List<string> of alternative wordings —
// many monsters have multiple physical attack variants ("slash" vs
// "all-out slash" for guardsman, "fangs / tail / talons / bite" for adult
// red dragon) that all produce the same outcome with different text. The
// combat matcher OR's them when looking for the outcome event. Most
// monsters carry a single string per field; variant monsters carry 2–5.
//
// Identity rule: Id is SHA1(Name | every line of every field, joined)
// truncated to 16 lowercase hex chars. Any edit to Name or any pattern
// flips the Id; the store's upsert logic uses the original-Id reference to
// replace in place.
//
// The nine line fields hold patterns for: HitYou (monster hits YOU),
// HitOther (monster hits SOMEONE ELSE, party-tracking), DeathLine (empty
// for unkillable monsters like shopkeepers / quest NPCs), ArmorBlockYou
// (swing hits YOU but YOUR armor deflects), ArmorBlockOther (someone
// else's armor deflects), DodgeYou (YOU evade), DodgeOther (someone else
// evades), MissYou (swing at YOU misses, no contact), MissOther (swing at
// someone else misses).
//
// FlavorPrefixes: adjective prefixes the game may insert before the
// monster name in any line pattern above. Example for giant rat:
// ["nasty", "angry", "large", "fat", "thin", "big", "small"] — so "The
// giant rat bites you" can appear as "The nasty giant rat bites you", etc.
// The matcher expands the {monster-name} slot in patterns to (?:{prefix} )?
// alternation across this list (plus the bare name when AllowNoPrefix is
// true). AllowNoPrefix is true when the monster can appear with just the
// bare Name; corresponds to the wcc flavor format's N: marker.
//
// Links: back-references to the game-data rows this record is anchored to.
// Normally exactly one entry, (Monsters, N) matching the record's monster
// Number. Multi-link is reserved for future use (e.g. user manually
// collapsing two variant rows into one shared record).
public sealed record MonsterMessageRecord(
    string                       Id,
    string                       Name,
    IReadOnlyList<string>        HitYou,
    IReadOnlyList<string>        HitOther,
    IReadOnlyList<string>        DeathLine,
    IReadOnlyList<string>        ArmorBlockYou,
    IReadOnlyList<string>        ArmorBlockOther,
    IReadOnlyList<string>        DodgeYou,
    IReadOnlyList<string>        DodgeOther,
    IReadOnlyList<string>        MissYou,
    IReadOnlyList<string>        MissOther,
    IReadOnlyList<string>        FlavorPrefixes,
    bool                         AllowNoPrefix,
    IReadOnlyList<GameDataLink>? Links = null);
