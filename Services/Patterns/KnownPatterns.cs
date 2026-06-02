namespace FujinTerm.Services.Patterns;

/// <summary>
/// Stable identifiers for the default <see cref="MessageRouter"/> pattern
/// seed. Later-phase subsystems subscribe by name —
/// <c>router.Subscribe(KnownPatterns.UserHits, handler)</c> — instead of
/// re-typing regex strings everywhere. Users can also reference these IDs
/// from the Phase 5 Trigger UI when they want to react to a built-in
/// pattern.
/// </summary>
/// <remarks>
/// Naming convention: <c>category.action</c> in lower-kebab. Matches the
/// rest of the codebase (<see cref="MenuCommandIds"/>) and the spec
/// vocabulary lifted from Megamind's <c>classifier.js</c>.
/// </remarks>
public static class KnownPatterns
{
    // ----- Stealth -------------------------------------------------------
    public const string UserSneaking      = "stealth.user-sneaking";
    public const string UserNotSneaking   = "stealth.user-not-sneaking";
    public const string UserSneakFailed   = "stealth.user-sneak-failed";
    public const string UserSneakInitiate = "stealth.user-sneak-initiate";
    public const string UserCantSneak     = "stealth.user-cant-sneak";

    // ----- Movement ------------------------------------------------------
    public const string DirectionFailed   = "movement.direction-failed";
    public const string BashFailed        = "movement.bash-failed";
    public const string HeardMovement     = "movement.heard-movement";

    // ----- Failure -------------------------------------------------------
    public const string CommandNoEffect   = "failure.command-no-effect";
    public const string CommandIgnored    = "failure.command-ignored";
    public const string SlowDown          = "failure.slow-down";

    // ----- Searching -----------------------------------------------------
    public const string UserSearchFailed     = "search.user-search-failed";
    public const string UserSearchSucceeded  = "search.user-search-succeeded";

    // ----- Combat --------------------------------------------------------
    public const string CombatStatus         = "combat.status";   // captures (?<status>Engaged|Off)
    public const string UserHits             = "combat.user-hits";
    public const string MobMisses            = "combat.mob-misses";
    public const string MobHits              = "combat.mob-hits";
    public const string UserGainExperience   = "combat.user-gain-experience";

    // ----- Conversation --------------------------------------------------
    public const string ConversationGossip      = "conversation.gossip";
    public const string ConversationBroadcast   = "conversation.broadcast";
    public const string ConversationGangpath    = "conversation.gangpath";
    public const string ConversationTelepathIn  = "conversation.telepath-in";    // incoming "X telepaths: msg"
    public const string ConversationTelepathOut = "conversation.telepath-out";   // outgoing "--- Telepath sent to X ---"
    public const string ConversationYell        = "conversation.yell";           // both "X yells" and "You yell"
    public const string ConversationLocal       = "conversation.local";
    // UserEmote intentionally omitted — Megamind's emote regex keys off
    // ANSI bytes the LineExtractor consumes. Re-add when attribute-aware
    // matching ships.

    // ----- Item actions --------------------------------------------------
    public const string UserHides         = "item.user-hides";
    public const string PlayerGets        = "item.player-gets";     // combined: own + others
    public const string PlayerDrops       = "item.player-drops";    // combined: own + others
    public const string UserEquipped      = "item.user-equipped";   // wearing + lit (torches etc.)
    public const string UserEquipFailed   = "item.user-equip-failed";
    public const string UserRemoved       = "item.user-removed";
    public const string HiddenItems       = "item.hidden-items";
    public const string ShopListHeader    = "item.shop-list-header";
    public const string UserBuys          = "item.user-buys";

    // ----- Room & status -------------------------------------------------
    public const string RoomExits        = "room.exits";
    public const string StatusLine       = "status.line";
    public const string UserExperience   = "status.user-experience";
    public const string UserProfile      = "status.user-profile";
    public const string UserEncumbrance  = "status.user-encumbrance";

    // ----- Player presence ----------------------------------------------
    public const string PlayerDisconnects = "presence.player-disconnects";
    public const string PlayerExits       = "presence.player-exits";
    public const string PlayerEnters      = "presence.player-enters";
    public const string RoomAlsoHere      = "presence.room-also-here";    // "Also here: A, B, and C." — per-room occupant list
    public const string PartyInviteReceived = "presence.party-invite-received"; // "X has invited you to follow him/her." — incoming party invite from another player (Playpen-verified wording; MajorMUD player chars are male/female only)

    // ----- Party --------------------------------------------------------
    // Single-line membership signals. The `par` table itself is multi-line
    // and parsed via a small state machine in `PartyManager` (same shape
    // as `WhoListParser`), not by a one-line regex.
    public const string PartyFollowsYou     = "party.follows-you";       // "X started to follow you."
    public const string PartyYouFollowing   = "party.you-following";     // "You are now following X."  (we joined someone's party)
    public const string PartyStopsFollowing = "party.stops-following";   // "X has stopped following you." / "X stops following you."
    public const string PartyHeader         = "party.par-header";        // "The following people are in your travel party:" — anchors the par-block state machine
    public const string PartyMemberDeath    = "party.member-death";      // "X has been slain by Y" — conservative kill-attribution match
    // ----- Dissolution signals (Playpen-verified, 2026-06-01) ----------
    public const string PartyFollowerRemoved      = "party.follower-removed";       // "X has been removed from your followers." — leader's view of an uninvite
    public const string PartyYouNoLongerFollowing = "party.you-no-longer-following";// "You are no longer following X." — follower's view of leader's uninvite / our own unfollow
    public const string PartyDissolved            = "party.dissolved";              // "You are not in a party at the present time." — authoritative wipe
    // Per-member rank-change observation — fires whenever someone in the
    // party reranks via `frontr` / `midr` / `backr`. Lets PartyManager
    // update PartyMember.Rank live instead of waiting for the next par poll.
    public const string PartyMemberRankChanged    = "party.member-rank-changed";    // "X just moved to the {front|back} rank in your group." / "X just moved to the middle of your group."
    public const string PartySelfRankChanged      = "party.self-rank-changed";      // "You have moved to the {front|middle|back} ranks of your group." — self's own rerank confirmation

    // ----- Main menu -----------------------------------------------------
    // BBSes customise the banner version + realm name + prompt text, but
    // the menu options themselves are stable across customisations.
    // The "Enter the Realm" row is the universal main-menu signature.
    public const string MainMenuEnterRealm = "menu.enter-realm";   // "[E] . Enter the Realm" — universal main-menu line
}
