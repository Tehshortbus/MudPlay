namespace FujinTerm.Models.GameData;

/// <summary>
/// Per-monster combat-line bundle — the parser-pattern set the future
/// combat manager uses to recognise every line a single monster can
/// produce in play: damage delivered (to you / to others), the kill
/// confirmation, defensive outcomes (armor block + dodge, both
/// perspectives), and missed-swing variants.
/// </summary>
/// <remarks>
/// <para>
/// One record per <see cref="MonsterRecord"/> Number. Records are NOT
/// deduplicated by name — multiple monsters can share a name in the
/// game data (giant rat #1 vs giant rat #109 are different rows in
/// the Monsters table, possibly different level / area / drops). Each
/// monster Number gets its own editable record so a custom game can
/// diverge per-monster without rippling.
/// </para>
/// <para>
/// Storage parallels <see cref="MessageRecord"/>: per-set runtime file
/// at <c>Data/game data/{set}/monster-messages.json</c>, universal seed
/// at <c>Defaults/MonsterMessages.seed.json</c> (generated offline from
/// the wcc <c>monster-messages.json</c> export). The seed is never
/// written to; user edits write the per-set file (creating it on first
/// save).
/// </para>
/// <para>
/// Each of the nine line fields is a <c>List&lt;string&gt;</c> of
/// alternative wordings — many monsters have multiple physical attack
/// variants ("slash" vs "all-out slash" for guardsman, "fangs / tail /
/// talons / bite" for adult red dragon) that all produce the same
/// outcome with different text. The combat matcher OR's them when
/// looking for the outcome event. Most monsters carry a single string
/// per field; variant monsters carry 2–5.
/// </para>
/// <para>
/// Identity rule: <see cref="Id"/> is <c>SHA1(Name | every line of every
/// field, joined)</c> truncated to 16 lowercase hex chars. Any edit
/// to Name or any pattern flips the Id; the store's upsert logic uses
/// the original-Id reference to replace in place.
/// </para>
/// </remarks>
/// <param name="Id">Stable content hash of (Name + every line in every field).</param>
/// <param name="Name">Monster's display name (as it appears in the game data Monsters table).</param>
/// <param name="HitYou">Patterns for the line YOU see when the monster successfully hits YOU.</param>
/// <param name="HitOther">Patterns for the line you see when the monster hits SOMEONE ELSE (party-tracking).</param>
/// <param name="DeathLine">Patterns for the monster's death line. Empty for unkillable monsters (shopkeepers, quest NPCs, etc.).</param>
/// <param name="ArmorBlockYou">Patterns for the line YOU see when the monster's swing hits YOU but YOUR armor deflects.</param>
/// <param name="ArmorBlockOther">Patterns for the line you see when SOMEONE ELSE's armor deflects the swing.</param>
/// <param name="DodgeYou">Patterns for the line YOU see when YOU evade the monster's swing.</param>
/// <param name="DodgeOther">Patterns for the line you see when SOMEONE ELSE evades.</param>
/// <param name="MissYou">Patterns for the line YOU see when the monster swings at YOU and misses (no contact).</param>
/// <param name="MissOther">Patterns for the line you see when the monster swings at SOMEONE ELSE and misses.</param>
/// <param name="FlavorPrefixes">
/// Adjective prefixes the game may insert before the monster name in
/// any of the line patterns above. Example for giant rat:
/// <c>["nasty", "angry", "large", "fat", "thin", "big", "small"]</c>
/// — so "The giant rat bites you" can appear as "The nasty giant rat
/// bites you", "The angry giant rat bites you", etc. The matcher
/// expands the {monster-name} slot in patterns to <c>(?:{prefix} )?</c>
/// alternation across this list (plus the bare name when
/// <see cref="AllowNoPrefix"/> is true).
/// </param>
/// <param name="AllowNoPrefix">
/// True when the monster can appear without any adjective prefix
/// (just the bare Name). Corresponds to the wcc flavor format's <c>N:</c>
/// marker.
/// </param>
/// <param name="Links">
/// Back-references to the game-data rows this record is anchored to.
/// Normally exactly one entry: <c>(Monsters, N)</c> matching the record's
/// monster Number. Multi-link is reserved for future use (e.g. user
/// manually collapsing two variant rows into one shared record).
/// </param>
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
