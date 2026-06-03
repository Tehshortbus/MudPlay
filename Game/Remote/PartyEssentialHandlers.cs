using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// First consumer of <see cref="RemoteCommandManager"/>. Registers the
/// party-essential @-commands the Phase 6 spec ships:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Query-tier</b> — <c>@version</c>, <c>@health</c>, <c>@status</c>,
///         <c>@par</c>, <c>@where</c>. Each replies via the channel the
///         command arrived on with a short response derived from local
///         state (<see cref="PlayerState"/>, <see cref="PartyState"/>).
///         <c>@where</c> ships a placeholder reply here — Phase 7's
///         RoomTracker enriches it when room state is available.</item>
///   <item><b>Party whitelist</b> — <c>@party &lt;sub&gt;</c>. Dispatches
///         on the first arg token to translate the leader's directive
///         (<c>attack</c> / <c>rest</c> / <c>meditate</c> / <c>go &lt;dir&gt;</c>
///         / <c>stat</c> / <c>i</c> / <c>par</c>) into the corresponding
///         local command sent via the engine's wire-sender.</item>
///   <item><b>Receive-only signalling</b> — <c>@wait</c> / <c>@ok</c>.
///         Recorded in <see cref="WaitingMembers"/> for PR 6.7 to consume
///         when it wires the pause-gate registration. Until then the
///         handlers just track who's currently asking the party to wait.</item>
/// </list>
/// <para>
/// Lifetime: registered once at <see cref="AppServices"/> construction
/// after the engine ships. Disposal unregisters every command so
/// repeated AppServices builds in tests don't leak handler entries.
/// </para>
/// </remarks>
public sealed class PartyEssentialHandlers : IDisposable
{
    /// <summary>Commands this consumer registers. Used by <see cref="Dispose"/> to clean up.</summary>
    private static readonly string[] RegisteredCommands =
    {
        "@version", "@health", "@status", "@par", "@where",
        "@party", "@wait", "@ok",
        "@lives", "@invite", "@join",
    };

    private readonly RemoteCommandManager _engine;
    private readonly PlayerState _player;
    private readonly PartyState _party;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>
    /// Player names currently asking the party to <c>@wait</c>. Removed
    /// when the same player sends <c>@ok</c>. PR 6.7's pause-gate
    /// registration reads this set to decide whether the auto-walker /
    /// combat engine should hold off. Case-insensitive.
    /// </summary>
    public HashSet<string> WaitingMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pause-gate read consumed by Phase 12 automation engines (auto-walk,
    /// auto-combat, etc.) — true whenever at least one party member has
    /// asked us to <c>@wait</c> and hasn't yet sent <c>@ok</c>. Cheap to
    /// poll; engines either check before each tick or subscribe to
    /// <see cref="PauseGateChanged"/> for edge-triggered notification.
    /// </summary>
    public bool IsPaused => WaitingMembers.Count > 0;

    /// <summary>
    /// Fires on every transition of <see cref="IsPaused"/>. Lets the
    /// pause-gate consumer drop a single subscription instead of polling.
    /// </summary>
    public event Action<bool>? PauseGateChanged;

    public PartyEssentialHandlers(
        RemoteCommandManager engine,
        PlayerState player,
        PartyState party)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(party);
        _engine = engine;
        _player = player;
        _party  = party;

        // Categories sourced from RemoteCommandCatalog — single source
        // of truth for every documented @-command's required permission
        // category. Hardcoding the category per RegisterHandler call
        // led to drift; routing through the catalog keeps Phase 6 and
        // future Phase 7 / 12 handlers consistent with the wiki + the
        // Players-tab 12-checkbox UI.
        Register("@version", OnVersion);
        Register("@health",  OnHealth);
        Register("@status",  OnStatus);
        Register("@par",     OnPartyStatus);  // alias for @party query form
        Register("@where",   OnWhere);
        Register("@party",   OnParty);
        Register("@wait",    OnWait);
        Register("@ok",      OnOk);
        Register("@lives",   OnLives);
        Register("@invite",  OnInvite);
        Register("@join",    OnJoin);
    }

    /// <summary>
    /// Bind the wire-sender. Required for <see cref="OnParty"/> to forward
    /// the party-leader's directive as a local command. Same signature
    /// shape as <see cref="MacroDispatcher.SetSender"/>; the main-window
    /// VM provides <c>SendUserInput</c>.
    /// </summary>
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

    /// <summary>
    /// Wrapper around <see cref="RemoteCommandManager.RegisterHandler"/>
    /// that pulls the required category from
    /// <see cref="RemoteCommandCatalog"/>. Throws if the command isn't in
    /// the catalog — Phase 6 handlers are catalog-backed by definition,
    /// so a missing entry means the catalog needs updating, not the
    /// handler.
    /// </summary>
    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // ----- Query handlers -------------------------------------------------

    private void OnVersion(RemoteCommandContext ctx) =>
        // Matches the format other clients use for the same query
        // (e.g. MegaMUD replies "{MegaMud 1.03u}"): "<name> <version>"
        // bracketed by the engine's SendReply at wire time.
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

    /// <summary>
    /// Status form of <c>@par</c> / <c>@party</c> (no args, or any
    /// channel other than Local). Three exclusive outcomes:
    /// <list type="bullet">
    ///   <item>solo (no active party) → <c>no active party</c></item>
    ///   <item>self is following → <c>I'm following &lt;leader-given&gt;</c></item>
    ///   <item>self is leading → <c>I'm leading: &lt;follower-given&gt;, …</c></item>
    /// </list>
    /// Followers list is given-names only, in roster order, skipping
    /// self + leader. Family names are omitted because MajorMUD
    /// commands and the rest of the @-command layer only ever address
    /// players by their given name.
    /// </summary>
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

    private void OnWhere(RemoteCommandContext ctx)
    {
        // Room tracking ships in Phase 7 — emit a placeholder so the
        // sender at least knows their request was received and the
        // engine is alive. Phase 7 PR 7.1 (RoomTracker) replaces this
        // body with a real lookup.
        ctx.Reply("Location unknown (room tracker pending)");
    }

    // ----- @party (channel-aware: status query or sub-command dispatch) --

    /// <summary>
    /// Channel-aware handler for <c>@party</c>:
    /// <list type="bullet">
    ///   <item><b>Telepath / Gangpath</b> — always reply with the status
    ///         form (alias for <c>@par</c>); args are ignored. The
    ///         destructive party sub-commands (attack / rest / etc.)
    ///         are leader → party-room coordination, not back-channel
    ///         whispers, so we refuse to honour them off-channel.</item>
    ///   <item><b>Local (Say) with no args</b> — also status form. Lets
    ///         the leader broadcast <c>@party</c> in the room and have
    ///         every present follower call out their status without
    ///         doing anything destructive.</item>
    ///   <item><b>Local (Say) with args</b> — sub-command dispatch via
    ///         <see cref="DispatchPartySubCommand"/>. Gated on
    ///         <see cref="IsActivePartyMember"/> +
    ///         <see cref="RemoteCommandManager.DisablePartyWhitelist"/>
    ///         because the engine's authorize tier for <c>@party</c> is
    ///         QueryHealthStatus (so a non-party caller with that grant
    ///         can use it as a status-query alias for <c>@par</c>) — the
    ///         destructive verb path needs its own party-member gate.</item>
    /// </list>
    /// Engine-level wiring: <c>@party</c> sits at QueryHealthStatus in
    /// the catalog plus an <c>@party</c>-specific party-member fallback
    /// in <see cref="RemoteCommandManager.IsAuthorised"/>, so this
    /// handler fires for (a) any active party member regardless of
    /// per-player grants and (b) any non-party caller with an explicit
    /// QueryHealthStatus grant.
    /// Hard-blocks for <c>@party suicide</c> / <c>@party reroll</c>
    /// fire at engine level before this handler runs.
    /// </summary>
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
        if (_engine.DisablePartyWhitelist) return;
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

    /// <summary>
    /// Map the leader's <c>@party &lt;sub&gt;</c> directive onto the local
    /// command a follower would type to perform the action. Unknown
    /// sub-commands are silently ignored — they're not party-essentials
    /// and shouldn't trip the wire from a typo.
    /// </summary>
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

    /// <summary>
    /// Reply with the local character's remaining lives count via the
    /// engine's <see cref="RemoteCommandManager.LivesProvider"/> —
    /// same source the <c>@suicide</c> hard-block consults. Returns
    /// <c>lives unknown</c> until the user has typed <c>stat</c> at
    /// least once this session so we don't volunteer a possibly-stale
    /// number to a caller deciding whether to send <c>@suicide</c>.
    /// </summary>
    private void OnLives(RemoteCommandContext ctx)
    {
        int? lives = _engine.LivesProvider?.Invoke();
        if (lives is null) { ctx.Reply("lives unknown"); return; }
        ctx.Reply($"{lives} {(lives == 1 ? "life" : "lives")} remaining");
    }

    /// <summary>
    /// <c>@invite</c> — sender is asking us to invite them into our
    /// party. Three exclusive outcomes:
    /// <list type="bullet">
    ///   <item>self is following → reply <c>I'm following X; denied.</c>
    ///         to prevent leader-follower chains (per user spec).</item>
    ///   <item>party full (6 members) → reply with the follower roster
    ///         so the sender knows why and can pick a different party.</item>
    ///   <item>otherwise → send <c>invite &lt;sender-given&gt;</c> on the
    ///         wire. The invite itself IS the confirmation; no
    ///         additional telepath reply.</item>
    /// </list>
    /// The follower-deny reply is gated on
    /// <see cref="RemoteCommandManager.WarnOnDenial"/> per the same
    /// remote-command reply policy that gates the suicide policy-block
    /// reply — denials are user-suppressible noise. The full-party
    /// reply is informational coordination, not a denial, and fires
    /// regardless.
    /// </summary>
    private void OnInvite(RemoteCommandContext ctx)
    {
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

    /// <summary>
    /// <c>@join</c> — sender wants us to type <c>join &lt;them&gt;</c>
    /// to enter their party. Symmetric to <see cref="OnInvite"/>: deny
    /// when we're already following someone (no chain mutation), else
    /// send the join command. No confirmation reply — the join itself
    /// is the answer.
    /// </summary>
    private void OnJoin(RemoteCommandContext ctx)
    {
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

    // ----- @wait / @ok receive (pause-gate consumes in PR 6.7) ----------

    private void OnWait(RemoteCommandContext ctx)
    {
        bool wasPaused = IsPaused;
        WaitingMembers.Add(ctx.Sender);
        SetMemberWaitFlag(ctx.Sender, true);
        if (!wasPaused && IsPaused) PauseGateChanged?.Invoke(true);
    }

    private void OnOk(RemoteCommandContext ctx)
    {
        bool wasPaused = IsPaused;
        WaitingMembers.Remove(ctx.Sender);
        SetMemberWaitFlag(ctx.Sender, false);
        if (wasPaused && !IsPaused) PauseGateChanged?.Invoke(false);
    }

    /// <summary>
    /// Mirror the <see cref="WaitingMembers"/> set onto the matching
    /// <see cref="PartyMember.IsWaiting"/> so the PartyWindow can render
    /// a per-row WAIT chip without binding through the HashSet. Senders
    /// are matched by given-name (first whitespace-delimited token) —
    /// MajorMUD telepaths arrive with the given name only, while par's
    /// member rows can be "Given Family", so we compare on the prefix.
    /// Silent no-op when the sender isn't in the party (e.g. an
    /// out-of-party stranger spamming @wait would still occupy the
    /// IsPaused gate but has no member row to flag).
    /// </summary>
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
