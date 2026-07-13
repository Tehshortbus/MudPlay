using System.Text.RegularExpressions;

namespace FujinTerm.Services.Patterns;

// Registers the baseline MajorMUD pattern set against a MessageRouter. Each
// entry pairs a stable pattern id with the regex that recognises one shape of
// game output.
//
// Multi-line "batch" patterns (the who list, player-status) are not represented
// here — IMessagePattern operates one line at a time, so those have their own
// dedicated parsers.
//
// The user-emote line is also omitted: it's distinguished purely by ANSI colour
// bytes that the LineExtractor consumes before the line surfaces, so detecting
// it needs attribute-aware matching (the row's foreground is green) rather than
// a text regex.
public static class DefaultPatterns
{
    // Populate router's known-patterns catalog. No handlers are attached — each
    // subsystem (ChatRouter, combat tracker, etc.) registers its own handlers by
    // id via MessageRouter.Subscribe.
    public static void Seed(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        foreach (IMessagePattern pattern in BuildDefaultPatterns())
        {
            router.RegisterPattern(pattern);
        }
    }

    // Enumerate every default pattern instance. Exposed so tests can inspect the
    // registry without having to wire a router.
    public static IEnumerable<IMessagePattern> BuildDefaultPatterns()
    {
        // ----- Stealth ---------------------------------------------------
        yield return new RegexPattern(KnownPatterns.UserSneaking,      @"^Sneaking\.\.\.");
        yield return new RegexPattern(KnownPatterns.UserNotSneaking,   @"^You make a sound as you enter the room!");
        yield return new RegexPattern(KnownPatterns.UserSneakFailed,   @"^Attempting to sneak\.\.\.You don't think you're sneaking\.");
        yield return new RegexPattern(KnownPatterns.UserSneakInitiate, @"^Attempting to sneak\.\.\.$");
        yield return new RegexPattern(KnownPatterns.UserCantSneak,     @"^You may not sneak right now!");
        // UserHideFailed carries the suffix; UserHideInitiate is $-anchored so it
        // only matches the bare (outcome-ambiguous) attempt line.
        yield return new RegexPattern(KnownPatterns.UserHideFailed,     @"^Attempting to hide\.\.\.You don't think you are hidden\.");
        yield return new RegexPattern(KnownPatterns.UserHideInitiate,   @"^Attempting to hide\.\.\.$");

        // ----- Room light ------------------------------------------------
        // Only the two "can't see" lines; prefix-anchored so trailing
        // punctuation / dash variants don't break the match.
        yield return new RegexPattern(KnownPatterns.RoomPitchBlack, @"^The room is pitch black");
        yield return new RegexPattern(KnownPatterns.RoomVeryDark,   @"^The room is very dark");
        // A readied light burning out. Anchored to "Your ... flickers and goes
        // out." so it can't collide with a quoted chat echo (which starts with a
        // speaker/quote, never "Your"). The item name is captured but unused — the
        // engine re-readies whatever spare it carries, not the same item by name.
        yield return new RegexPattern(KnownPatterns.LightBurnedOut,
            @"^Your .+ flickers and goes out\.$");

        // ----- Movement --------------------------------------------------
        // Two forms folded into one alternation: the no-exit line and the
        // closed door/gate line, both meaning "you didn't move".
        yield return new RegexPattern(KnownPatterns.DirectionFailed,
            @"^(?:There is no exit in that direction!|The (?:door|gate) is closed(?: in that direction)?!)");
        yield return new RegexPattern(KnownPatterns.BashFailed,
            @"^Your attempts to bash through fail!$");
        yield return new RegexPattern(KnownPatterns.HeardMovement,
            @"^You hear movement to the (?<direction>\w+)\.");
        // Left-behind disambiguators. "You can't seem to move anywhere!" fires
        // when a prevents-movement gamedata flag blocks us; "...too heavy to
        // move" fires when over-encumbered.
        // The heavy form is anchored ^[^"]* so a quoted chat line (all
        // MajorMUD player chat is quoted) carrying the phrase can never
        // match — only the unquoted system line does.
        yield return new RegexPattern(KnownPatterns.MovementFailedStuck,
            @"^You can't seem to move anywhere!");
        yield return new RegexPattern(KnownPatterns.MovementFailedHeavy,
            @"^[^""]*too heavy to move");
        // Fully anchored to the standalone period form — a move made while
        // blind renders exactly this line and nothing else. The exclamation
        // onset ("You are blind!") ends in '!' so it can't match, and the
        // full-line anchor keeps a quoted chat echo of the phrase out.
        yield return new RegexPattern(KnownPatterns.BlindMoveStarved,
            @"^You are blind\.$");
        // Paradigm `rm` reply — "Location:      1,1729". The label is padded to a
        // column, so allow leading + inter-token whitespace; the trailing $ keeps
        // the "Regen Time:" / "Room Illu:" siblings (and any quoted chat echo) out.
        // Groups: [0]=map, [1]=room.
        yield return new RegexPattern(KnownPatterns.ParadigmLocation,
            @"^\s*Location:\s+(?<map>\d+),(?<room>\d+)\s*$");

        // ----- Failures --------------------------------------------------
        yield return new RegexPattern(KnownPatterns.CommandNoEffect, @"^Your command had no effect\.$");
        yield return new RegexPattern(KnownPatterns.CommandIgnored,  @"^You are typing too quickly - command ignored");
        yield return new RegexPattern(KnownPatterns.SlowDown,        @"^Why don't you slow down for a few seconds\?");

        // ----- Searching -------------------------------------------------
        yield return new RegexPattern(KnownPatterns.UserSearchFailed,
            @"^You notice nothing different to the \w+");
        // Cardinal forms use "to the <dir>" ("north" / "northeast" / etc.);
        // U/D drop the preposition and use the "<dir>wards" suffix
        // ("upwards" / "downwards"). One regex covers both — the
        // direction word lands in the same capture group either way.
        yield return new RegexPattern(KnownPatterns.UserSearchSucceeded,
            @"^You found an exit (?:to the )?(?<direction>\w+)!");

        // ----- Combat ----------------------------------------------------
        yield return new RegexPattern(KnownPatterns.CombatStatus,
            @"^\*Combat (?<status>Engaged|Off)\*");
        yield return new RegexPattern(KnownPatterns.UserHits,
            @"^(?<source>[\w]+) (?:critically )?(?:\w+) (?<target>[\w- ]+) for (?<damage>\d+) damage!");
        // Trailing punctuation varies per realm — real output uses ".", "!",
        // ",", and ";" depending on whether the miss line continues with a
        // dodge / parry / "but misses!" follow-up. Use a word boundary after
        // "you" so any non-letter delimiter classifies.
        yield return new RegexPattern(KnownPatterns.MobMisses,
            @"^The (?<target>[\w -]+) \w+ at you\b");
        yield return new RegexPattern(KnownPatterns.MobHits,
            @"^The (?<target>[\w -]+) \w+ you for (?<damage>\d+) damage!");
        yield return new RegexPattern(KnownPatterns.UserGainExperience,
            @"^You gain (?<exp>\d+) experience\.");
        // The local player's own swing missing. On the live realm a whiff
        // renders as the SAME first-person swing skeleton as a hit
        // ("You punch acid slime!") but WITHOUT the "for N damage" tail — there
        // is no literal word "miss". So we match a "You <verb> <target>!" line
        // and exclude the hit form with a negative look-ahead for "for N damage"
        // (which UserHits owns). [^!] body so the swing terminates at its own
        // "!". This shape also matches the older explicit wording
        // ("You swing at the kobold, but miss!"). It will additionally match
        // self-emotes that end in "!" ("You feel much better!"), so
        // CombatSessionTracker only counts a UserMisses line while combat is
        // engaged — see its CombatStatus gate.
        yield return new RegexPattern(KnownPatterns.UserMisses,
            @"^You (?![^\n]*\bfor \d+ damage\b)[^!]+!");
        // Local player dodges an incoming mob attack. The dodge line ("The
        // kobold thief lunges at you, but you dodge!") also
        // satisfies MobMisses, so CombatSessionTracker de-dupes by skipping
        // a MobMisses line that carries "dodge". Keyed on the "you dodge"
        // phrase, which is unique to a successful dodge.
        yield return new RegexPattern(KnownPatterns.UserDodges,
            @"^The (?<source>[\w -]+?) .*\byou dodge\b");

        // Local-player death. MajorMUD's canonical wording is "You have been
        // slain by <killer>." — the killer is whatever last hit landed (monster
        // name OR another player for PvP, even though FujinTerm scopes engines
        // to PvE; we still observe the line so DeathRecoveryManager can fire).
        // The trailing "." is captured tolerantly: some realms include a
        // trailing "!" instead.
        yield return new RegexPattern(KnownPatterns.UserSlain,
            @"^You have been slain by (?<killer>[\w '-]+?)[.!]\s*$");

        // "You don't see <X> here!" — target-gone signal. Trailing punctuation
        // tolerant — "!" canonical but some realms emit ".".
        yield return new RegexPattern(KnownPatterns.TargetNotHere,
            @"^You don't see (?<target>.+?) here[.!]\s*$");

        // Weapon-no-effect signals.
        yield return new RegexPattern(KnownPatterns.WeaponNoEffect,
            @"^Your weapon has no effect against this monster!");
        yield return new RegexPattern(KnownPatterns.FistsNoEffect,
            @"^Your fists have no effect against this monster!");

        // ----- Spellcasting failures ------------------------------------
        // Cast outcomes that block further casts for the current round.
        // CastCoordinator subscribes to flag the engine's _castBlocked latch;
        // CastingDirector waits for the next CombatTick before retrying.
        yield return new RegexPattern(KnownPatterns.CastFizzled,
            @"^You attempt to cast (?<spell>.+?), but fail\.");
        yield return new RegexPattern(KnownPatterns.CastNoMana,
            @"^You do not have enough mana to cast that spell\.");
        yield return new RegexPattern(KnownPatterns.CastAlreadyThisRound,
            @"^You have already cast a spell this round!");
        yield return new RegexPattern(KnownPatterns.CastInterrupted,
            @"^You lost your concentration on the spell!");

        // Attack-spell immunity. Non-greedy capture stops at the first period
        // (no `$` — Multiline `$` won't match before `\r`). CombatManager marks
        // the species attack-spell-immune so the chooser skips the primary
        // attack spell.
        yield return new RegexPattern(KnownPatterns.SpellNoEffect,
            @"^Your spell has no effect on (?<target>.+?)\.");

        // ----- Cash ----------------------------------------------------
        // Stock MajorMUD wording for cash on the ground. Singular form
        // (1 coin) drops the count entirely ("There is a gold piece
        // here."). Plural carries the count + currency word. Currency
        // word capture so CashManager can dispatch per-currency policy
        // without per-currency regexes.
        yield return new RegexPattern(KnownPatterns.CashOnGround,
            @"^There (?:is a (?<currency>\w+) piece|are (?<count>\d+) (?<currency2>\w+) pieces) here\.");
        // Pickup / drop / stash confirmations. The coin is named in full —
        // "copper farthings", "silver nobles", "gold crowns", "platinum
        // pieces", "runic coins" — NOT a generic "pieces", and the pickup line
        // carries no trailing period. The leading currency word is captured as
        // `\w+` (not a fixed keyword list) because a board can rename the runic
        // word per-BBS ("quatloos coins"); the denomination is actually pinned
        // by the trailing coin NOUN, so anchoring on the specific noun set
        // (farthing|noble|crown|piece|coin) is what keeps a shared-verb item
        // line ("You dropped a silver key.") from being misread as coin — item
        // pickups use "You took", but item drops / hides share the verb.
        // CurrencyNaming.Canonicalize maps the captured word back to a
        // denomination downstream. Coin nouns mirror InventoryManager's
        // currency regexes; the plural `s?` covers count==1 singulars
        // ("1 silver noble"). "piece" stays in the noun set so synthetic
        // "N gold pieces" fixtures still resolve.
        yield return new RegexPattern(KnownPatterns.CashPickedUp,
            @"^You pick(?:ed)? up (?:a (?<currency>\w+)|(?<count>\d+) (?<currency2>\w+)) (?:farthing|noble|crown|piece|coin)s?\b");
        yield return new RegexPattern(KnownPatterns.CashDropped,
            @"^You drop(?:ped)? (?:a (?<currency>\w+)|(?<count>\d+) (?<currency2>\w+)) (?:farthing|noble|crown|piece|coin)s?\b");
        yield return new RegexPattern(KnownPatterns.CashHidden,
            @"^You hid (?:a (?<currency>\w+)|(?<count>\d+) (?<currency2>\w+)) (?:farthing|noble|crown|piece|coin)s?\b");
        // Corpse loot — "N <currency> drop to the ground." emitted
        // after the kill announce. Verb agreement is `drop` for plural
        // counts; tolerating `drops?` covers any singular-1 variant
        // the server might emit. CashFromKill is a separate pattern
        // (vs reusing CashOnGround) so each line shape stays
        // observable / documented; the CashManager handler funnels
        // both into the same policy dispatch.
        yield return new RegexPattern(KnownPatterns.CashFromKill,
            @"^(?<count>\d+) (?<currency>\w+) drops? to the ground\.");

        // Realm-specific room survey — "You notice <list> here." with
        // cash entries (always first) + items. The single-line case
        // is matched here; multi-line wraps stitch back through
        // CashManager.AttachLineExtractor.
        yield return new RegexPattern(KnownPatterns.YouNoticeRoom,
            @"^You notice (?<list>.+?) here\.\s*$");

        // Party / room attack announce. Tolerates the bracketed-prompt prefix
        // ("[HP=100/MA=50]:") OR a bare colon prefix that some realms emit
        // before the name. Captures the announcer's name + the target.
        yield return new RegexPattern(KnownPatterns.PartyAttackAnnounce,
            @"^(?:\[[^\]]*\]:|:)*(?<player>\w+) moves to attack (?<target>.+?)\.");

        // Caster round action — "X moves to cast <spell> upon Y." The spell name is
        // skipped (non-capturing) and the target after "upon" is captured, so the
        // announcer + target stay positional like the melee form. Attack-last treats
        // this as an equivalent per-member "gone this round" announce.
        yield return new RegexPattern(KnownPatterns.PartyCastAnnounce,
            @"^(?:\[[^\]]*\]:|:)*(?<player>\w+) moves to cast .+? upon (?<target>.+?)\.");

        // Room-entry arrival. Anchored on "in… from <dir>"
        // so a wide alternation of verbs (crawls, walks, slithers, lumbers,
        // teleports, materialises, …) is folded into a single \w+ capture.
        // Direction tolerates hyphens for "north-east" variants alongside the
        // canonical cardinals + "nowhere" (script spawn).
        // Tolerated phrasing variants, all verified in server output:
        //  • Preposition `in` or `into` — slow-creep verbs ("creeps in the
        //    room from nowhere.") use the locative `in` rather than `into`.
        //  • Optional " the room" — most lines carry it, but per-monster greet
        //    text (the GreetTXT field) drops it: "A cave bear lumbers in from
        //    the south!".
        //  • Optional "the " before the direction — greet-text arrivals say
        //    "from the south" where canonical lines say bare "from east".
        //  • Terminator `.` or `!` — greet-text arrivals end on a bang.
        // The leading (?!You notice ) guard keeps the sneak-arrival notice
        // ("You notice <name> sneaking in from the <dir>.") out of this pattern:
        // its greedy name capture would swallow "You notice <name>" and the
        // wire's monster hue would tag the sneaker a null-numbered Monster that
        // strands the Combat gate. SneakArrivalNotice handles that line instead.
        yield return new RegexPattern(KnownPatterns.RoomEntryArrival,
            @"^(?!You notice )(?<name>.+?) \w+ in(?:to)?(?: the room)? from (?:the )?(?<direction>[\w-]+)[.!]\s*$");

        // Sneak-arrival notice — a player who failed a sneak into our room.
        // Monsters never emit this; RoomEntryWatcher classifies it Player
        // unconditionally (the line's wire colour is the monster hue and can't
        // be trusted here). Only the "in from the <dir>." wording is confirmed;
        // add alternates here if the game emits others.
        yield return new RegexPattern(KnownPatterns.SneakArrivalNotice,
            @"^You notice (?<name>\w+) sneaking in from (?:the )?(?<direction>[\w-]+)[.!]\s*$");

        // Room-exit departure — mirror of the arrival line. "The orc rogue walks
        // out of the room to the above!" is the canonical form seen when a
        // fleeing player drags an engaged mob out with them. Only one departure
        // wording is confirmed so far; add alternates here if the game emits
        // others.
        yield return new RegexPattern(KnownPatterns.RoomEntryDeparture,
            @"^(?<name>.+?) walks out of the room to (?:the )?(?<direction>[\w-]+)[.!]\s*$");

        // ----- Conversation ---------------------------------------------
        // Auction lines share gossip's shape ("X auctions: ...") and the user
        // wants them filtered under the same Gossip toggle in the Conversation
        // window, so we classify both under one id via alternation on the verb.
        yield return new RegexPattern(KnownPatterns.ConversationGossip,
            @"^(?<player>\w+) (?:gossips|auctions): (?<message>.+)");
        yield return new RegexPattern(KnownPatterns.ConversationBroadcast,
            @"^Broadcast from (?<player>\w+) ""(?<message>.+)""");
        yield return new RegexPattern(KnownPatterns.ConversationGangpath,
            @"^(?<player>\w+) gangpaths: (?<message>.+)");
        // Telepath: incoming + outgoing have different shapes — split into two ids.
        yield return new RegexPattern(KnownPatterns.ConversationTelepathIn,
            @"^(?<player>\w+) telepaths: (?<message>.+)");
        // The verb's capitalization varies between BBSes — some realms emit
        // "Sent". Use IgnoreCase so both spellings classify; we don't assume a
        // realm.
        yield return new RegexPattern(KnownPatterns.ConversationTelepathOut,
            @"^--- Telepath sent to (?<player>\w+) ---$",
            options: System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Yell: combined own + others into one regex; player group empty for "You yell".
        yield return new RegexPattern(KnownPatterns.ConversationYell,
            @"^(?:(?<player>\w+) yells|You yell) ""(?<message>.+)""");
        // Combined own + others, mirroring Yell: a third party emits
        // "X says ""…""" (player captured); the local character's own echo
        // is "You say ""…""" (player group empty → self). Splitting the
        // verb forms keeps "You" out of the player group so downstream
        // consumers (notably RemoteCommandManager) never treat the local
        // character's own speech as an inbound @-command.
        yield return new RegexPattern(KnownPatterns.ConversationLocal,
            @"^(?:(?<player>\w+) says|You say) ""(?<message>.+)""");
        // user-emote is distinguished purely by ANSI colour bytes the
        // LineExtractor strips, so it's omitted until attribute-aware matching
        // ships — see the class summary above.

        // ----- Action / Items -------------------------------------------
        yield return new RegexPattern(KnownPatterns.UserHides,
            @"^You hid (?<item>.*)\.");
        // PlayerGets: combined own + others via alternation.
        yield return new RegexPattern(KnownPatterns.PlayerGets,
            @"^(?:(?<player>\w+) picks up|You took) (?<item>.*)\.");
        yield return new RegexPattern(KnownPatterns.PlayerDrops,
            @"^(?:(?<player>\w+) drops|You dropped) (?<item>.*)\.");
        yield return new RegexPattern(KnownPatterns.UserEquipped,
            @"^(?:You are now wearing|You lit the) (?<item>[\w ]+)\.$");
        yield return new RegexPattern(KnownPatterns.UserEquipFailed,
            @"^You may not wear that item!$");
        yield return new RegexPattern(KnownPatterns.UserRemoved,
            @"^You have removed (?<item>[\w ]+?)(?: and extinguished it)?\.$");
        yield return new RegexPattern(KnownPatterns.HiddenItems,
            @"^You notice (?<items>.*)(?:\r\n| )");
        yield return new RegexPattern(KnownPatterns.ShopListHeader,
            @"^The following items are for sale here:$");
        // Buy / sell result lines (any currency, or "nothing"/"0 copper farthings").
        // The auto-buy / auto-sell engines advance their per-item transaction
        // pumps off these; the price tail is captured but the engines gate on the
        // live result, not a predicted price.
        yield return new RegexPattern(KnownPatterns.UserBuys,
            @"^You (?:just )?bought (?:(?<qty>\d+) )?(?<item>.+?) for (?<price>.+)\.$");
        yield return new RegexPattern(KnownPatterns.UserSells,
            @"^You (?:just )?sold (?<item>.+?) for (?<price>.+)\.$");
        yield return new RegexPattern(KnownPatterns.UserBuyFailed,
            @"^You cannot afford (?<item>.+)\.$");
        yield return new RegexPattern(KnownPatterns.UserSellRefused,
            @"^You cannot sell (?<item>.+) here\.$");

        // ----- Room -----------------------------------------------------
        // Room-parser consumer: before splitting `exits` on comma, strip the
        // [A-Z]\b. artifact — BBSes embed direction-shortcut overstrike that
        // survives the emulator.
        yield return new RegexPattern(KnownPatterns.RoomExits,
            @"^Obvious exits: [\w, ]+");

        // ----- Status ---------------------------------------------------
        // HP is signed so the prompt still matches while mortally wounded
        // ([HP=-4/MA=31]:) — a dropped character's prompt must not go unrecognised.
        yield return new RegexPattern(KnownPatterns.StatusLine,
            @"^\[HP=(?<hp>-?\d{1,4})(?:\/(?<type>MA|KAI)=(?<mana>\d{1,3}))?(?:\s\((?<statea>Resting|Meditating)\)\s)?\]:(?:\s\((?<stateb>Resting|Meditating)\))?");
        yield return new RegexPattern(KnownPatterns.UserExperience,
            @"^Exp: (?<exp>\d+) Level: (?<level>\d+) Exp needed for next level: (?<need>\d+) \((?<req>\d+)\) \[(?<percent>\d+)%\]");
        yield return new RegexPattern(KnownPatterns.UserProfile,
            @"^(?:Recent Deaths:|Location:)");
        yield return new RegexPattern(KnownPatterns.UserEncumbrance,
            @"^Encumbrance:\s+\d+");

        // ----- Player presence ------------------------------------------
        yield return new RegexPattern(KnownPatterns.PlayerDisconnects,
            @"^(?<player>\w+) just disconnected!!!\.");
        // Clean logoff via the in-game hangup command. Distinct from the
        // BBS-level "[Account] logs OFF" signal — that one's account-name
        // keyed and we have no reliable account→character mapping at the
        // observation layer, so we deliberately don't pattern-match it
        // here. The "just hung up" line is the player-name-keyed form we
        // can act on; some BBSes disable it but when it's on we use it.
        yield return new RegexPattern(KnownPatterns.PlayerHungUp,
            @"^(?<player>\w+) just hung up!!!\.?");
        yield return new RegexPattern(KnownPatterns.PlayerExits,
            @"^(?<player>\w+) just left the Realm\.");
        yield return new RegexPattern(KnownPatterns.PlayerEnters,
            @"^(?<player>\w+) just entered the Realm\.");
        // Another player materialising in our room by teleport — a recall
        // spell or a party-splitting chime/CMD teleport. Distinct from the
        // directional "walks in" arrival: teleport arrivals carry no
        // direction, so a party reform waiting on a member's arrival keys off
        // this line to time its re-invite (inviting before the member has
        // flashed in draws "You don't see X here!" and the invite is lost).
        yield return new RegexPattern(KnownPatterns.PlayerTeleportsIn,
            @"^(?<player>\w+) appears in a blinding flash of light!");

        // Room-occupant list — fires on every room render that includes
        // visible non-mob players. Single capture group holds the full
        // comma-separated list (with optional "and" Oxford-comma form);
        // the consumer (AutoPartyManager) splits the list itself so we
        // don't have to express alternation N-ways in the regex.
        // Examples observed: "Also here: Raijin." (single),
        // "Also here: Foo, Bar." (two), "Also here: Foo, Bar and Baz."
        // (three with Oxford-and).
        yield return new RegexPattern(KnownPatterns.RoomAlsoHere,
            @"^Also here: (?<players>.+?)\.\s*$");

        // Incoming party invite from another player. Real Playpen BBS
        // wording (verified live, 2026-06-01): "Fujin has invited you
        // to follow him."
        //
        // MajorMUD gender vocabulary — apply consistently when adding
        // future patterns that involve subject/object pronouns:
        //   * Player characters: male | female (him / her only).
        //   * Monsters: male | female | neuter (him / her / it).
        // Party invites are always player→player so the alternation
        // here is just him/her. Monster-flavour patterns (combat
        // misses, mob taunts, etc.) need the third arm.
        yield return new RegexPattern(KnownPatterns.PartyInviteReceived,
            @"^(?<player>\w+) has invited you to follow (?:him|her)\.?\s*$");

        // ----- Party ----------------------------------------------------
        // Real-BBS-verified patterns. Two distinct follow-direction signals:
        //   - "X started to follow you."     ⇒ X joined OUR party (we lead)
        //   - "You are now following X."     ⇒ WE joined X's party (X leads)
        // Stop-following alternation covers both observed wordings.
        yield return new RegexPattern(KnownPatterns.PartyFollowsYou,
            @"^(?<player>\w+) started to follow you\.");
        yield return new RegexPattern(KnownPatterns.PartyYouFollowing,
            @"^You are now following (?<player>\w+)\.?$");
        // Party-follow drag — the game moved us one room in the leader's wake and
        // prints " -- Following your Party leader <dir> --" just before the new
        // room display. Longest direction alternatives first so "northeast" can't
        // be shortened to "north". Captures the direction so FollowMoveObserver can
        // feed it to RoomTracker as the move a dragged follower never typed.
        yield return new RegexPattern(KnownPatterns.PartyFollowMove,
            @"^\s*--\s*Following your Party leader\s+(?<dir>northeast|northwest|southeast|southwest|north|south|east|west|up|down)\s*--\s*$");
        yield return new RegexPattern(KnownPatterns.PartyStopsFollowing,
            @"^(?<player>\w+) (?:stops following you|has stopped following you)\.?");
        // Outbound-invite confirmation — the server echoes this every
        // time we (or AutoPartyManager / RemoteCommandManager invite
        // handler) sends `invite X` on the wire. PartyManager adds an
        // IsInvited row for X on this line so the user sees the
        // pending invitee in PartyWindow before they accept.
        yield return new RegexPattern(KnownPatterns.PartyYouInvited,
            @"^You have invited (?<player>\w+) to follow you\.?$");
        // par-header — MajorMUD actually labels it "The following people
        // are in your travel party:" (not "Party Status:" which was my
        // earlier guess). Anchors PartyManager's stateful row parser.
        yield return new RegexPattern(KnownPatterns.PartyHeader,
            @"^The following people are in your travel party:");
        // Conservative member-death match — "X has been slain by Y" is
        // the clearest PvP kill line in MajorMUD's vocabulary, with the
        // victim's name as the load-bearing group. Generic "X has died"
        // lines aren't matched here because they can fire for non-party
        // mobs / NPCs in the same room and we don't want false-positive
        // evictions from PartyState.Members.
        yield return new RegexPattern(KnownPatterns.PartyMemberDeath,
            @"^(?<player>\w+) has been slain by ");
        // "<Name> has died." — the universal third-person death line an observer
        // sees when any player in the room dies (the counterpart to the
        // first-person "You have been slain by ..." we match as UserSlain). It is
        // NOT party-specific, so unlike PartyMemberDeath above it is deliberately
        // NOT wired to any roster-eviction path — matching it broadly would
        // false-evict on a same-named mob/NPC. Its sole consumer,
        // PartyDeathRosterCleanup, bounds every action to a name that is BOTH a
        // current party member AND shows as an [Invited] par slot, so a stray
        // "goblin has died." can never trigger a real uninvite. Wording is
        // user-reported and pending live re-confirmation.
        yield return new RegexPattern(KnownPatterns.PartyMemberDied,
            @"^(?<player>\w+) has died\.?\s*$");
        // "<Name> drops to the ground!" — a player hit 0 HP and is mortally
        // wounded (down, bleeding out, not yet dead). Room/party-side signal;
        // everyone present sees it, the dropper with their own name. Distinct
        // from the corpse-cash "N silver drop to the ground." (two leading tokens,
        // verb "drop", trailing period) so the two never collide. AllyDroppedHandler
        // scopes the reaction to a party / recently-partied ally.
        yield return new RegexPattern(KnownPatterns.PartyMemberDropped,
            @"^(?<player>\w+) drops to the ground!");
        // "You have aided <Name>, ..." — our first-aid landed on a downed ally;
        // they're back to positive HP. Group 1 captures the aided ally's name.
        yield return new RegexPattern(KnownPatterns.UserAidedAlly,
            @"^You have aided (?<player>\w+),");
        // "<Leader> is dragging you around." — the dragged (mortally-wounded)
        // character's per-move view of being hauled around by a party member's
        // `drag <name>`. Group 1 = the dragger's given name.
        yield return new RegexPattern(KnownPatterns.PartyDraggedAround,
            @"^(?<leader>\w+) is dragging you around\.?\s*$");

        // ----- Party dissolution (Playpen-verified, 2026-06-01) ---------
        // Three signals that should evict members / wipe the party.
        // Verified live by uninviting Raijin from Fujin's party — the
        // game emits the first two from the leader's side and the
        // third + "no longer following" from the follower's side.
        //
        //   "Raijin has been removed from your followers."
        //     ⇒ leader's view of an uninvite (or self-leave). Remove X.
        //   "You are no longer following Fujin."
        //     ⇒ follower's view of the leader uninviting us, OR our own
        //        `unfollow` command. Remove X from the roster.
        //   "You are not in a party at the present time."
        //     ⇒ authoritative dissolution — wipe the whole party.
        yield return new RegexPattern(KnownPatterns.PartyFollowerRemoved,
            @"^(?<player>\w+) has been removed from your followers\.?\s*$");
        yield return new RegexPattern(KnownPatterns.PartyYouNoLongerFollowing,
            @"^You are no longer following (?<player>\w+)\.?\s*$");
        yield return new RegexPattern(KnownPatterns.PartyDissolved,
            @"^You are not in a party at the present time\.?\s*$");

        // ----- Per-member rank changes (Playpen-verified, 2026-06-02) ---
        // When another party member reranks, the game prints one of three
        // phrasings depending on which rank they moved to. The "middle"
        // form drops the word "rank" ("...to the middle of your group");
        // the "front"/"back" forms keep it ("...to the front rank in your
        // group" / "...to the back rank in your group"). Capture the rank
        // word so PartyManager can update PartyMember.Rank live without
        // waiting for the next par poll.
        //
        // Player name is given/first only — matches PartyManager's
        // GivenNameOf roster matching.
        yield return new RegexPattern(KnownPatterns.PartyMemberRankChanged,
            @"^(?<player>\w+) just moved to the (?<rank>front|middle|back) (?:rank in|of) your group\.?\s*$");
        // Self's own rerank confirmation. No name to capture — applies to
        // the local character row. Phrasing is consistently "ranks of"
        // across all three (front/middle/back).
        yield return new RegexPattern(KnownPatterns.PartySelfRankChanged,
            @"^You have moved to the (?<rank>front|middle|back) ranks of your group\.?\s*$");
        // "X stops to rest." — third-person rest observation. Given-name only
        // (matches roster matching); the handler scopes it to party members.
        // Self's own action reads "You stop to rest." (different verb), so this
        // never matches our own row.
        yield return new RegexPattern(KnownPatterns.PartyMemberRestObserved,
            @"^(?<player>\w+) stops to rest\.?\s*$");

        // ----- Main menu (BBS-customisable but options are stable) -----
        // The "Enter the Realm" row is the universal signature — every
        // BBS keeps the [E] option on the main menu even when banners,
        // version strings and prompt text differ. The bracket-letter-
        // period-space-text format is unique to the main menu (in-game
        // status lines, room descriptions, chat etc. don't share it).
        yield return new RegexPattern(KnownPatterns.MainMenuEnterRealm,
            @"^\[E\]\s*\.\s*Enter the Realm\b");

        // Marker for the train-stats menu's "Point Cost Chart" panel
        // header. NOT anchored to line start/end — the panel sits in the
        // upper-right of the menu and shares its terminal row with the
        // left-side "MAJOR MUD Character Creation" box, so the
        // LineExtractor emits a single row containing BOTH titles plus
        // box-drawing chrome. Anchored matching missed entirely. The
        // outbound-`train stats` gate in TrainerMenuTracker is the real
        // defence against chat false positives — a chat line embedding
        // "Point Cost Chart" within 5 s of someone sending `train stats`
        // is essentially impossible in practice.
        yield return new RegexPattern(KnownPatterns.MenuTrainerStatsMarker,
            @"Point Cost Chart");

        // ----- Suicide password flow patterns -----------------------------
        // All anchored to the line start so a chat / gossip line embedding
        // the phrase can't trigger them. SuicidePasswordTracker layers
        // additional context on top — it only acts on these when it knows
        // we're actively in a flow (user just sent `set s*` or `suicide`).
        yield return new RegexPattern(KnownPatterns.SuicidePromptOldPassword,
            @"^Enter the current password:");
        yield return new RegexPattern(KnownPatterns.SuicidePromptNewPassword,
            @"^Enter New Password:");
        yield return new RegexPattern(KnownPatterns.SuicidePromptUseSuicide,
            @"^Enter your suicide password:");
        // Two observed variants of the rejection line on Playpen:
        //   "Invalid password specified."  — `suicide` use-form with wrong password
        //   "Invalid password!"            — `set suicide` with wrong CURRENT password
        // Match anything starting with "Invalid password" followed by a
        // non-word boundary so any future realm variant
        // ("Invalid password?" / "Invalid password — try again" / etc.)
        // still disarms the sniffer + unlocks the gate.
        yield return new RegexPattern(KnownPatterns.SuicideInvalidPassword,
            @"(?i)^Invalid password\b");
        yield return new RegexPattern(KnownPatterns.SuicideNotSet,
            @"^You do not have a suicide password set\.");
        // Playpen renders the success line as "Password changed"
        // (lowercase 'c'); previous regex required capital C and
        // silently failed to match, so the encrypted blob never landed
        // on the profile and the Settings → BBS suicide-password row
        // stayed hidden. Use the case-insensitive inline flag so any
        // realm variant ("Password CHANGED" / "Password Changed" /
        // "Password changed") commits the captured candidate.
        yield return new RegexPattern(KnownPatterns.SuicidePasswordChanged,
            @"(?i)^Password Changed\b");
        // Same tolerance for the negative form — the existing literal
        // happened to match Playpen's casing, but a future realm
        // tweak shouldn't break commit suppression silently.
        yield return new RegexPattern(KnownPatterns.SuicidePasswordNotChanged,
            @"(?i)^Password NOT changed\b");
        // Successful suicide → the character is rerolled (deleted, recreated
        // fresh at level 1). The realm's own suicide password dies with the
        // old character, so we wipe our stored copy; the Spell Book's
        // obtained set is cleared too (a fresh character has learned nothing).
        yield return new RegexPattern(KnownPatterns.Reroll,
            @"(?i)^After a LONG thought, you take your own life");
        // Learn-scroll signal — reading a spell scroll teaches its spell.
        // Group 1 is the spell's full Name ("harm"), NOT the short cast
        // code, so it resolves through SpellbookState.MarkObtainedByName.
        // Lazy capture so the terminating period isn't swallowed.
        yield return new RegexPattern(KnownPatterns.LearnSpell,
            @"(?i)^You read .+ and learn the spell (.+?)\.\s*$");

        // ----- Trap-disarm flow ------------------------------------------
        // Direction capture is the LONG form (north / northeast / up /
        // etc.) since that's what the game's first-person output uses.
        // TrapDisarmManager normalises both sides to short form ("n",
        // "ne", "u") for the matching key.
        yield return new RegexPattern(KnownPatterns.TrapFoundInSearch,
            @"^You found a trap to the (?<dir>\w+)!?\s*$");
        yield return new RegexPattern(KnownPatterns.TrapNoneInSearch,
            @"^You notice nothing different to the (?<dir>\w+)\.?\s*$");
        yield return new RegexPattern(KnownPatterns.TrapDisarmedSuccess,
            @"^You successfully disarmed the trap to the (?<dir>\w+)\.?\s*$");

        // ----- Door handling --------------------------------------------
        // Single-shot match — DoorOpenManager runs one request at a time,
        // so we don't need direction capture in the match. Both "door"
        // and "gate" nouns covered.
        yield return new RegexPattern(KnownPatterns.DoorBashSuccess,
            @"\bbashed the (?:door|gate) open\b");
        yield return new RegexPattern(KnownPatterns.DoorBashFailure,
            @"\battempts? to bash through fails?\b");
        yield return new RegexPattern(KnownPatterns.DoorPickSuccess,
            @"\bsuccessfully unlocks? the (?:door|gate)\b");
        yield return new RegexPattern(KnownPatterns.DoorPickFailure,
            @"\b(?:lockpicking )?skill fails you\b");
        yield return new RegexPattern(KnownPatterns.DoorPickNotLocked,
            @"\b(?:door|gate|exit|passage) (?:was|is) not locked\b");
        yield return new RegexPattern(KnownPatterns.DoorOpenedNow,
            @"\b(?:door|gate) is now open\b");
        yield return new RegexPattern(KnownPatterns.DoorAlreadyOpen,
            @"\b(?:door|gate) is already open\b");
        yield return new RegexPattern(KnownPatterns.DoorIsLocked,
            @"\b(?:door|gate) is locked\b");
        // "You successfully unlocked the door/gate" — after `use <key> <dir>`.
        // Distinct id from DoorPickSuccess so the FSM can branch on which
        // verb produced the unlock (pick goes to open; use-key also goes
        // to open, but the source state determines the next step).
        yield return new RegexPattern(KnownPatterns.DoorKeyUnlockSuccess,
            @"\bsuccessfully unlocked the (?:door|gate)\b",
            options: RegexOptions.IgnoreCase);
        // "You have no <item>" / "You don't have a key for that"
        // — generic missing-key reply. Coarse to cover both phrasings;
        // the manager only consults it during WaitingUseKey.
        yield return new RegexPattern(KnownPatterns.DoorKeyUnknown,
            @"\b(?:you have no |you don'?t have|nothing happens)\b",
            options: RegexOptions.IgnoreCase);

        // "You see <name> attempt to bash the door to the <dir>." — another
        // player (possibly our party leader) failing to force a door. Name
        // is the actor's given name; direction is the full word.
        yield return new RegexPattern(KnownPatterns.PlayerDoorBashAttempt,
            @"^You see (?<name>.+?) attempt to bash the door to the (?<dir>north|south|east|west|northeast|northwest|southeast|southwest|up|down)\.");

        // ----- Alignment -------------------------------------------------
        // "A dark cloud passes over you" — the local character's alignment
        // shifted toward evil (an evil-point gain). Prefix-anchored so a
        // quoted chat line carrying the phrase can't trigger it.
        // AlignmentTracker flags the Character Workshop's alignment stale
        // until the next `who` refresh.
        yield return new RegexPattern(KnownPatterns.AlignmentDarkCloud,
            @"^A dark cloud passes over you");

        // ----- Training --------------------------------------------------
        // "You hand over 1 gold crown and you receive training to attain
        // level 3." — `train` level-up confirmation; group 1 = attained level.
        // Auto-train uses this to detect the level-up + drive the CP plan.
        yield return new RegexPattern(KnownPatterns.TrainAttainLevel,
            @"^You hand over .+ and you receive training to attain level (?<level>\d+)");

        // Trainer rejections that stop the @train multi-level loop. The first
        // means we've out-levelled this trainer (more levels may remain, but not
        // here); the second means we can't afford the next level's fee.
        yield return new RegexPattern(KnownPatterns.TrainProgressedTooFar,
            @"^You have progressed too far to use the training provided here\.");
        yield return new RegexPattern(KnownPatterns.TrainNoMoney,
            @"^You do not have the money required for your training\.");
    }

}
