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
        // to follow him." Gender-pronoun varies (him / her / them);
        // alternate covers all three. The earlier "invites you to join
        // his party" form was a guess at the wording — left out so the
        // pattern only matches what the game actually emits, otherwise
        // unrelated lines could false-positive.
        yield return new RegexPattern(KnownPatterns.PartyInviteReceived,
            @"^(?<player>\w+) has invited you to follow (?:him|her|them)\.?\s*$");

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
    }

}
