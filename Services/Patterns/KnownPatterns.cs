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
    // Left-behind disambiguators (Phase 6 PR 6.2). When a party leader
    // moves and we can't follow, the game prints one of these the instant
    // before "You are no longer following X." — distinguishing a genuine
    // left-behind (auto-`@comeback`) from a deliberate uninvite/unfollow.
    public const string MovementFailedStuck = "movement.failed-stuck";   // "You can't seem to move anywhere!" — a prevents-movement gamedata flag blocked us
    public const string MovementFailedHeavy = "movement.failed-heavy";   // "...too heavy to move" — over-encumbered (system line; never inside a chat channel)

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

    /// <summary>
    /// Local-player death — "You have been slain by &lt;killer&gt;." per
    /// MajorMUD's canonical wording. <see cref="Game.Combat.DeathLineWatcher"/>
    /// subscribes here and emits the PlayerDied event that
    /// DeathRecoveryManager (PR 9.I) consumes for corpse-recovery.
    /// </summary>
    public const string UserSlain            = "combat.user-slain";

    /// <summary>
    /// "X moves to attack Y." — emitted by the server for every
    /// player's combat announce, including ours. Used by
    /// <see cref="Game.Combat.CombatManager"/> to implement the
    /// AttackTiming re-fire mechanism (AttackLastParty / AttackLastRoom
    /// / AttackAfter). The pattern tolerates the bracketed-prompt
    /// prefix ("[HP=100/MA=50]:X moves to attack Y.") plus a bare
    /// colon prefix; the announcer name + target are positional
    /// captures.
    /// </summary>
    public const string PartyAttackAnnounce  = "combat.party-attack-announce";

    /// <summary>
    /// "You don't see &lt;X&gt; here!" — server's response when our
    /// <c>attack X</c> resolves against a target that left or died
    /// between our send and the server's resolve (our death-line
    /// match was missed, the mob fled, a partymate killed it first,
    /// etc.). <see cref="Game.Combat.CombatManager"/> clears
    /// <c>_currentTarget</c> and forces a room re-display.
    /// </summary>
    public const string TargetNotHere        = "combat.target-not-here";

    /// <summary>"Your weapon has no effect against this monster!" —
    /// server's signal that the currently-equipped weapon can't
    /// damage the current target. CombatManager swaps to the
    /// AlternateWeapon (or marks the monster unhittable if already
    /// on alt) per CombatSettings.NoEffectFailureThreshold.</summary>
    public const string WeaponNoEffect       = "combat.weapon-no-effect";

    /// <summary>"Your fists have no effect against this monster!" —
    /// our weapon fell off (encumbrance, server quirk, missed
    /// equip-confirm). CombatManager treats this as "re-equip from
    /// scratch" and clears the shadow-equipped state.</summary>
    public const string FistsNoEffect        = "combat.fists-no-effect";

    // ----- Spellcasting -------------------------------------------------
    /// <summary>"You attempt to cast &lt;spell&gt;, but fail." — failed
    /// concentration / fizzle. Blocks further casts for the current
    /// round.</summary>
    public const string CastFizzled          = "spell.cast-fizzled";

    /// <summary>"You do not have enough mana to cast that spell." —
    /// blocks further casts until mana recovers.</summary>
    public const string CastNoMana           = "spell.cast-no-mana";

    /// <summary>"You have already cast a spell this round!" — only one
    /// cast per combat round; blocks until the next tick.</summary>
    public const string CastAlreadyThisRound = "spell.cast-already-this-round";

    /// <summary>"You lost your concentration on the spell!" — mid-cast
    /// interrupt (took damage during prep, broke stealth, etc.).</summary>
    public const string CastInterrupted      = "spell.cast-interrupted";

    /// <summary>"Your spell has no effect on &lt;monster&gt;." — the
    /// target is immune to the attack spell we just cast (priest
    /// <c>harm</c> vs an acid slime, etc.). Group 0 captures the
    /// monster name. CombatManager canonicalizes it to base species and
    /// marks that species attack-spell-immune so the chooser skips the
    /// primary attack spell to the alternate (then the weapon command)
    /// for the rest of the room.</summary>
    public const string SpellNoEffect        = "spell.no-effect";

    // ----- Cash --------------------------------------------------------
    /// <summary>"There are N &lt;coin&gt; pieces here." / singular
    /// variant. Fired on room display when cash is on the ground.
    /// CashManager subscribes to react per
    /// <see cref="Models.Profile.CashSettings"/> policy.</summary>
    public const string CashOnGround        = "cash.on-ground";

    /// <summary>"You picked up N &lt;coin&gt; pieces." / singular
    /// variant — confirmation that a get succeeded. CashManager
    /// updates internal tallies + the auto-deposit trigger check.</summary>
    public const string CashPickedUp        = "cash.picked-up";

    /// <summary>"You dropped N &lt;coin&gt; pieces." — discard
    /// confirmation.</summary>
    public const string CashDropped         = "cash.dropped";

    /// <summary>"You hid N &lt;coin&gt; pieces." — stash-room
    /// confirmation. Wire shape distinct from <see cref="CashDropped"/>
    /// because the <c>hide</c> command is the stash-room verb in stock
    /// MajorMUD. Without this, the held-coin tally goes stale after a
    /// stash and the auto-deposit threshold fires on phantom wealth.
    /// Lifted from MudProxy <c>CombatSessionTracker.cs:503-505</c>.</summary>
    public const string CashHidden          = "cash.hidden";

    /// <summary>
    /// "N &lt;coin&gt; drop to the ground." — corpse-spawned cash
    /// after a monster kill (combat-log shape, distinct from the
    /// room-display <see cref="CashOnGround"/>). Currency word is the
    /// short form ("silver", "gold", …) without the "pieces" suffix.
    /// CashManager dispatches through the same per-currency policy as
    /// CashOnGround so kill-loot follows the user's Collect / Discard
    /// / Ignore choices.
    /// </summary>
    public const string CashFromKill        = "cash.from-kill";

    /// <summary>"You notice &lt;list&gt; here." — the realm-specific
    /// room-survey line. Cash entries appear FIRST (server orders
    /// runic → platinum → gold → copper → silver), followed by items.
    /// Comma-separated, last entry ends with a period. Server wraps
    /// at 80 cols mid-token so multi-line stitching is required.
    /// CashManager parses each comma-separated entry and pulls
    /// recognisable currency tokens for tally + collect dispatch.</summary>
    public const string YouNoticeRoom       = "cash.you-notice-room";

    /// <summary>
    /// Room-entry arrival — "&lt;name&gt; &lt;verb&gt; into the room
    /// from &lt;direction&gt;." Fires when a monster spawns OR a
    /// player walks in mid-room (no full re-display). The wire
    /// colours the name segment yellow for monsters, red for players;
    /// <see cref="Game.Combat.RoomEntryWatcher"/> reads the colour
    /// off the line's attribute strip and uses it as a hint when the
    /// name doesn't match the active game-data tables. Direction is
    /// any cardinal / non-cardinal / up / down or the literal
    /// <c>"nowhere"</c> (script-spawn).
    /// </summary>
    public const string RoomEntryArrival     = "presence.room-entry-arrival";

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

    // ----- Room light ----------------------------------------------------
    // The two "can't see" room-light lines drive auto-light (PR 9.K) to
    // post a LightSource need. The penalized lines (barely visible / dimly
    // lit) still render room contents and have no auto-action, so they're
    // not seeded. Wording per docs/auto-engine-orchestration.md (MMUD
    // Explorer reproduction); confirm against a live capture before
    // relying on the exact phrasing.
    public const string RoomPitchBlack   = "light.room-pitch-black";   // "The room is pitch black"
    public const string RoomVeryDark     = "light.room-very-dark";     // "The room is very dark - you can't see anything"

    // ----- Room & status -------------------------------------------------
    public const string RoomExits        = "room.exits";
    public const string StatusLine       = "status.line";
    public const string UserExperience   = "status.user-experience";
    public const string UserProfile      = "status.user-profile";
    public const string UserEncumbrance  = "status.user-encumbrance";

    // ----- Player presence ----------------------------------------------
    public const string PlayerDisconnects = "presence.player-disconnects";
    public const string PlayerHungUp      = "presence.player-hung-up";    // "X just hung up!!!" — clean logoff via in-game hangup command; some BBSes disable this line entirely
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
    public const string PartyYouInvited     = "party.you-invited";       // "You have invited X to follow you." — our own outbound invite confirmation
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

    // ----- Alignment -----------------------------------------------------
    /// <summary>
    /// "A dark cloud passes over you" — MajorMUD's signal that the local
    /// character's alignment just shifted toward evil (an evil-point gain).
    /// The displayed alignment word doesn't update until the next <c>who</c>,
    /// so <see cref="Game.AlignmentTracker"/> flags it stale on this line and
    /// clears the flag once our own row is re-observed.
    /// </summary>
    public const string AlignmentDarkCloud = "alignment.dark-cloud";

    // ----- Main menu -----------------------------------------------------
    // BBSes customise the banner version + realm name + prompt text, but
    // the menu options themselves are stable across customisations.
    // The "Enter the Realm" row is the universal main-menu signature.
    public const string MainMenuEnterRealm = "menu.enter-realm";   // "[E] . Enter the Realm" — universal main-menu line

    // ----- Trainer menu marker (Phase 6 follow-up) ----------------------
    // The "train stats" trainer screen has a "Point Cost Chart" panel
    // header in the upper-right that doesn't appear in any other
    // game-mode output. Combined with outbound-`train stats` gating
    // in TrainerMenuTracker, this is our entry signal for the
    // re-invite-after-trainer-menu flow.
    public const string MenuTrainerStatsMarker = "menu.trainer-stats-marker";

    // ----- Suicide password flow (Phase 6 follow-up) ---------------------
    // Drives the SuicidePasswordTracker state machine and the engine-
    // send gate. The whole point of pattern-matching these is to
    // (a) detect when we've entered a password-entry prompt and lock
    // out engine auto-sends so they don't pollute the input, and
    // (b) capture the password the user types so we can store it
    // encrypted on the profile for @suicide consumption.
    public const string SuicidePromptOldPassword = "suicide.prompt-old";   // "Enter the current password:"  (set-flow when password exists)
    public const string SuicidePromptNewPassword = "suicide.prompt-new";   // "Enter New Password:"          (set-flow new entry)
    public const string SuicidePromptUseSuicide  = "suicide.prompt-use";   // "Enter your suicide password:" (plain `suicide` command when password is set)
    public const string SuicideInvalidPassword   = "suicide.invalid";      // "Invalid password specified."  (wrong old password OR wrong use-suicide password)
    public const string SuicideNotSet            = "suicide.not-set";      // "You do not have a suicide password set."  (response to `pro`)
    public const string SuicidePasswordChanged   = "suicide.changed";      // "Password Changed"             (success commit)
    public const string SuicidePasswordNotChanged = "suicide.not-changed"; // "Password NOT changed"         (empty-CR into new-password prompt)
    public const string Reroll                   = "reroll";               // "After a LONG thought, you take your own life" (successful suicide → character rerolled)
    public const string LearnSpell               = "spell.learn";          // "You read <scroll> and learn the spell <name>." (group 1 = spell Name)

    // ----- Trap-disarm flow (Phase 6 @trap handler) ----------------------
    // Drives TrapDisarmManager's search → disarm state machine. Failure
    // messages for the disarm phase are tracked in the per-realm
    // Messages catalogue today; once the canonical handler stabilises
    // they'll migrate here so the catalogue can prune its trap entries.
    public const string TrapFoundInSearch     = "trap.found-in-search";   // "You found a trap to the <dir>!"
    public const string TrapNoneInSearch      = "trap.none-in-search";    // "You notice nothing different to the <dir>."
    public const string TrapDisarmedSuccess   = "trap.disarmed-success";  // "You successfully disarmed the trap to the <dir>."

    // ----- Door open/bash/pick (Phase 7 DoorOpenManager) ----------------
    // Drives the walker's door FSM. Phrasing ports from MudProxy's door
    // handler — covers the door / gate noun pair (some realms render
    // "gate" for the same lock state) and the bash / pick / open verbs.
    public const string DoorBashSuccess       = "door.bash.success";       // "you bashed the door open" / "bashed the gate open"
    public const string DoorBashFailure       = "door.bash.failure";       // "your attempts to bash through fail"
    public const string DoorPickSuccess       = "door.pick.success";       // "you successfully unlock the door"
    public const string DoorPickFailure       = "door.pick.failure";       // "your lockpicking skill fails you"
    public const string DoorPickNotLocked     = "door.pick.notlocked";     // "was not locked"
    public const string DoorOpenedNow         = "door.opened.now";         // "is now open" (after open)
    public const string DoorAlreadyOpen       = "door.opened.already";     // "is already open"
    public const string DoorIsLocked          = "door.islocked";           // "is locked" (open hit a keyed door)
    public const string DoorKeyUnlockSuccess  = "door.key.unlocked";       // "successfully unlocked" (after use <key> <dir>)
    public const string DoorKeyUnknown        = "door.key.unknown";        // "have no <item>" / "you don't have" (use <key> failed)

    // ----- Another player forcing a door (LeaderDoorAssistManager) -------
    // Observer-side line emitted when another in-room player fails to bash
    // a door: "You see <name> attempt to bash the door to the <dir>."
    // Captures the actor name + the direction word so we can pitch in on
    // the same door when the actor is our party leader.
    public const string PlayerDoorBashAttempt = "door.player.bashattempt";
}
