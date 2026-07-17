namespace FujinTerm.Models.Profile;

// Per-character "Other" settings — the misc bucket. Stored as the "Other" entry
// in CharacterProfile.Settings.
public sealed class OtherSettings
{
    // Block @do suicide / @party suicide when remaining lives are at or below
    // this threshold. Default 5 — protects players who haven't yet built up a
    // comfortable lives buffer. Setting to 0 allows forced suicide through all
    // lives. Max lives in MajorMUD is 9, so the UI clamps this to 0..9. Pushed
    // into Game.Remote.RemoteCommandManager.MaxSuicideLivesThreshold.
    //
    // The engine still hard-blocks suicide commands when the live lives count is
    // unknown (no LivesProvider bound) — the conservative default. This setting
    // only takes effect once that lives source is connected.
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // Note: the ailment-handling toggles (the four "Ignore X" @wait gates
    // and the four "do not announce" say-suppression gates) graduated to
    // SpellsSettings — they sit on the Spells tab next to the cure-spell
    // picks they coordinate with. AilmentSyncEngine reads them from there.

    // Master gate for walker trap-disarming. When true (default) the walker
    // routes a trapped exit through the disarm machinery before stepping through
    // — but only when a disarm is actually possible (the local character has the
    // Traps skill, or — once the party-delegation path lands — a party member
    // does). When false the walker walks straight through trapped exits with no
    // disarm attempt. Labeled "Utilize disarm traps if able" in Settings → Other
    // because the "if able" capability check rides on top of this on/off switch.
    public bool UtilizeDisarmTrapsIfAble { get; set; } = true;

    // Caps the search loop in the @trap handler — how many sea <dir> attempts
    // we'll make before giving up and telepathing the sender that we couldn't
    // find a trap. Default 20, range 1..100. Surfaced above the disarm-attempts
    // row in Settings → Other.
    public int MaxTrapSearchAttempts { get; set; } = 20;

    // Caps the disarm-retry loop in the @trap handler — how many disarm trap
    // <dir> attempts we'll make after the trap has been spotted before giving
    // up. Default 5, range 1..50. Damage-aware abort (stop early if the trap
    // fires and we lose HP) ships with the HealthManager wiring.
    public int MaxTrapDisarmAttempts { get; set; } = 5;

    // When true, the auto-discard engine conceals each excess flagged item with
    // hide <item> instead of drop <item> — it still leaves the pack, but lands
    // out of sight on the ground. Engine hides are excluded from the Transaction
    // history ledger (a discard isn't a stash); manual and stash-room hides still
    // record there. Default false (plain drop). Char-tier; surfaced in
    // Settings → Other. Read live by Game.Inventory.AutoDiscardManager.HideMode.
    public bool HideWhenDiscarding { get; set; }

    // ----- Door / lock handling --------------------------------------

    // Walker's max bash <dir> retries before giving up on a single door. Hits
    // when the player's strength is below the door's requirement and the server
    // keeps replying with "attempts to bash through fail". Default 10 per user
    // direction.
    public int MaxBashAttempts { get; set; } = 10;

    // Walker's max pick <dir> retries before giving up on a single door. Picking
    // is probabilistic — the skill can fail even when the value meets the door
    // requirement. Default 10 per user direction.
    public int MaxPickAttempts { get; set; } = 10;

    // When true, the walker prefers pick <dir> over bash <dir> on doors where
    // both verbs are viable. Bash is louder and breaks stealth; thieves
    // typically flip this on. Default false (bash-first).
    public bool PicklocksOverBash { get; set; }

    // Walker max sea <dir> retries when revealing a hidden exit ((Hidden)
    // modifier) along the path. Default 20 — mirrors the trap-search cap since
    // it's the same verb, kept separate so the user can tune them independently.
    public int MaxHiddenSearchAttempts { get; set; } = 20;

    // When true, arm auto-search on demand: while the walker is travelling a
    // route that crosses an (Item: N) / (Ticket: N) exit whose item the
    // character isn't carrying (e.g. a boat for the Silver River, a
    // rope-and-grapple for a climb), Game.Map.AutoSearchManager issues a bare
    // sea on every room entry to hunt the missing item until it's found — even
    // when the persisted Auto-Search master toggle is off. Read live by
    // Game.Map.PathItemDemandTracker through the resolver. Default false
    // (opt-in). Char-tier; surfaced in Settings → Other.
    public bool SearchRoomsIfItemNeeded { get; set; }

    // When true and leading a party, route around (Level: MIN to MAX) gates the
    // whole party can't clear rather than walking the leader through and leaving
    // a member behind. BFS treats such a gate as non-traversable via
    // Services.MovementFilter.PartyLevelBoundsProvider, which
    // Game.Remote.PartyLevelTracker feeds from each member's exact level
    // (learned by an @level probe) or, until probed, their title-derived level
    // band. When the only safe way needs a gate (all routes level-blocked), the
    // walker's existing gates-ignored re-probe still tells "level-gated" apart
    // from "disconnected" so the destination stays reachable. Read live by the
    // filter through the tracker. Default false (opt-in). Char-tier; surfaced in
    // Settings → Other.
    public bool AvoidPartyImpassableLevelGates { get; set; }

    // When true, Game.HopTimingCalibrator logs one Info line per observed hop
    // with the wall-clock time + the current Game.EncumbranceLevel. Used to
    // calibrate the Settings → Auto-Lair tab's per-encumbrance seconds-per-hop
    // defaults against in-game truth. Off by default — it's a developer /
    // data-collection knob, not a normal-play affordance.
    public bool LogMovementHopTiming { get; set; }

    // Leader-side @comeback backtrack budget — when a stranded follower sends a
    // bare @comeback (no target room), the leader pauses its active movement
    // engine and walks backwards along the path just taken, room by room, up to
    // this many rooms looking for the follower. If not recovered within the
    // budget the leader gives up and goes idle to let the player handle it.
    // Default 10, range 1..50. Ignored when the follower supplies an explicit
    // room (@comeback 9/1012) — that path walks straight to the named room
    // instead. Surfaced in Settings → Other.
    public int MaxComebackBacktrackRooms { get; set; } = 10;

    // Follower-side auto-@comeback. When true (default) and a movement-blocking
    // condition (prevents-movement gamedata flag or over-encumbrance) leaves us
    // behind as the party leader walks off, we automatically telepath @comeback
    // <room> to the leader so their party-recovery walk picks us up. When false,
    // the left-behind is still detected but no request is sent — the player
    // handles it manually. Defaults on: the request is a single telepath that
    // moves nothing on our side, so being stranded silently is strictly the
    // worse outcome. Char-tier setting; surfaced in Settings → Other.
    public bool AutoRequestComebackWhenLeftBehind { get; set; } = true;

    // Note: the former per-character verbose toggles (VerboseCombat /
    // VerboseRoomClassifier / VerboseCasting / VerboseCash / VerboseStealth) +
    // WriteCombatRoundTrace lived here briefly. They moved to the Log pane menu
    // as a single "Combat diagnostics" umbrella switch (session-only, not
    // persisted) — see Services/LogDiagnosticState.cs. Verbose tracing is a
    // "while I'm debugging right now" affordance, not a per-character
    // preference, and keeping it off the profile saves it from leaking on
    // between sessions.

    // Note: the run-away (flee) knobs (RunDirection / BreakBeforeFleeing)
    // graduated to CombatSettings — they sit on the Combat tab next to the
    // room thresholds + RunDistance they coordinate with. HealthManager's
    // flee path reads them from there.

    // Note: the party-bless gates (BlessWhileResting / BlessDuringCombat)
    // graduated to PartySettings — they sit on the Party tab next to the
    // bless slots they gate. CastingDirector reads them from there.
}
