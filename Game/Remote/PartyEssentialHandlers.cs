using System.Text;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// The party-essential @-commands:
//   - Query-tier — @version, @health, @status, @lives, @where. Each replies via
//     the channel the command arrived on with a short response derived from local
//     state (PlayerState, PartyState, PlayerStats).
//   - Party whitelist — @party <sub>. Dispatches on the first arg token to
//     translate the leader's directive (attack / rest / meditate / go <dir> /
//     stat / i / par) into the corresponding local command sent via the engine's
//     wire-sender.
//   - Receive-only signalling — @wait / @ok. Recorded in WaitingMembers, which
//     the pause-gate reads to decide whether to hold automation.
//
// Lifetime: registered once at AppServices construction after the engine ships.
// Disposal unregisters every command so repeated AppServices builds in tests
// don't leak handler entries.
public sealed class PartyEssentialHandlers : IDisposable
{
    // Commands this consumer registers. Used by Dispose to clean up.
    private static readonly string[] RegisteredCommands =
    {
        "@version", "@health", "@status", "@where", "@who", "@path",
        "@party", "@wait", "@ok",
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
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Player names currently asking the party to @wait. Removed when the same
    // player sends @ok. The pause-gate reads this set to decide whether the
    // auto-walker / combat engine should hold off. Case-insensitive.
    public HashSet<string> WaitingMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Pause-gate read consumed by automation engines (auto-walk, auto-combat,
    // etc.) — true whenever at least one party member has asked us to @wait and
    // hasn't yet sent @ok. Cheap to poll; engines either check before each tick or
    // subscribe to PauseGateChanged for edge-triggered notification.
    public bool IsPaused => WaitingMembers.Count > 0;

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
        Func<string?>? readDraggedBy = null)
    {
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

        // Categories sourced from RemoteCommandCatalog — single source of truth
        // for every documented @-command's required permission category.
        // Hardcoding the category per RegisterHandler call led to drift; routing
        // through the catalog keeps every handler consistent with the
        // Players-tab 12-checkbox UI.
        Register("@version", OnVersion);
        Register("@health",  OnHealth);
        Register("@status",  OnStatus);
        Register("@where",   OnWhere);
        Register("@who",     OnWho);
        Register("@path",    OnPath);
        Register("@party",   OnParty);
        Register("@wait",    OnWait);
        Register("@ok",      OnOk);
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
        if (!_player.HasPromptData) { ctx.Reply("HP unknown — no prompt observed yet"); return; }
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

    private void OnStatus(RemoteCommandContext ctx)
    {
        if (!_player.HasPromptData) { ctx.Reply("Status unknown"); return; }
        ctx.Reply(_player.Position.ToString());
    }

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
    // accessor; a null room (tracker Unknown / Lost, or no game data loaded yet)
    // yields a "Location unknown" reply rather than an empty body. The engine
    // wraps the payload in { } at SendReply time.
    private void OnWhere(RemoteCommandContext ctx)
    {
        Room? room = _readCurrentRoom?.Invoke();
        if (room is null) { ctx.Reply("Location unknown"); return; }

        // Stable enum order so a repeated query reads the same and exits
        // come out N, S, E, W, NE, ... rather than dictionary order.
        string exits = room.Exits.Count == 0
            ? "none"
            : string.Join(", ", room.Exits.Keys.OrderBy(d => (int)d).Select(d => d.ToLongName()));
        ctx.Reply($"{room.DisplayName} (map {room.Key.Map}, room {room.Key.Room}); exits: {exits}");
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
        if (mv.Kind == MovementKind.None) { ctx.Reply("not moving"); return; }

        string engine = mv.Kind switch
        {
            MovementKind.Loop    => $"running loop '{mv.Label}'",
            MovementKind.Lair    => "auto-lair",
            MovementKind.Walking => $"walking to {mv.Label}",
            _                    => "moving",
        };

        Room? room = _readCurrentRoom?.Invoke();
        string where = room is null
            ? "location unknown"
            : $"{room.DisplayName} (map {room.Key.Map}, room {room.Key.Room})";

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
    // QueryHealthStatus grant. Hard-blocks for @party suicide / @party reroll fire
    // at engine level before this handler runs.
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

    // Map the leader's @party <sub> directive onto the local command a follower
    // would type to perform the action. Unknown sub-commands are silently ignored
    // — they're not party-essentials and shouldn't trip the wire from a typo.
    private void DispatchPartySubCommand(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;
        string sub = ctx.Args[0].ToLowerInvariant();
        string? local = sub switch
        {
            "attack"   => "attack",
            "rest"     => "rest",
            "meditate" => "medi",     // MajorMUD's canonical short form
            "stat"     => "stat",
            "i"        => "i",
            "par"      => "par",
            // @party go <dir> — forward the direction token; "go n" → "n"
            "go" when ctx.Args.Count >= 2 => ctx.Args[1].ToLowerInvariant(),
            _ => null,
        };
        if (local is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(local + "\r");
        _wireSender(bytes);
    }

    // ----- Lives / invite / join -----------------------------------------

    // Reply with the local character's remaining lives count via the engine's
    // LivesProvider — same source the @suicide hard-block consults. Returns "lives
    // unknown" until the user has typed stat at least once this session so we
    // don't volunteer a possibly-stale number to a caller deciding whether to send
    // @suicide.
    private void OnLives(RemoteCommandContext ctx)
    {
        int? lives = _engine.LivesProvider?.Invoke();
        if (lives is null) { ctx.Reply("lives unknown"); return; }
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
    // The follower-deny reply is gated on WarnOnDenial per the same remote-command
    // reply policy that gates the suicide policy-block reply — denials are
    // user-suppressible noise. The full-party reply is informational coordination,
    // not a denial, and fires regardless.
    // A mortally-wounded character can neither join nor lead a party — the game
    // rejects the join/invite command outright ("You may not do that while you
    // are mortally wounded!"). Reply with the reason, and who (if anyone) is
    // dragging our downed body, so the partymate understands why and whether help
    // is already underway instead of watching the command bounce. Fires
    // regardless of WarnOnDenial: this is party-coordination info for a member
    // trying to help, like the full-party @invite reply, not a security-sensitive
    // denial to a stranger. Returns true when it handled (blocked) the command.
    private bool RejectIfMortallyWounded(RemoteCommandContext ctx, string verb)
    {
        if (!_player.IsMortallyWounded) return false;
        string drag = _readDraggedBy?.Invoke() is { Length: > 0 } who
            ? $"being dragged by {who}"
            : "nobody is dragging me";
        ctx.Reply($"Can't {verb} — I'm mortally wounded; {drag}.");
        return true;
    }

    private void OnInvite(RemoteCommandContext ctx)
    {
        if (RejectIfMortallyWounded(ctx, "invite")) return;
        if (_party.IsInParty && !_party.SelfIsLeader)
        {
            if (!_engine.WarnOnDenial) return;
            string leader = GivenName(_party.LeaderName ?? string.Empty);
            ctx.Reply(string.IsNullOrEmpty(leader)
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
            ctx.Reply($"My Party is full, {list} are following me.");
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
        if (_party.IsInParty && !_party.SelfIsLeader)
        {
            if (!_engine.WarnOnDenial) return;
            string leader = GivenName(_party.LeaderName ?? string.Empty);
            ctx.Reply(string.IsNullOrEmpty(leader)
                ? "I'm following someone; denied."
                : $"I'm following {leader}; denied.");
            return;
        }
        if (_wireSender is null) return;
        string senderGiven = GivenName(ctx.Sender);
        byte[] bytes = Encoding.Latin1.GetBytes($"join {senderGiven}\r");
        _wireSender(bytes);
    }

    // ----- @wait / @ok receive -------------------------------------------

    private void OnWait(RemoteCommandContext ctx) => NotePause(ctx.Sender);

    // Register member as asking the party to pause and raise the gate on the 0→1
    // transition. Shared by the @wait telepath handler and the inbound .@held say
    // path (PartyAilmentTracker): a held member's say pauses the leader exactly as
    // an explicit @wait would, and the member's eventual @ok (sent by their own
    // AilmentSyncEngine on last-clear) releases it via OnOk. Honours the
    // leader-side PartySettings.IgnoreWaitWhenLeading opt-out.
    public void NotePause(string member)
    {
        // Leader-side opt-out: when we're leading and the user has set
        // "ignore @wait when leading", a follower's @wait must not pause
        // the leader's automation. Drop it before it reaches the gate.
        if (_party.SelfIsLeader && _readPartySettings?.Invoke() is { IgnoreWaitWhenLeading: true })
            return;
        bool wasPaused = IsPaused;
        WaitingMembers.Add(member);
        SetMemberWaitFlag(member, true);
        if (!wasPaused && IsPaused) PauseGateChanged?.Invoke(true);
    }

    private void OnOk(RemoteCommandContext ctx)
    {
        bool wasPaused = IsPaused;
        WaitingMembers.Remove(ctx.Sender);
        SetMemberWaitFlag(ctx.Sender, false);
        if (wasPaused && !IsPaused) PauseGateChanged?.Invoke(false);
    }

    // Force-release every outstanding @wait at once — the second release path
    // beside a per-member @ok. The leader's wait timer expired
    // (PartyWaitMovementGate), so we stop holding for members who never sent
    // @ok: clear the roster WAIT chips too and fire the single paused→unpaused
    // transition so the movement bridge resumes. No-op when nobody is waiting.
    public void ClearAllWaits()
    {
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
