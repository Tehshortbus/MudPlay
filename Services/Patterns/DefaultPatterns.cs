namespace FujinTerm.Services.Patterns;

/// <summary>
/// Registers the baseline MajorMUD pattern set against a
/// <see cref="MessageRouter"/>. Categories follow Megamind's
/// <c>classifier.js</c> taxonomy and the regex strings are ported verbatim
/// from the upstream JS literals (with .NET-flavoured named-group syntax
/// where it differs).
/// </summary>
/// <remarks>
/// <para>
/// Source: megamind-mud/megamind-client (MIT) —
/// <c>src/main/routines/classifier.js</c>. Each pattern below carries a
/// <c>// source</c> reference per the CLAUDE.md attribution rule.
/// </para>
/// <para>
/// Multi-line "batch" patterns (<c>who-fantasy</c>, <c>who-technical</c>,
/// <c>player-status</c>) are not represented here — <see cref="IMessagePattern"/>
/// operates one line at a time. Their dedicated parsers land when the
/// consuming phase ships (Phase 5 for <c>who</c>, Phase 9 for
/// <c>player-status</c>).
/// </para>
/// <para>
/// One exception: Megamind's <c>user-emote</c> regex keys off ANSI bytes
/// (<c>[K[0;32m</c>) that the FujinTerm <see cref="LineExtractor"/>
/// consumes before the line surfaces. The emote pattern is omitted here;
/// emote detection needs attribute-aware matching (the row's foreground is
/// green / the cell's flags include the right SGR state), which is its own
/// follow-up.
/// </para>
/// </remarks>
public static class DefaultPatterns
{
    /// <summary>
    /// Populate <paramref name="router"/>'s known-patterns catalog. No
    /// handlers are attached — each subsystem (ChatRouter, combat tracker,
    /// etc.) registers its own handlers by id via
    /// <see cref="MessageRouter.Subscribe(string, Action{MatchResult})"/>.
    /// </summary>
    public static void Seed(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        foreach (IMessagePattern pattern in BuildDefaultPatterns())
        {
            router.RegisterPattern(pattern);
        }
    }

    /// <summary>
    /// Enumerate every default pattern instance. Exposed so tests can
    /// inspect the registry without having to wire a router.
    /// </summary>
    public static IEnumerable<IMessagePattern> BuildDefaultPatterns()
    {
        // ----- Stealth --------------------------------------------------- (source: classifier.js stealth)
        yield return new RegexPattern(KnownPatterns.UserSneaking,      @"^Sneaking\.\.\.");
        yield return new RegexPattern(KnownPatterns.UserNotSneaking,   @"^You make a sound as you enter the room!");
        yield return new RegexPattern(KnownPatterns.UserSneakFailed,   @"^Attempting to sneak\.\.\.You don't think you're sneaking\.");
        yield return new RegexPattern(KnownPatterns.UserSneakInitiate, @"^Attempting to sneak\.\.\.$");
        yield return new RegexPattern(KnownPatterns.UserCantSneak,     @"^You may not sneak right now!");

        // ----- Movement -------------------------------------------------- (source: classifier.js movement)
        // Megamind ships two regexes under direction-failed (no-exit + closed door/gate); combined via alternation.
        yield return new RegexPattern(KnownPatterns.DirectionFailed,
            @"^(?:There is no exit in that direction!|The (?:door|gate) is closed(?: in that direction)?!)");
        yield return new RegexPattern(KnownPatterns.BashFailed,
            @"^Your attempts to bash through fail!$");
        yield return new RegexPattern(KnownPatterns.HeardMovement,
            @"^You hear movement to the (?<direction>\w+)\.");

        // ----- Failures -------------------------------------------------- (source: classifier.js failures)
        yield return new RegexPattern(KnownPatterns.CommandNoEffect, @"^Your command had no effect\.$");
        yield return new RegexPattern(KnownPatterns.CommandIgnored,  @"^You are typing too quickly - command ignored");
        yield return new RegexPattern(KnownPatterns.SlowDown,        @"^Why don't you slow down for a few seconds\?");

        // ----- Searching ------------------------------------------------- (source: classifier.js searching)
        yield return new RegexPattern(KnownPatterns.UserSearchFailed,
            @"^You notice nothing different to the \w+");
        yield return new RegexPattern(KnownPatterns.UserSearchSucceeded,
            @"^You found an exit to the (?<direction>\w+)!");

        // ----- Combat ---------------------------------------------------- (source: classifier.js combat)
        yield return new RegexPattern(KnownPatterns.CombatStatus,
            @"^\*Combat (?<status>Engaged|Off)\*");
        yield return new RegexPattern(KnownPatterns.UserHits,
            @"^(?<source>[\w]+) (?:critically )?(?:\w+) (?<target>[\w- ]+) for (?<damage>\d+) damage!");
        // Trailing punctuation varies per realm — Megamind's literal had
        // \. but real output uses ".", "!", ",", and ";" depending on
        // whether the miss line continues with a dodge / parry / "but
        // misses!" follow-up. Use a word boundary after "you" so any
        // non-letter delimiter classifies.
        yield return new RegexPattern(KnownPatterns.MobMisses,
            @"^The (?<target>[\w -]+) \w+ at you\b");
        yield return new RegexPattern(KnownPatterns.MobHits,
            @"^The (?<target>[\w -]+) \w+ you for (?<damage>\d+) damage!");
        yield return new RegexPattern(KnownPatterns.UserGainExperience,
            @"^You gain (?<exp>\d+) experience\.");

        // ----- Conversation --------------------------------------------- (source: classifier.js conversation)
        // Auction lines share gossip's shape ("X auctions: ...") and the
        // user wants them filtered under the same Gossip toggle in the
        // Conversation window, so we classify both under one id via
        // alternation on the verb. Megamind's classifier does the same.
        yield return new RegexPattern(KnownPatterns.ConversationGossip,
            @"^(?<player>\w+) (?:gossips|auctions): (?<message>.+)");
        yield return new RegexPattern(KnownPatterns.ConversationBroadcast,
            @"^Broadcast from (?<player>\w+) ""(?<message>.+)""");
        yield return new RegexPattern(KnownPatterns.ConversationGangpath,
            @"^(?<player>\w+) gangpaths: (?<message>.+)");
        // Telepath: incoming + outgoing have different shapes — split into two ids.
        yield return new RegexPattern(KnownPatterns.ConversationTelepathIn,
            @"^(?<player>\w+) telepaths: (?<message>.+)");
        // The verb's capitalization varies between BBSes — Megamind's
        // literal is lowercase "sent" but some realms emit "Sent". Use
        // IgnoreCase so both spellings classify; we don't assume a realm.
        yield return new RegexPattern(KnownPatterns.ConversationTelepathOut,
            @"^--- Telepath sent to (?<player>\w+) ---$",
            options: System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Yell: combined own + others into one regex; player group empty for "You yell".
        yield return new RegexPattern(KnownPatterns.ConversationYell,
            @"^(?:(?<player>\w+) yells|You yell) ""(?<message>.+)""");
        yield return new RegexPattern(KnownPatterns.ConversationLocal,
            @"^(?<player>\w+) says? ""(?<message>.+)""");
        // user-emote (Megamind's regex keys off ANSI bytes the LineExtractor
        // strips). Omitted until attribute-aware matching ships — see remarks
        // on this class.

        // ----- Action / Items ------------------------------------------- (source: classifier.js action-items)
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
        yield return new RegexPattern(KnownPatterns.UserBuys,
            @"^You just bought (?:(?<qty>\d+) )?(?<item>[\w ]+) for (?<price>\d+) copper farthings\.$");

        // ----- Room ----------------------------------------------------- (source: classifier.js room)
        // Phase 7 room-parser consumer: before splitting `exits` on comma,
        // strip the [A-Z]\b. artifact Megamind's roomHandler.js handles —
        // BBSes embed direction-shortcut overstrike that survives the
        // emulator (megamind-client roomHandler.js updateRoomExits).
        yield return new RegexPattern(KnownPatterns.RoomExits,
            @"^Obvious exits: [\w, ]+");

        // ----- Status --------------------------------------------------- (source: classifier.js status)
        yield return new RegexPattern(KnownPatterns.StatusLine,
            @"^\[HP=(?<hp>\d{1,4})(?:\/(?<type>MA|KAI)=(?<mana>\d{1,3}))?(?:\s\((?<statea>Resting|Meditating)\)\s)?\]:(?:\s\((?<stateb>Resting|Meditating)\))?");
        yield return new RegexPattern(KnownPatterns.UserExperience,
            @"^Exp: (?<exp>\d+) Level: (?<level>\d+) Exp needed for next level: (?<need>\d+) \((?<req>\d+)\) \[(?<percent>\d+)%\]");
        yield return new RegexPattern(KnownPatterns.UserProfile,
            @"^(?:Recent Deaths:|Location:)");
        yield return new RegexPattern(KnownPatterns.UserEncumbrance,
            @"^Encumbrance:\s+\d+");

        // ----- Player presence ------------------------------------------ (source: classifier.js module)
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

        // ----- Party ---------------------------------------------------- (Phase 6 PR 6.1)
        // Real-BBS-verified patterns (Playpen BBS observation, Phase 6
        // post-PR-6.8). Two distinct follow-direction signals:
        //   - "X started to follow you."     ⇒ X joined OUR party (we lead)
        //   - "You are now following X."     ⇒ WE joined X's party (X leads)
        // Stop-following alternation covers both observed wordings.
        yield return new RegexPattern(KnownPatterns.PartyFollowsYou,
            @"^(?<player>\w+) started to follow you\.");
        yield return new RegexPattern(KnownPatterns.PartyYouFollowing,
            @"^You are now following (?<player>\w+)\.?$");
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
    }

}
