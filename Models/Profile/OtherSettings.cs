namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Other" settings — the misc bucket. Stored as the
/// <c>"Other"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Phase 6 wires one field — the suicide-lives threshold. The rest of
/// the Other tab (lock / trap / hangup / ignore-ailment / auto-engage
/// toggles) ships its consumers in Phases 7 / 11 / 13 and adds fields
/// here as those engines land. The tab still renders the full stub
/// catalog underneath the wired group so the user sees the surface
/// from day one.
/// </remarks>
public sealed class OtherSettings
{
    /// <summary>
    /// Block <c>@do suicide</c> / <c>@party suicide</c> when remaining
    /// lives are at or below this threshold. Default 5 — protects players
    /// who haven't yet built up a comfortable lives buffer. Setting to
    /// <c>0</c> allows forced suicide through all lives. Max lives in
    /// MajorMUD is 9, so the UI clamps this to 0..9. Pushed into
    /// <see cref="Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold"/>.
    /// </summary>
    /// <remarks>
    /// The engine still hard-blocks suicide commands when the live
    /// lives count is unknown (no <c>LivesProvider</c> bound) — the
    /// conservative default until the Phase 9 DEATH section wires
    /// up live-lives tracking. This setting only takes effect once
    /// that lives source is connected.
    /// </remarks>
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // Note: the ailment-handling toggles (the four "Ignore X" @wait gates
    // and the four "do not announce" say-suppression gates) graduated to
    // SpellsSettings — they sit on the Spells tab next to the cure-spell
    // picks they coordinate with. AilmentSyncEngine reads them from there.

    /// <summary>
    /// Master gate for walker trap-disarming. When <c>true</c> (default)
    /// the walker routes a trapped exit through the disarm machinery
    /// before stepping through — but only when a disarm is actually
    /// possible (the local character has the Traps skill, or — once the
    /// party-delegation path lands — a party member does). When
    /// <c>false</c> the walker walks straight through trapped exits with
    /// no disarm attempt. Labeled "Utilize disarm traps if able" in
    /// Settings → Other because the "if able" capability check rides on
    /// top of this on/off switch.
    /// </summary>
    public bool UtilizeDisarmTrapsIfAble { get; set; } = true;

    /// <summary>
    /// Caps the search loop in the @trap handler — how many
    /// <c>sea &lt;dir&gt;</c> attempts we'll make before giving up and
    /// telepathing the sender that we couldn't find a trap. Default
    /// 20, range 1..100. Surfaced above the disarm-attempts row in
    /// Settings → Other.
    /// </summary>
    public int MaxTrapSearchAttempts { get; set; } = 20;

    /// <summary>
    /// Caps the disarm-retry loop in the @trap handler — how many
    /// <c>disarm trap &lt;dir&gt;</c> attempts we'll make after the
    /// trap has been spotted before giving up. Default 5, range
    /// 1..50. Damage-aware abort (stop early if the trap fires and
    /// we lose HP) ships with the Phase 13 HealthManager wiring.
    /// </summary>
    public int MaxTrapDisarmAttempts { get; set; } = 5;

    // ----- Door / lock handling --------------------------------------

    /// <summary>
    /// Walker's max <c>bash &lt;dir&gt;</c> retries before giving up
    /// on a single door. Hits when the player's strength is below
    /// the door's requirement and the server keeps replying with
    /// <c>"attempts to bash through fail"</c>. Default 10 per user
    /// direction.
    /// </summary>
    public int MaxBashAttempts { get; set; } = 10;

    /// <summary>
    /// Walker's max <c>pick &lt;dir&gt;</c> retries before giving up
    /// on a single door. Picking is probabilistic — the skill can
    /// fail even when the value meets the door requirement. Default
    /// 10 per user direction.
    /// </summary>
    public int MaxPickAttempts { get; set; } = 10;

    /// <summary>
    /// When <c>true</c>, the walker prefers <c>pick &lt;dir&gt;</c>
    /// over <c>bash &lt;dir&gt;</c> on doors where both verbs are
    /// viable. Bash is louder and breaks stealth; thieves typically
    /// flip this on. Default <c>false</c> (bash-first).
    /// </summary>
    public bool PicklocksOverBash { get; set; }

    /// <summary>
    /// Walker max <c>sea &lt;dir&gt;</c> retries when revealing a
    /// hidden exit (<c>(Hidden)</c> modifier) along the path. Default
    /// 20 — mirrors the trap-search cap since it's the same verb,
    /// kept separate so the user can tune them independently.
    /// </summary>
    public int MaxHiddenSearchAttempts { get; set; } = 20;

    /// <summary>
    /// When <c>true</c>, arm auto-search on demand: while the walker is
    /// travelling a route that crosses an <c>(Item: N)</c> / <c>(Ticket: N)</c>
    /// exit whose item the character isn't carrying (e.g. a boat for the
    /// Silver River, a rope-and-grapple for a climb),
    /// <see cref="Game.Map.AutoSearchManager"/> issues a bare <c>sea</c> on
    /// every room entry to hunt the missing item until it's found — even
    /// when the persisted Auto-Search master toggle is off. Read live by
    /// <see cref="Game.Map.PathItemDemandTracker"/> through the resolver.
    /// Default <c>false</c> (opt-in). Char-tier; surfaced in Settings → Other.
    /// </summary>
    public bool SearchRoomsIfItemNeeded { get; set; }

    /// <summary>
    /// When <c>true</c>, actively source a missing route item from a shop:
    /// on a one-shot walk-to that crosses an <c>(Item: N)</c> /
    /// <c>(Ticket: N)</c> exit whose item we're not carrying, if any shop in
    /// the active set stocks that item,
    /// <see cref="Game.Map.PathItemShopRouter"/> detours to the shop adding
    /// the fewest steps (<c>dist(cur,shop)+dist(shop,dest)</c>), issues
    /// <c>buy &lt;item&gt;</c>, then resumes to the original destination. If
    /// the item turns up first (e.g. via demand-driven search) the detour is
    /// abandoned; a failed buy or unreachable shop falls back to search.
    /// Only plain walk-to's detour — loop / auto-lair runs don't. Independent
    /// of the Auto-Search master toggle. Read live by the router through the
    /// resolver. Default <c>false</c> (opt-in). Char-tier; surfaced in
    /// Settings → Other.
    /// </summary>
    public bool BuyNeededPathItems { get; set; }

    /// <summary>
    /// When <c>true</c>, source a missing route item no shop sells by
    /// hunting for it: on a one-shot walk-to that crosses an <c>(Item: N)</c>
    /// / <c>(Ticket: N)</c> exit whose item we're not carrying and which no
    /// shop stocks, if some monster drops it,
    /// <see cref="Game.Map.MonsterDropRouter"/> prompts (via
    /// <see cref="Services.ConfirmService"/>) to reroute to the nearest room
    /// that monster spawns in; on confirmation it walks there, waits for the
    /// drop, then resumes to the original destination. Declining leaves the
    /// need to demand-driven search. Complements
    /// <see cref="BuyNeededPathItems"/> (which handles shop-sold items) —
    /// this covers only what no shop sells. Only plain walk-to's reroute —
    /// loop / auto-lair runs don't. Read live by the router through the
    /// resolver. Default <c>false</c> (opt-in). Char-tier; surfaced in
    /// Settings → Other.
    /// </summary>
    public bool HuntNeededPathItems { get; set; }

    /// <summary>
    /// When <c>true</c>, ask the party for a missing route item before
    /// searching / buying / hunting for it: on a walk-to that crosses an
    /// <c>(Item: N)</c> / <c>(Ticket: N)</c> exit whose per-member item we're
    /// not carrying, <see cref="Game.Map.PartyPathItemGate"/> broadcasts
    /// <c>@have</c> to the party and — if a member holds a spare — has them
    /// <c>give</c> it over instead of posting a search / shop / hunt need. Only
    /// a genuine shortfall (no member has a spare) falls through to the demand
    /// pipeline. No-op when solo. Complements the search / buy / hunt sources
    /// (it runs ahead of them). Read live by the gate through the resolver.
    /// Default <c>false</c> (opt-in). Char-tier; surfaced in Settings → Other.
    /// </summary>
    public bool DeferToPartyInventory { get; set; }

    /// <summary>
    /// When <c>true</c>, <see cref="Game.HopTimingCalibrator"/> logs
    /// one Info line per observed hop with the wall-clock time + the
    /// current <see cref="Game.EncumbranceLevel"/>. Used to calibrate
    /// the Settings → Auto-Lair tab's per-encumbrance seconds-per-hop
    /// defaults against in-game truth. Off by default — it's a
    /// developer / data-collection knob, not a normal-play affordance.
    /// </summary>
    public bool LogMovementHopTiming { get; set; }

    /// <summary>
    /// Leader-side <c>@comeback</c> backtrack budget — when a stranded
    /// follower sends a bare <c>@comeback</c> (no target room), the
    /// leader pauses its active movement engine and walks backwards
    /// along the path just taken, room by room, up to this many rooms
    /// looking for the follower. If not recovered within the budget the
    /// leader gives up and goes idle to let the player handle it.
    /// Default 10, range 1..50. Ignored when the follower supplies an
    /// explicit room (<c>@comeback 9/1012</c>) — that path walks
    /// straight to the named room instead. Surfaced in Settings → Other.
    /// </summary>
    public int MaxComebackBacktrackRooms { get; set; } = 10;

    /// <summary>
    /// Follower-side auto-<c>@comeback</c>. When <c>true</c> (default) and
    /// a movement-blocking condition (prevents-movement gamedata flag or
    /// over-encumbrance) leaves us behind as the party leader walks off,
    /// we automatically telepath <c>@comeback &lt;room&gt;</c> to the
    /// leader so their party-recovery walk picks us up. When <c>false</c>,
    /// the left-behind is still detected but no request is sent — the
    /// player handles it manually. Defaults on: the request is a single
    /// telepath that moves nothing on our side, so being stranded silently
    /// is strictly the worse outcome. Char-tier setting; surfaced in
    /// Settings → Other.
    /// </summary>
    public bool AutoRequestComebackWhenLeftBehind { get; set; } = true;

    // Note: the former Phase 9 per-character verbose toggles
    // (VerboseCombat / VerboseRoomClassifier / VerboseCasting /
    // VerboseCash / VerboseStealth) + WriteCombatRoundTrace lived
    // here briefly. They moved to the Log pane menu as a single
    // "Combat diagnostics" umbrella switch (session-only, not
    // persisted) — see Services/LogDiagnosticState.cs. Verbose tracing
    // is a "while I'm debugging right now" affordance, not a per-
    // character preference, and keeping it off the profile saves it
    // from leaking on between sessions.

    // Note: the run-away (flee) knobs (RunDirection / BreakBeforeFleeing)
    // graduated to CombatSettings — they sit on the Combat tab next to the
    // room thresholds + RunDistance they coordinate with. HealthManager's
    // flee path reads them from there.

    // Note: the party-bless gates (BlessWhileResting / BlessDuringCombat)
    // graduated to PartySettings — they sit on the Party tab next to the
    // bless slots they gate. CastingDirector reads them from there.
}
