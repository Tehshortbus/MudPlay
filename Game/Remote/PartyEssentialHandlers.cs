using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The party-essential @-commands:
//   - Query-tier — @version, @health, @status, @lives, @where. Each replies via
//     the channel the command arrived on with a short response derived from local
//     state (PlayerState, PartyState, PlayerStats).
//   - Party passthrough — @party <command>. A party-membership-gated relay of
//     the leader's directive to the follower's wire (the party analogue of @do,
//     but authorised by active party membership rather than the per-player
//     ExecuteCommands grant). Any command is sent verbatim (in plain ASCII, WireText) so
//     the whole party moves in lock-step; `meditate`→`medi` and `go <dir>`→bare-direction are the
//     only token rewrites.
//   - Receive-only signalling — @wait / @ok. Recorded in WaitingMembers, which
//     the pause-gate reads to decide whether to hold automation. @waiting is the
//     leader's nudge back: it's holding for our @ok.
//
// Lifetime: registered once at AppServices construction after the engine ships.
// Disposal unregisters every command so a rebuilt AppServices doesn't leak
// handler entries.
public sealed class PartyEssentialHandlers : IDisposable
{
    // Commands this consumer registers, so Dispose can unregister them all.
    private static readonly string[] RegisteredCommands =
    {
        "@version", "@health", "@status", "@where", "@who", "@path",
        "@party", "@wait", "@ok", "@waiting",
        "@lives", "@invite", "@join",
    };

    private readonly RemoteCommandManager _engine;
    private readonly PlayerState _player;
    private readonly PartyState _party;
    private readonly Func<PartySettings>? _readPartySettings;
    private readonly Func<Room?>? _readCurrentRoom;
    private readonly Func<IReadOnlyList<RoomEntity>?>? _readRoomEntities;
    private readonly Func<MovementStatus>? _readMovement;
    private readonly Func<string?>? _readDraggedBy;
    private readonly Func<MessageFlags>? _readAilments;
    private readonly Func<bool>? _readFleeing;
    private readonly Func<bool>? _readHoldingWait;
    private Action<byte[]>? _wireSender;

    // Paradigm-only authoritative position re-fix seam. Bound by AppServices to
    // ParadigmPositionResolver.RequestResyncOnce: (reason, onResolved, onFailed)
    // fires `rm` and calls back once the game answers, returning false when a
    // re-fix can't be started (stock realm / no wire / throttled). Lets @where
    // ask the game for our true position instead of replying "Location unknown"
    // when the heuristic tracker is lost but the game can tell us exactly where
    // we are. Null on stock realms.
    private Func<string, Action<RoomKey>, Action, bool>? _requestPositionRefix;

    private bool _disposed;

    // The curable ailments @status reports, paired with the plain word the reply
    // spells them as. Mirrors the say-toggle token table (PartyAilmentTracker.Tokens)
    // minus the '@' — the asking client's PartyAilmentTracker parses these exact
    // words to reconcile the responder's chips. Held is excluded: its chip clear is
    // tied to the @ok / leader-pause lifecycle, so a @status reconcile must not
    // touch it.
    private static readonly (MessageFlags Flag, string Word)[] StatusAilments =
    {
        (MessageFlags.Poisoned, "poisoned"),
        (MessageFlags.Blinded,  "blind"),
        (MessageFlags.Confused, "confused"),
        (MessageFlags.Diseased, "diseased"),
    };

    // Player names currently asking the party to @wait. Removed when the same
    // player sends @ok. The pause-gate reads this set to decide whether the
    // auto-walker / combat engine should hold off. Case-insensitive.
    public HashSet<string> WaitingMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Pause-gate read consumed by automation engines (auto-walk, auto-combat,
    // etc.) — true whenever at least one party member has asked us to @wait and
    // hasn't yet sent @ok. Cheap to poll; engines either check before each tick or
    // subscribe to PauseGateChanged for edge-triggered notification.
    public bool IsPaused => WaitingMembers.Count > 0;

    // Members whose @ok we won't take this wait — their last one came just before
    // our move left them behind, so it didn't mean they could move. Only the wait
    // window releases them. Cleared with the wait.
    private readonly HashSet<string> _okDistrusted = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> OkDistrusted => _okDistrusted;

    // When each member last sent @ok (given name), to spot a premature one.
    private readonly Dictionary<string, DateTime> _lastOkAt = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTime> _now;

    // Whether member sent @ok within window of now.
    public bool OkedWithin(string member, TimeSpan window)
        => _lastOkAt.TryGetValue(GivenName(member), out DateTime at) && _now() - at <= window;

    // Fires on every transition of IsPaused. Lets the pause-gate consumer drop a
    // single subscription instead of polling.
    public event Action<bool>? PauseGateChanged;

    public PartyEssentialHandlers(
        RemoteCommandManager engine,
        PlayerState player,
        PartyState party,
        Func<PartySettings>? readPartySettings = null,
        Func<Room?>? readCurrentRoom = null,
        Func<IReadOnlyList<RoomEntity>?>? readRoomEntities = null,
        Func<MovementStatus>? readMovement = null,
        Func<string?>? readDraggedBy = null,
        Func<MessageFlags>? readAilments = null,
        Func<bool>? readFleeing = null,
        Func<bool>? readHoldingWait = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(party);
        _engine = engine;
        _player = player;
        _party  = party;
        _readPartySettings = readPartySettings;
        _readCurrentRoom = readCurrentRoom;
        _readRoomEntities = readRoomEntities;
        _readMovement = readMovement;
        _readDraggedBy = readDraggedBy;
        _readAilments = readAilments;
        _readFleeing = readFleeing;
        _readHoldingWait = readHoldingWait;

        // Categories sourced from RemoteCommandCatalog — single source of truth
        // for every documented @-command's required permission category.
        // Hardcoding the category per RegisterHandler call led to drift; routing
        // through the catalog keeps every handler consistent with the
        // per-category permission checkboxes in the player edit dialog.
        Register("@version", OnVersion);
        Register("@health",  OnHealth);
        Register("@status",  OnStatus);
        Register("@where",   OnWhere);
        Register("@who",     OnWho);
        Register("@path",    OnPath);
        Register("@party",   OnParty);
        Register("@wait",    OnWait);
        Register("@ok",      OnOk);
        Register("@waiting", OnWaiting);
        Register("@lives",   OnLives);
        Register("@invite",  OnInvite);
        Register("@join",    OnJoin);
    }

    // Bind the wire-sender. Required for OnParty to forward the party-leader's
    // directive as a local command. Same signature shape as
    // MacroDispatcher.SetSender; the main-window VM provides SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the Paradigm position re-fix seam (ParadigmPositionResolver.
    // RequestResyncOnce). AppServices calls this once the resolver exists.
    // Without it, @where degrades to "Location unknown" when the room is
    // unknown — the stock-realm behaviour.
    public void SetPositionRefix(Func<string, Action<RoomKey>, Action, bool> requestRefix)
    {
        ArgumentNullException.ThrowIfNull(requestRefix);
        _requestPositionRefix = requestRefix;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    // Wrapper around RemoteCommandManager.RegisterHandler that pulls the required
    // category from RemoteCommandCatalog. Throws if the command isn't in the
    // catalog — these handlers are catalog-backed by definition, so a missing
    // entry means the catalog needs updating, not the handler.
    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // ----- Query handlers -------------------------------------------------

    private void OnVersion(RemoteCommandContext ctx) =>
        // Matches the format other clients use for the same query (e.g. MegaMUD
        // replies "{MegaMud 1.03u}"): "<name> <version>" bracketed by the engine's
        // SendReply at wire time.
        ctx.Reply(AppInfo.DisplayNameWithVersion);

    private void OnHealth(RemoteCommandContext ctx)
    {
        if (!_player.HasPromptData) { ctx.Refuse("HP unknown - no prompt observed yet"); return; }
        // Format mirrors the in-game prompt vocabulary so the recipient
        // can read it at a glance: HP=cur/max,MA=cur/max[, Resting|Meditating].
        // The engine wraps the whole payload in { } at SendReply time;
        // we provide the bare body.
        //
        // - HP is always present.
        // - Mana segment uses MA / KAI per ManaType; omitted when None.
        // - Position suffix only appears for non-idle stances. Standing
        //   is the default "doing nothing notable" and adds no signal
        //   for the recipient — Resting and Meditating do.
        string mana = _player.ManaType switch
        {
            ManaType.Mana => $",MA={_player.Ma}/{_player.MaxMa}",
            ManaType.Kai  => $",KAI={_player.Ma}/{_player.MaxMa}",
            _             => string.Empty,
        };
        string position = _player.Position switch
        {
            PlayerPosition.Resting    => ", Resting",
            PlayerPosition.Meditating => ", Meditating",
            _                         => string.Empty,
        };
        ctx.Reply($"HP={_player.Hp}/{_player.MaxHp}{mana}{position}");
    }

    // @status — reply with semicolon-separated clauses the party can act on:
    //   1. activity — the running movement engine (walking / looping / auto-lair)
    //      or "idle", plus the most urgent condition sub-state when one applies
    //      (fleeing > fighting > resting/meditating).
    //   2. where — the current room name + (map, room), or "location unknown".
    //   3. ETA — only while en route: a boat-leg countdown, else the land walk's
    //      remaining step count. Absent when not walking anywhere.
    //   4. ailments — "no ailments" or "ailments: blind, poisoned".
    // The ailment clause doubles as a pull-based resync: the asking client's
    // PartyAilmentTracker reconciles our chips against it, so a chip missed by the
    // push-based .@X on/off say still clears the next time someone asks @status.
    // The engine wraps the payload in { } at SendReply time.
    private void OnStatus(RemoteCommandContext ctx)
    {
        if (!_player.HasPromptData) { ctx.Refuse("Status unknown"); return; }
        MovementStatus mv = _readMovement?.Invoke() ?? default;
        string eta = DescribeEta(mv);
        string etaClause = eta.Length > 0 ? $"; {eta}" : string.Empty;
        ctx.Reply($"{DescribeState(mv)}; {DescribeRoom()}{etaClause}; {DescribeAilments()}");
    }

    // The activity phrase: the running movement engine (or "idle") plus the most
    // urgent condition sub-state that adds signal. Fleeing outranks fighting, which
    // outranks a held rest stance; only the single most urgent condition is shown.
    // Movement + a condition co-occur only when combat has halted an in-flight walk,
    // so both are listed in that case ("running loop 'X', fighting").
    private string DescribeState(MovementStatus mv)
    {
        List<string> parts = new();

        string move = MovementEnginePhrase(mv);
        if (move.Length > 0) parts.Add(move);

        // A paused engine already spells out WHY in the move phrase ("resting (low HP)
        // en route to X", "held on loop 'Y'", …), so appending the raw condition here
        // would just echo it. Only add the condition sub-state when we're not reporting
        // a pause reason.
        if (!mv.Paused)
        {
            string? condition =
                _readFleeing?.Invoke() == true ? "fleeing"
                : _player.InCombat            ? "fighting"
                : _player.Position switch
                {
                    PlayerPosition.Resting    => "resting",
                    PlayerPosition.Meditating => "meditating",
                    _                         => null,
                };
            if (condition is not null) parts.Add(condition);
        }

        return parts.Count == 0 ? "idle" : string.Join(", ", parts);
    }

    // The current-room clause shared with @path: room name + (map, room), or a
    // plain "location unknown" when the tracker has no fix.
    private string DescribeRoom()
    {
        Room? room = _readCurrentRoom?.Invoke();
        return room is null ? "location unknown" : FormatRoom(room);
    }

    // ETA clause, present only when a walk is actually under way. A boat leg has a
    // real wall-clock arrival, so we count down to it (the state clause already reads
    // "sailing to <port>", so the tag isn't repeated here). A land walk has no
    // per-step cadence to time — the walker sends each step reactively on arrival —
    // so we report the honest distance measure instead: steps left on the path.
    private static string DescribeEta(MovementStatus mv)
    {
        if (mv.Sailing)
        {
            int secs = Math.Max(0, (int)Math.Round((mv.SailingEta - DateTimeOffset.UtcNow).TotalSeconds));
            return $"ETA ~{secs}s";
        }
        if (mv.Kind == MovementKind.Walking && mv.TotalSteps > 0)
        {
            int left = Math.Max(0, mv.TotalSteps - mv.CurrentStep);
            return left == 1 ? "1 step left" : $"{left} steps left";
        }
        return string.Empty;
    }

    // The curable ailments currently active on us, as the @status ailment clause.
    // Absent = "no ailments"; otherwise "ailments: <word>, <word>" in table order.
    private string DescribeAilments()
    {
        MessageFlags flags = _readAilments?.Invoke() ?? MessageFlags.None;
        List<string> words = new();
        foreach ((MessageFlags flag, string word) in StatusAilments)
            if (flags.HasFlag(flag)) words.Add(word);
        return words.Count == 0 ? "no ailments" : $"ailments: {string.Join(", ", words)}";
    }

    // The movement-engine token shared by @status and @path: a short phrase for the
    // topmost running engine, or empty when nothing is moving us. @path early-returns
    // "not moving" for None before calling this; @status folds the empty string into
    // its "idle" fallback. A boat leg overrides the engine token — while aboard we're
    // "sailing to <port>" whichever engine steered us onto the ship.
    private static string MovementEnginePhrase(MovementStatus mv)
    {
        if (mv.Sailing)
            return mv.SailingPlace is { Length: > 0 } place ? $"sailing to {place}" : "sailing";
        // A paused walker (rest / hold / wait / manual pause) reads as its SPECIFIC
        // reason, not a bare "walking" — a walk that isn't actually stepping shouldn't
        // report as moving, and "resting (low HP)" vs "held" vs a manual "paused" each
        // means something different to the party member asking. PauseReason carries the
        // resolved word; fall back to "paused" when it couldn't be resolved.
        string reason = mv.PauseReason is { Length: > 0 } r ? r : "paused";
        return mv.Kind switch
        {
            MovementKind.Loop    => mv.Paused ? $"{reason} on loop '{mv.Label}'" : $"running loop '{mv.Label}'",
            MovementKind.Lair    => mv.Paused ? $"{reason} (auto-lair)" : "auto-lair",
            MovementKind.Walking => mv.Paused ? $"{reason} en route to {mv.Label}" : $"walking to {mv.Label}",
            _                    => string.Empty,
        };
    }

    // The idle @path fallback: name the last loop / auto-lair the player ran (so a
    // party member can help them resume), or a plain "not moving" when nothing has
    // run this session. An unnamed ad-hoc auto-lair reports the bare engine.
    private static string DescribeLastPath(MovementStatus mv) => mv.LastPathKind switch
    {
        MovementKind.Loop => $"not moving; last ran loop '{mv.LastPathName}'",
        MovementKind.Lair => mv.LastPathName is { Length: > 0 } name
            ? $"not moving; last ran auto-lair '{name}'"
            : "not moving; last ran auto-lair",
        _                 => "not moving",
    };

    // The "<name> (map M, room R)" room descriptor used by @status, @path, and (with
    // an exits tail) @where. One formatter so the three replies never drift.
    private static string FormatRoom(Room room) =>
        $"{room.DisplayName} (map {room.Key.Map}, room {room.Key.Room})";

    // Status form of @party (no args, or any channel other than Local). Three
    // exclusive outcomes:
    //   - solo (no active party) → no active party
    //   - self is following → I'm following <leader-given>
    //   - self is leading → I'm leading: <follower-given>, …
    // Followers list is given-names only, in roster order, skipping self + leader.
    // Family names are omitted because MajorMUD commands and the rest of the
    // @-command layer only ever address players by their given name.
    private void OnPartyStatus(RemoteCommandContext ctx)
    {
        if (!_party.IsInParty || _party.Members.Count == 0)
        {
            ctx.Reply("no active party");
            return;
        }
        if (!_party.SelfIsLeader)
        {
            string leader = GivenName(_party.LeaderName ?? string.Empty);
            ctx.Reply(string.IsNullOrEmpty(leader)
                ? "I'm following an unknown leader"
                : $"I'm following {leader}");
            return;
        }
        // Leader path — list followers' given names. Skip self + the
        // leader row (which is self, but be defensive about ordering).
        List<string> followers = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsLeader) continue;
            string g = GivenName(m.Name);
            if (!string.IsNullOrEmpty(g)) followers.Add(g);
        }
        ctx.Reply(followers.Count == 0
            ? "I'm leading: (no followers)"
            : $"I'm leading: {string.Join(", ", followers)}");
    }

    // @where — reply with the current room's name, exits, and (Map, Room) key.
    // Reads the live RoomTracker snapshot through the injected _readCurrentRoom
    // accessor. When the room is unknown (tracker Unknown / Lost, or no game data
    // loaded yet) on a Paradigm realm, ask the game for our authoritative position
    // via `rm` and answer once it re-anchors — the game can tell us exactly where
    // we are, so a bare "Location unknown" would throw away information we can
    // fetch. Stock realms (or an unwired seam) fall straight through to the plain
    // "Location unknown" reply. The engine wraps the payload in { } at SendReply.
    private void OnWhere(RemoteCommandContext ctx)
    {
        Room? room = _readCurrentRoom?.Invoke();
        if (room is not null) { ReplyWhere(ctx, room); return; }

        if (TryResolveWhereViaResync(ctx)) return;

        ctx.Refuse("Location unknown");
    }

    // On Paradigm, fire `rm` and defer the @where reply until the game answers.
    // Returns false when a re-fix can't be started so OnWhere falls back to the
    // plain "Location unknown". Both callbacks fire at most once (the resolver's
    // one-shot contract) on the UI thread, so re-reading the room and replying
    // needs no locking.
    private bool TryResolveWhereViaResync(RemoteCommandContext ctx)
    {
        if (_requestPositionRefix is null) return false;
        return _requestPositionRefix(
            $"@where from {ctx.Sender}",
            // onResolved: rm answered and the tracker re-anchored.
            key =>
            {
                Room? resolved = _readCurrentRoom?.Invoke();
                if (resolved is not null) ReplyWhere(ctx, resolved);
                // rm answered but the room isn't in our map data — still report
                // the authoritative (map, room) the game just handed us.
                else ctx.Reply($"map {key.Map}, room {key.Room} (not in map data)");
            },
            // onFailed: rm timed out / room not in graph — degrade gracefully.
            () => ctx.Refuse("Location unknown"));
    }

    private static void ReplyWhere(RemoteCommandContext ctx, Room room)
    {
        // Stable enum order so a repeated query reads the same and exits
        // come out N, S, E, W, NE, ... rather than dictionary order.
        string exits = room.Exits.Count == 0
            ? "none"
            : string.Join(", ", room.Exits.Keys.OrderBy(d => (int)d).Select(d => d.ToLongName()));
        ctx.Reply($"{FormatRoom(room)}; exits: {exits}");
    }

    // @who — reply with the other players and monsters sharing the current room.
    // Reads the live RoomEntityClassifier occupant snapshot through the injected
    // _readRoomEntities accessor. The classifier's list is built from the wire's
    // "Also here:" line, which by game convention excludes the local player — so
    // no self-filtering is needed. An empty or null list (room had no occupants,
    // or none observed yet) replies "no one". Each entity is named by its resolved
    // (canonical) name: a player's given name, a monster's base name without
    // flavour prefix. The engine wraps the payload in { } at SendReply time,
    // yielding {no one} / {Raijin, Susanoo, giant rat}.
    private void OnWho(RemoteCommandContext ctx)
    {
        IReadOnlyList<RoomEntity>? entities = _readRoomEntities?.Invoke();
        if (entities is null || entities.Count == 0) { ctx.Reply("no one"); return; }
        ctx.Reply(string.Join(", ", entities.Select(e => e.ResolvedName)));
    }

    // @path — reply with what movement engine is running (loop name / auto-lair /
    // walking-to-destination), the current room name + (Map, Room) key, and the
    // walker's step progress as step X/Y. Reads the live engine snapshot through
    // _readMovement and the room through _readCurrentRoom. When no engine is
    // running the reply is "not moving"; the engine wraps it in { } at SendReply
    // time.
    private void OnPath(RemoteCommandContext ctx)
    {
        MovementStatus mv = _readMovement?.Invoke() ?? default;
        if (mv.Kind == MovementKind.None)
        {
            // Idle now — name the last loop / auto-lair run so a party member can
            // help a dead / stopped player pick their circuit back up.
            ctx.Reply(DescribeLastPath(mv));
            return;
        }

        string engine = MovementEnginePhrase(mv);

        string where = DescribeRoom();

        // CurrentStep is the 0-based next-step index; report it 1-based and
        // clamp to TotalSteps for the tail where every step is sent but the
        // walker is still awaiting the final arrival confirmation.
        string step = mv.TotalSteps > 0
            ? $"; step {Math.Min(mv.CurrentStep + 1, mv.TotalSteps)}/{mv.TotalSteps}"
            : string.Empty;

        ctx.Reply($"{engine}; {where}{step}");
    }

    // ----- @party (channel-aware: status query or sub-command dispatch) --

    // Channel-aware handler for @party:
    //   - Telepath / Gangpath — always reply with the status form (solo / leading
    //     / following); args are ignored. The destructive party sub-commands
    //     (attack / rest / etc.) are leader → party-room coordination, not
    //     back-channel whispers, so we refuse to honour them off-channel.
    //   - Local (Say) with no args — also status form. Lets the leader broadcast
    //     @party in the room and have every present follower call out their status
    //     without doing anything destructive.
    //   - Local (Say) with args — sub-command dispatch via DispatchPartySubCommand.
    //     Gated on IsActivePartyMember + DisallowPartyDirectives because the
    //     engine's authorize tier for @party is QueryHealthStatus (so a non-party
    //     caller with that grant reaches this handler for the status form too) —
    //     the destructive verb path needs its own party-member gate.
    // Engine-level wiring: @party sits at QueryHealthStatus in the catalog plus an
    // @party-specific party-member fallback in RemoteCommandManager.IsAuthorised,
    // so this handler fires for (a) any active party member regardless of
    // per-player grants and (b) any non-party caller with an explicit
    // QueryHealthStatus grant. Hard-blocks for @party set suicide / @party reroll
    // fire at engine level before this handler runs.
    private void OnParty(RemoteCommandContext ctx)
    {
        if (ctx.Channel != RemoteChannel.Local || ctx.Args.Count == 0)
        {
            OnPartyStatus(ctx);
            return;
        }
        // Local + args → sub-command dispatch. Re-check party whitelist
        // here because the engine-level authorize tier (QueryHealthStatus)
        // lets non-party players reach this handler for the status form;
        // the destructive verb path is party-member-only by design.
        if (_engine.DisallowPartyDirectives) return;
        if (!IsActivePartyMember(ctx.Sender)) return;
        DispatchPartySubCommand(ctx);
    }

    private bool IsActivePartyMember(string sender)
    {
        string senderGiven = GivenName(sender);
        foreach (PartyMember m in _party.Members)
        {
            if (m.Name.Equals(sender, StringComparison.OrdinalIgnoreCase)) return true;
            if (GivenName(m.Name).Equals(senderGiven, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Relay the leader's @party <command> directive to the follower's wire. This
    // is a party-membership-gated passthrough — the party analogue of @do, but
    // authorised by active party membership rather than the per-player
    // ExecuteCommands grant. Whatever the leader broadcasts (`use chime`,
    // `ring chime`, `.hi`, `stat`, …) is sent verbatim so the whole party can be
    // driven in lock-step. A fixed sub-command whitelist used to drop everything
    // else silently, which stranded the CMD-teleport choreography (a follower who
    // never got `use chime` never teleported, so the leader's re-invite couldn't
    // reach them). The two rewrites below survive because the token the leader
    // speaks isn't the verb the follower must type. The only payloads @party
    // refuses — `set suicide` (arms unattended auto-suicide) and `reroll` (wipes
    // the build) — are stopped at engine level before this handler runs
    // (RemoteCommandManager.IsHardBlocked); a plain `suicide` relays like anything
    // else. Verbatim stops at plain ASCII: anything outside it goes out as every
    // reply's text does (WireText).
    private void DispatchPartySubCommand(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;
        string sub = ctx.Args[0].ToLowerInvariant();
        string local = WireText.Ascii(sub switch
        {
            "meditate" => "medi",                                  // MajorMUD's canonical short form
            // @party go <dir> — a CARDINAL direction moves on the bare token, so drop
            // the "go" (MajorMUD moves on the direction alone). A non-cardinal target
            // is a TEXT exit (go hole / go path) that MUST keep its "go" verb — the
            // bare token isn't a command (report: "@party go hole" wrongly sent
            // "hole"). The non-cardinal "go" falls through to the verbatim default.
            "go" when ctx.Args.Count >= 2
                     && DirectionExtensions.TryFromToken(ctx.Args[1], out _)
                => string.Join(" ", ctx.Args.Skip(1)),
            _ => string.Join(" ", ctx.Args),
        });
        byte[] bytes = Encoding.Latin1.GetBytes(local + "\r");
        RelayingFor = GivenName(ctx.Sender);
        try { _wireSender(bytes); }
        finally { RelayingFor = null; }
        PartyDirectiveRelayed?.Invoke(GivenName(ctx.Sender), local);
    }

    // Whose `@party <command>` is going out right now, while it is being sent; null
    // otherwise. A `break` relayed this way holds the combat engine's attack as a
    // `@do break` does, and a relayed attack lifts it (user, 2026-10-10); the
    // outbound observer that reads the line needs to know it is one of these, and
    // whose (DoHandler.SendingFor is the same for `@do`). Read at the wire, so a
    // line the send gate dropped holds nothing.
    public string? RelayingFor { get; private set; }

    // A member's `@party <command>` was just put on our wire: who sent it and the
    // command as sent. Most are no concern of the follow (a hand-over before an
    // item gate, a rest); ComebackRequester picks out a leader's teleport keyword.
    public event Action<string, string>? PartyDirectiveRelayed;

    // ----- Lives / invite / join -----------------------------------------

    // Reply with the local character's remaining lives count via the engine's
    // LivesProvider — same source the @suicide hard-block consults. Returns "lives
    // unknown" until the user has typed stat at least once this session so we
    // don't volunteer a possibly-stale number to a caller deciding whether to send
    // @suicide.
    private void OnLives(RemoteCommandContext ctx)
    {
        int? lives = _engine.LivesProvider?.Invoke();
        if (lives is null) { ctx.Refuse("lives unknown"); return; }
        ctx.Reply($"{lives} {(lives == 1 ? "life" : "lives")} remaining");
    }

    // @invite — sender is asking us to invite them into our party. Three exclusive
    // outcomes:
    //   - self is following → reply "I'm following X; denied." to prevent
    //     leader-follower chains.
    //   - party full (6 members) → reply with the follower roster so the sender
    //     knows why and can pick a different party.
    //   - otherwise → send invite <sender-given> on the wire. The invite itself IS
    //     the confirmation; no additional telepath reply.
    // The follower-deny and full-party replies are refusals, so the Warn setting
    // silences them (the remote-command reply policy).
    // A mortally-wounded character can neither join nor lead a party — the game
    // rejects the join/invite command outright ("You may not do that while you
    // are mortally wounded!"). Reply with the reason, and who (if anyone) is
    // dragging our downed body, so the partymate understands why and whether help
    // is already underway instead of watching the command bounce. A refusal like
    // any other: the Warn setting silences it (user, 2026-10-10). Returns true
    // when it handled (blocked) the command.
    private bool RejectIfMortallyWounded(RemoteCommandContext ctx, string verb)
    {
        if (!_player.IsMortallyWounded) return false;
        string drag = _readDraggedBy?.Invoke() is { Length: > 0 } who
            ? $"being dragged by {who}"
            : "nobody is dragging me";
        ctx.Refuse($"Can't {verb} - I'm mortally wounded; {drag}.");
        return true;
    }

    private void OnInvite(RemoteCommandContext ctx)
    {
        if (RejectIfMortallyWounded(ctx, "invite")) return;
        if (_party.IsInParty && !_party.SelfIsLeader)
        {
            string leader = GivenName(_party.LeaderName ?? string.Empty);
            ctx.Refuse(string.IsNullOrEmpty(leader)
                ? "I'm following someone; denied."
                : $"I'm following {leader}; denied.");
            return;
        }
        if (_party.Members.Count >= 6)
        {
            // Full party — list the 5 followers (excluding self).
            List<string> followers = new();
            foreach (PartyMember m in _party.Members)
            {
                if (m.IsSelf) continue;
                string g = GivenName(m.Name);
                if (!string.IsNullOrEmpty(g)) followers.Add(g);
            }
            string list = followers.Count == 0 ? "(roster unknown)" : string.Join(", ", followers);
            ctx.Refuse($"My party is full: {list} {(followers.Count == 1 ? "is" : "are")} following me.");
            return;
        }
        if (_wireSender is null) return;
        string senderGiven = GivenName(ctx.Sender);
        byte[] bytes = Encoding.Latin1.GetBytes($"invite {senderGiven}\r");
        _wireSender(bytes);
    }

    // @join — sender wants us to type join <them> to enter their party. Symmetric
    // to OnInvite: deny when we're already following someone (no chain mutation),
    // else send the join command. No confirmation reply — the join itself is the
    // answer.
    private void OnJoin(RemoteCommandContext ctx)
    {
        if (RejectIfMortallyWounded(ctx, "join")) return;
        string senderGiven = GivenName(ctx.Sender);
        // Deny only when we're following someone OTHER than the sender. If the
        // leader we believe we're following is the one @join-ing us, our
        // "following" state is provably stale — a leader never re-invites a current
        // follower — so honour it and rejoin instead of denying "already
        // following". This is what un-sticks a follower whose train-stats trip
        // broke the party server-side without any leave-line reaching our client
        // (report stock-20260801-002423). Sending join when we somehow WERE still
        // in the party is harmless (the game just says we already are).
        if (_party.IsInParty && !_party.SelfIsLeader && !IsBelievedLeader(senderGiven))
        {
            string leader = GivenName(_party.LeaderName ?? string.Empty);
            ctx.Refuse(string.IsNullOrEmpty(leader)
                ? "I'm following someone; denied."
                : $"I'm following {leader}; denied.");
            return;
        }
        if (_wireSender is null) return;
        // Our own client may have joined that leader a moment ago by itself (a
        // Stock follower does on the leader's "found you"): one join is enough.
        //
        // This holds on every realm, and for a join this handler sent itself: a
        // second @join from the same leader inside ComebackRequester's reply
        // window (3 s) sends nothing. That is safe because the game has answered
        // the first join by then or is about to, and no client sends two @joins
        // that close together (the nag's shortest cadence is seconds apart, 10 s
        // by default); the next one a leader sends is taken as usual.
        if (JoinAlreadySent?.Invoke(senderGiven) == true) return;
        byte[] bytes = Encoding.Latin1.GetBytes($"join {senderGiven}\r");
        _wireSender(bytes);
        JoinSent?.Invoke(senderGiven);
    }

    // Told with the leader's given name when an @join had us send `join <leader>`,
    // so whoever reads the game's answer to a join knows this one was sent.
    public Action<string>? JoinSent { get; set; }

    // Whether a `join` to that leader went out a moment ago from elsewhere in the
    // client (ComebackRequester.JoinJustSentTo).
    public Func<string, bool>? JoinAlreadySent { get; set; }

    // True when the given name is the leader we currently believe we're following.
    // A telepath from that leader asking us to (re)join / (re)invite means our
    // "following" belief is stale — see OnJoin.
    private bool IsBelievedLeader(string senderGiven)
        => !string.IsNullOrEmpty(senderGiven)
           && _party.IsInParty && !_party.SelfIsLeader
           && string.Equals(GivenName(_party.LeaderName ?? string.Empty), senderGiven,
                            StringComparison.OrdinalIgnoreCase);

    // ----- @wait / @ok receive -------------------------------------------

    private void OnWait(RemoteCommandContext ctx) => NotePause(ctx.Sender);

    // Register member as asking the party to pause and raise the gate on the 0→1
    // transition. Shared by the @wait telepath handler and the inbound .@held say
    // path (PartyAilmentTracker): a held member's say pauses the leader exactly as
    // an explicit @wait would, and the member's eventual @ok (sent by their own
    // AilmentSyncEngine on last-clear) releases it via OnOk. Honours the
    // leader-side PartySettings.IgnoreWaitWhenLeading opt-out.
    public void NotePause(string member, bool ignoreOk = false)
    {
        // Leader-side opt-out: when we're leading and the user has set
        // "ignore @wait when leading", a follower's @wait must not pause
        // the leader's automation. Drop it before it reaches the gate.
        if (_party.SelfIsLeader && _readPartySettings?.Invoke() is { IgnoreWaitWhenLeading: true })
            return;
        bool wasPaused = IsPaused;
        WaitingMembers.Add(member);
        if (ignoreOk) _okDistrusted.Add(GivenName(member));
        SetMemberWaitFlag(member, true);
        if (!wasPaused && IsPaused) PauseGateChanged?.Invoke(true);
    }

    private void OnOk(RemoteCommandContext ctx)
    {
        _lastOkAt[GivenName(ctx.Sender)] = _now();
        if (_okDistrusted.Contains(GivenName(ctx.Sender))) return;
        bool wasPaused = IsPaused;
        WaitingMembers.Remove(ctx.Sender);
        SetMemberWaitFlag(ctx.Sender, false);
        if (wasPaused && !IsPaused) PauseGateChanged?.Invoke(false);
    }

    // @waiting — our leader is holding for our @ok. It sends this after re-collecting
    // us from a room we were left in (held / knocked down): we fell out of the party
    // there, so a hold that cleared while we were out never sent its @ok. Answer
    // @ok now when nothing still holds us; otherwise the @ok goes out when the last
    // hold clears (PartyRestSync.RequestOk), since we're back in the party.
    //
    // The @ok goes by telepath whatever channel the @waiting came in on, as every
    // @ok we send does: it is a command for the leader alone, and said aloud or
    // gangpathed it would reach every client that holds a wait of ours.
    private void OnWaiting(RemoteCommandContext ctx)
    {
        string senderGiven = GivenName(ctx.Sender);
        if (!IsBelievedLeader(senderGiven) || _wireSender is null) return;
        if (_readHoldingWait?.Invoke() == true) return;
        _wireSender(Encoding.Latin1.GetBytes($"/{senderGiven} @ok\r"));
    }

    // Drop one member's @wait without their @ok — they fell out of the party (left
    // behind), so holding the leader in place can't help them; going back for them
    // does, and their rejoin re-applies the hold (PartyAilmentTracker.NoteInferredHold).
    public void ReleaseWait(string member)
    {
        bool wasPaused = IsPaused;
        if (!WaitingMembers.Remove(member) && !WaitingMembers.Remove(GivenName(member))) return;
        if (wasPaused && !IsPaused) PauseGateChanged?.Invoke(false);
    }

    // Force-release every outstanding @wait at once — the second release path
    // beside a per-member @ok. The leader's wait timer expired
    // (PartyWaitMovementGate), so we stop holding for members who never sent
    // @ok: clear the roster WAIT chips too and fire the single paused→unpaused
    // transition so the movement bridge resumes. No-op when nobody is waiting.
    public void ClearAllWaits()
    {
        _okDistrusted.Clear();
        if (WaitingMembers.Count == 0) return;
        foreach (string member in WaitingMembers) SetMemberWaitFlag(member, false);
        WaitingMembers.Clear();
        PauseGateChanged?.Invoke(false);
    }

    // Mirror the WaitingMembers set onto the matching PartyMember.IsWaiting so the
    // PartyWindow can render a per-row WAIT chip without binding through the
    // HashSet. Senders are matched by given-name (first whitespace-delimited
    // token) — MajorMUD telepaths arrive with the given name only, while par's
    // member rows can be "Given Family", so we compare on the prefix. Silent no-op
    // when the sender isn't in the party (e.g. an out-of-party stranger spamming
    // @wait would still occupy the IsPaused gate but has no member row to flag).
    private void SetMemberWaitFlag(string sender, bool waiting)
    {
        string senderGiven = GivenName(sender);
        foreach (PartyMember m in _party.Members)
        {
            if (GivenName(m.Name).Equals(senderGiven, StringComparison.OrdinalIgnoreCase))
            {
                m.IsWaiting = waiting;
                return;
            }
        }
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
