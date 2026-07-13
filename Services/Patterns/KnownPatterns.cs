namespace FujinTerm.Services.Patterns;

// Stable identifiers for the default MessageRouter pattern seed. Subsystems
// subscribe by name — router.Subscribe(KnownPatterns.UserHits, handler) —
// instead of re-typing regex strings everywhere. Users can also reference these
// IDs from the Trigger UI when they want to react to a built-in pattern.
//
// Naming convention: category.action in lower-kebab, matching the rest of the
// codebase (MenuCommandIds).
public static class KnownPatterns
{
    // ----- Stealth -------------------------------------------------------
    public const string UserSneaking      = "stealth.user-sneaking";
    public const string UserNotSneaking   = "stealth.user-not-sneaking";
    public const string UserSneakFailed   = "stealth.user-sneak-failed";
    public const string UserSneakInitiate = "stealth.user-sneak-initiate";
    public const string UserCantSneak     = "stealth.user-cant-sneak";
    // Hide has no positive self-confirmation: a clean "Attempting to hide..."
    // means the server ran a hide check but does NOT report the outcome, so the
    // client treats it as *optimistically* hidden. The only knowable failure is
    // the suffixed "...You don't think you are hidden." line.
    public const string UserHideInitiate  = "stealth.user-hide-initiate";
    public const string UserHideFailed    = "stealth.user-hide-failed";

    // ----- Movement ------------------------------------------------------
    public const string DirectionFailed   = "movement.direction-failed";
    public const string BashFailed        = "movement.bash-failed";
    public const string HeardMovement     = "movement.heard-movement";
    // Left-behind disambiguators. When a party leader moves and we can't
    // follow, the game prints one of these the instant
    // before "You are no longer following X." — distinguishing a genuine
    // left-behind (auto-`@comeback`) from a deliberate uninvite/unfollow.
    public const string MovementFailedStuck = "movement.failed-stuck";   // "You can't seem to move anywhere!" — a prevents-movement gamedata flag blocked us
    public const string MovementFailedHeavy = "movement.failed-heavy";   // "...too heavy to move" — over-encumbered (system line; never inside a chat channel)
    // "You are blind." (period) — a move issued while blinded SUCCEEDS but
    // starves the room display, exactly like a dark room. Distinct from the
    // affliction-onset "You are blind!" (exclamation) and from the refusal
    // "You can't see well enough to move." (a bonk). Drives dead-reckoning.
    public const string BlindMoveStarved  = "movement.blind-move-starved";
    // Paradigm-only `rm` reply — "Location:      <map>,<room>". The authoritative
    // (map, room) the game reports for the player's own position; stock realms
    // have no `rm`. ParadigmPositionResolver consumes this while it has an `rm`
    // request in flight to re-anchor the tracker + recovery gate. The sibling
    // "Regen Time:" / "Room Illu:" lines of the same block deliberately don't
    // match (the pattern is anchored to the Location label + a numeric pair).
    public const string ParadigmLocation  = "movement.paradigm-location";

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

    // The local player's own swing MISSING. On the live realm a whiff prints the
    // same first-person swing skeleton as a hit ("You punch acid slime!") but
    // WITHOUT the "for N damage" tail — there is no literal "miss" word — so the
    // pattern matches a "You <verb> <target>!" line and excludes the hit form
    // (owned by UserHits) via a look-ahead for "for N damage". Distinct from
    // MobMisses (an incoming attack the mob whiffs): this is OUR offensive miss,
    // the denominator for the session hit/miss accuracy. The skeleton also
    // matches self-emotes ending in "!", so CombatSessionTracker only counts a
    // match while combat is engaged.
    public const string UserMisses           = "combat.user-misses";

    // The local player DODGING an incoming attack — "The <mob> … but you
    // dodge!". Carved out of MobMisses (whose broad "<mob> … at you" shape also
    // matches a dodge line) so dodges can be counted separately for the
    // dodge-rate denominator. Keys off the canonical "you dodge" phrase, which
    // only appears on a successful dodge.
    public const string UserDodges           = "combat.user-dodges";

    // Local-player death — "You have been slain by <killer>." per MajorMUD's
    // canonical wording. DeathLineWatcher subscribes here and emits the
    // PlayerDied event that DeathRecoveryManager consumes for corpse-recovery.
    public const string UserSlain            = "combat.user-slain";

    // "X moves to attack Y." — emitted by the server for every player's combat
    // announce, including ours. Used by CombatManager to implement the
    // AttackTiming re-fire mechanism (AttackLastParty / AttackLastRoom /
    // AttackAfter). The pattern tolerates the bracketed-prompt prefix
    // ("[HP=100/MA=50]:X moves to attack Y.") plus a bare colon prefix; the
    // announcer name + target are positional captures.
    public const string PartyAttackAnnounce  = "combat.party-attack-announce";

    // "X moves to cast <spell> upon Y." — a caster's round action. GAME_MECHANICS:
    // a party member has "gone" for the round via EITHER the melee "moves to attack"
    // form OR this spell form, so attack-last coordination treats both as equivalent
    // per-member announce signals. CombatManager routes this to the Attack-Order
    // re-fire only (never target-follow, so a heal can't make us swing at a mate).
    // Same prefix tolerance + positional announcer/target captures as the melee form.
    public const string PartyCastAnnounce    = "combat.party-cast-announce";

    // "You don't see <X> here!" — server's response when our `attack X` resolves
    // against a target that left or died between our send and the server's
    // resolve (our death-line match was missed, the mob fled, a partymate killed
    // it first, etc.). CombatManager clears _currentTarget and forces a room
    // re-display.
    public const string TargetNotHere        = "combat.target-not-here";

    // "Your weapon has no effect against this monster!" — server's signal that
    // the currently-equipped weapon can't damage the current target.
    // CombatManager swaps to the AlternateWeapon on the first such line (or
    // marks the monster unhittable if already on alt) — continuing to swing the
    // same weapon can't turn a no-effect into a hit.
    public const string WeaponNoEffect       = "combat.weapon-no-effect";

    // "Your fists have no effect against this monster!" — our weapon fell off
    // (encumbrance, server quirk, missed equip-confirm). CombatManager treats
    // this as "re-equip from scratch" and clears the shadow-equipped state.
    public const string FistsNoEffect        = "combat.fists-no-effect";

    // ----- Spellcasting -------------------------------------------------
    // "You attempt to cast <spell>, but fail." — failed concentration / fizzle.
    // Blocks further casts for the current round.
    public const string CastFizzled          = "spell.cast-fizzled";

    // "You do not have enough mana to cast that spell." — blocks further casts
    // until mana recovers.
    public const string CastNoMana           = "spell.cast-no-mana";

    // "You have already cast a spell this round!" — only one cast per combat
    // round; blocks until the next tick.
    public const string CastAlreadyThisRound = "spell.cast-already-this-round";

    // "You lost your concentration on the spell!" — mid-cast interrupt (took
    // damage during prep, broke stealth, etc.).
    public const string CastInterrupted      = "spell.cast-interrupted";

    // "Your spell has no effect on <monster>." — the target is immune to the
    // attack spell we just cast (priest `harm` vs an acid slime, etc.). Group 0
    // captures the monster name. CombatManager canonicalizes it to base species
    // and marks that species attack-spell-immune so the chooser skips the
    // primary attack spell to the alternate (then the weapon command) for the
    // rest of the room.
    public const string SpellNoEffect        = "spell.no-effect";

    // ----- Cash --------------------------------------------------------
    // "There are N <coin> pieces here." / singular variant. Fired on room
    // display when cash is on the ground. CashManager subscribes to react per
    // CashSettings policy.
    public const string CashOnGround        = "cash.on-ground";

    // "You picked up N <coin> pieces." / singular variant — confirmation that a
    // get succeeded. CashManager updates internal tallies + the auto-deposit
    // trigger check.
    public const string CashPickedUp        = "cash.picked-up";

    // "You dropped N <coin> pieces." — discard confirmation.
    public const string CashDropped         = "cash.dropped";

    // "You hid N <coin> pieces." — stash-room confirmation. Wire shape distinct
    // from CashDropped because the `hide` command is the stash-room verb in stock
    // MajorMUD. Without this, the held-coin tally goes stale after a stash and
    // the auto-deposit threshold fires on phantom wealth.
    public const string CashHidden          = "cash.hidden";

    // "N <coin> drop to the ground." — corpse-spawned cash after a monster kill
    // (combat-log shape, distinct from the room-display CashOnGround). Currency
    // word is the short form ("silver", "gold", …) without the "pieces" suffix.
    // CashManager dispatches through the same per-currency policy as CashOnGround
    // so kill-loot follows the user's Collect / Discard / Ignore choices.
    public const string CashFromKill        = "cash.from-kill";

    // "You notice <list> here." — the realm-specific room-survey line. Cash
    // entries appear FIRST (server orders runic → platinum → gold → copper →
    // silver), followed by items. Comma-separated, last entry ends with a period.
    // Server wraps at 80 cols mid-token so multi-line stitching is required.
    // CashManager parses each comma-separated entry and pulls recognisable
    // currency tokens for tally + collect dispatch.
    public const string YouNoticeRoom       = "cash.you-notice-room";

    // Room-entry arrival — "<name> <verb> into the room from <direction>." Fires
    // when a monster spawns OR a player walks in mid-room (no full re-display).
    // The wire colours the name segment yellow for monsters, red for players;
    // RoomEntryWatcher reads the colour off the line's attribute strip and uses
    // it as a hint when the name doesn't match the active game-data tables.
    // Direction is any cardinal / non-cardinal / up / down or the literal
    // "nowhere" (script-spawn).
    public const string RoomEntryArrival     = "presence.room-entry-arrival";

    // Room-exit departure — "<name> walks out of the room to <direction>." Fires
    // when a monster leaves our room, most often when a fleeing player drags the
    // mob we were engaged with out with them. RoomDepartureWatcher strips the
    // article and removes the matching entity from the room observation so the
    // Combat gate the mob was holding drops instead of freezing the walker.
    public const string RoomEntryDeparture   = "presence.room-entry-departure";

    // Sneak-arrival notice — "You notice <name> sneaking in from the <dir>."
    // Fires ONLY when a player fails a sneak into our room; monsters never emit
    // it. Split out from RoomEntryArrival because that pattern greedily captured
    // the whole "You notice <name>" as the arrival name and — the wire paints
    // this line the monster hue — tagged it a null-numbered Monster that held
    // the Combat gate open with no death/departure line to ever clear it.
    // RoomEntryWatcher classifies this line Player unconditionally.
    public const string SneakArrivalNotice   = "presence.sneak-arrival-notice";

    // ----- Conversation --------------------------------------------------
    public const string ConversationGossip      = "conversation.gossip";
    public const string ConversationBroadcast   = "conversation.broadcast";
    public const string ConversationGangpath    = "conversation.gangpath";
    public const string ConversationTelepathIn  = "conversation.telepath-in";    // incoming "X telepaths: msg"
    public const string ConversationTelepathOut = "conversation.telepath-out";   // outgoing "--- Telepath sent to X ---"
    public const string ConversationYell        = "conversation.yell";           // both "X yells" and "You yell"
    public const string ConversationLocal       = "conversation.local";
    // Paradigm-only server PvP kill announcement — "Server PvP Message: X just
    // killed Y!". ChatRouter realm-gates this to paradigm before emitting.
    public const string ConversationServerPvp   = "conversation.server-pvp";
    // UserEmote intentionally omitted — the emote line is distinguished purely
    // by ANSI colour bytes the LineExtractor consumes. Re-add when
    // attribute-aware matching ships.

    // ----- Item actions --------------------------------------------------
    public const string UserHides         = "item.user-hides";
    public const string PlayerGets        = "item.player-gets";     // combined: own + others
    public const string PlayerDrops       = "item.player-drops";    // combined: own + others
    public const string UserEquipped      = "item.user-equipped";   // wearing + lit (torches etc.)
    public const string UserEquipFailed   = "item.user-equip-failed";
    public const string UserRemoved       = "item.user-removed";
    public const string HiddenItems       = "item.hidden-items";
    public const string ShopListHeader    = "item.shop-list-header";
    public const string UserBuys          = "item.user-buys";        // "You just bought X for <price>." (any currency, incl. "nothing")
    public const string UserSells         = "item.user-sells";       // "You sold X for <price>."
    public const string UserBuyFailed     = "item.user-buy-failed";  // "You cannot afford X."
    public const string UserSellRefused   = "item.user-sell-refused";// "You cannot sell X here."

    // ----- Room light ----------------------------------------------------
    // The two "can't see" room-light lines drive auto-light to post a
    // LightSource need. The penalized lines (barely visible / dimly lit) still
    // render room contents and have no auto-action, so they're not seeded.
    // Confirm the exact wording against a live capture before relying on it.
    public const string RoomPitchBlack   = "light.room-pitch-black";   // "The room is pitch black"
    public const string RoomVeryDark     = "light.room-very-dark";     // "The room is very dark - you can't see anything"
    // "Your <light> flickers and goes out." — the readied light burned out. The
    // inventory snapshot's ReadiedLight only refreshes on the next `i` dump, so
    // this line is auto-light's live signal that the lit light is now gone, letting
    // the next dark-room line re-ready a carried spare instead of being blocked by
    // a stale readied-light value.
    public const string LightBurnedOut   = "light.burned-out";

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
    public const string PlayerTeleportsIn = "presence.player-teleports-in"; // "X appears in a blinding flash of light!" — another player materialising in our room (recall / party-splitting chime teleport)
    public const string PartyInviteReceived = "presence.party-invite-received"; // "X has invited you to follow him/her." — incoming party invite from another player (Playpen-verified wording; MajorMUD player chars are male/female only)

    // ----- Party --------------------------------------------------------
    // Single-line membership signals. The `par` table itself is multi-line
    // and parsed via a small state machine in `PartyManager` (same shape
    // as `WhoListParser`), not by a one-line regex.
    public const string PartyFollowsYou     = "party.follows-you";       // "X started to follow you."
    public const string PartyYouFollowing   = "party.you-following";     // "You are now following X."  (we joined someone's party)
    // " -- Following your Party leader <dir> --" — the game dragging us one room
    // in the leader's wake. A follower sends no movement bytes, so this line is
    // the ONLY movement signal for a dragged follower; FollowMoveObserver turns it
    // into a RoomTracker.NoteMoveSent so the map stays located instead of drifting
    // to Lost. Group 0 captures the long-form direction word.
    public const string PartyFollowMove     = "party.follow-move";
    public const string PartyStopsFollowing = "party.stops-following";   // "X has stopped following you." / "X stops following you."
    public const string PartyYouInvited     = "party.you-invited";       // "You have invited X to follow you." — our own outbound invite confirmation
    public const string PartyHeader         = "party.par-header";        // "The following people are in your travel party:" — anchors the par-block state machine
    public const string PartyMemberDeath    = "party.member-death";      // "X has been slain by Y" — conservative kill-attribution match
    public const string PartyMemberDied     = "party.member-died";       // "X has died." — the universal third-person death line any observer sees; the consumer scopes it to party members by roster match (see PartyDeathRosterCleanup)
    // "X drops to the ground!" — the room/party-side signal a player hit 0 HP
    // (mortally wounded, not yet dead). Seen by everyone present, including the
    // dropper (with their own name). AllyDroppedHandler scopes it to a party /
    // recently-partied ally (excluding self, whose self-drop PlayerDroppedGate
    // owns) and reacts with aid + name-targeted heal.
    public const string PartyMemberDropped  = "party.member-dropped";
    // "You have aided X, ..." — confirmation OUR `aid <name>` landed, so the
    // downed ally is back to positive HP and can act again. Gates the switch from
    // "aiding" to "keep healing by name / re-invite".
    public const string UserAidedAlly       = "party.user-aided-ally";
    // "<Leader> is dragging you around." — printed to a mortally-wounded
    // character on each move the dragger makes while hauling the downed body
    // around (a leader-issued `drag <name>`). A dropped character can't move on
    // its own, so this is the only signal that someone's relocating us while
    // we're down. Group 1 captures the dragger's given name; DraggedTracker keys
    // on it to answer "who's dragging me?" for the @join / @invite refusal reply.
    public const string PartyDraggedAround  = "party.dragged-around";
    // ----- Dissolution signals (Playpen-verified, 2026-06-01) ----------
    public const string PartyFollowerRemoved      = "party.follower-removed";       // "X has been removed from your followers." — leader's view of an uninvite
    public const string PartyYouNoLongerFollowing = "party.you-no-longer-following";// "You are no longer following X." — follower's view of leader's uninvite / our own unfollow
    public const string PartyDissolved            = "party.dissolved";              // "You are not in a party at the present time." — authoritative wipe
    // Per-member rank-change observation — fires whenever someone in the
    // party reranks via `frontr` / `midr` / `backr`. Lets PartyManager
    // update PartyMember.Rank live instead of waiting for the next par poll.
    public const string PartyMemberRankChanged    = "party.member-rank-changed";    // "X just moved to the {front|back} rank in your group." / "X just moved to the middle of your group."
    public const string PartySelfRankChanged      = "party.self-rank-changed";      // "You have moved to the {front|middle|back} ranks of your group." — self's own rerank confirmation
    // "X stops to rest." — a party member (roster-scoped by the handler) sat down
    // to rest. Flips PartyMember.Resting the moment it's seen, ahead of the 5s par
    // poll, so a follower can mirror the leader's rest immediately. (The meditate
    // observation line isn't confirmed yet — add its const + pattern beside this
    // when the wording is known.)
    public const string PartyMemberRestObserved   = "party.member-rest-observed";   // "X stops to rest."

    // ----- Alignment -----------------------------------------------------
    // "A dark cloud passes over you" — MajorMUD's signal that the local
    // character's alignment just shifted toward evil (an evil-point gain). The
    // displayed alignment word doesn't update until the next `who`, so
    // AlignmentTracker flags it stale on this line and clears the flag once our
    // own row is re-observed.
    public const string AlignmentDarkCloud = "alignment.dark-cloud";

    // ----- Training ------------------------------------------------------
    // "You hand over <cost> and you receive training to attain level N." — the
    // server's confirmation that a `train` levelled the character up. Group 1
    // captures the attained level. Drives auto-train's level-up detection and the
    // Level Projection / CP-plan level sync.
    public const string TrainAttainLevel = "train.attain-level";

    // "You have progressed too far to use the training provided here." — the
    // trainer rejecting a `train` because the character has out-levelled this
    // trainer's band. A hard stop for the `@train` multi-level loop: no further
    // levels can be trained at this location.
    public const string TrainProgressedTooFar = "train.progressed-too-far";

    // "You do not have the money required for your training." — the trainer
    // rejecting a `train` for lack of coin. Also stops the `@train` loop (no
    // point re-sending until the purse is fixed).
    public const string TrainNoMoney = "train.no-money";

    // ----- Main menu -----------------------------------------------------
    // BBSes customise the banner version + realm name + prompt text, but
    // the menu options themselves are stable across customisations.
    // The "Enter the Realm" row is the universal main-menu signature.
    public const string MainMenuEnterRealm = "menu.enter-realm";   // "[E] . Enter the Realm" — universal main-menu line

    // ----- Trainer menu marker -------------------------------------------
    // The "train stats" trainer screen has a "Point Cost Chart" panel
    // header in the upper-right that doesn't appear in any other
    // game-mode output. Combined with outbound-`train stats` gating
    // in TrainerMenuTracker, this is our entry signal for the
    // re-invite-after-trainer-menu flow.
    public const string MenuTrainerStatsMarker = "menu.trainer-stats-marker";

    // ----- Suicide password flow -----------------------------------------
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

    // ----- Trap-disarm flow ----------------------------------------------
    // Drives TrapDisarmManager's search → disarm state machine. Failure
    // messages for the disarm phase are tracked in the per-realm
    // Messages catalogue today; once the canonical handler stabilises
    // they'll migrate here so the catalogue can prune its trap entries.
    public const string TrapFoundInSearch     = "trap.found-in-search";   // "You found a trap to the <dir>!"
    public const string TrapNoneInSearch      = "trap.none-in-search";    // "You notice nothing different to the <dir>."
    public const string TrapDisarmedSuccess   = "trap.disarmed-success";  // "You successfully disarmed the trap to the <dir>."

    // ----- Door open/bash/pick -------------------------------------------
    // Drives the walker's door FSM — covers the door / gate noun pair (some
    // realms render "gate" for the same lock state) and the bash / pick / open
    // verbs.
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
