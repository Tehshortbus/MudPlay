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

    public PartyEssentialHandlers(RemoteCommandManager engine, PlayerState player, PartyState party)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(party);
        _engine = engine;
        _player = player;
        _party  = party;

        _engine.RegisterHandler("@version", PlayerRemoteControls.QueryVersion,      OnVersion);
        _engine.RegisterHandler("@health",  PlayerRemoteControls.QueryHealthStatus, OnHealth);
        _engine.RegisterHandler("@status",  PlayerRemoteControls.QueryHealthStatus, OnStatus);
        _engine.RegisterHandler("@par",     PlayerRemoteControls.QueryHealthStatus, OnPar);
        _engine.RegisterHandler("@where",   PlayerRemoteControls.QueryLocation,     OnWhere);
        _engine.RegisterHandler("@party",   PlayerRemoteControls.None,              OnParty);
        _engine.RegisterHandler("@wait",    PlayerRemoteControls.None,              OnWait);
        _engine.RegisterHandler("@ok",      PlayerRemoteControls.None,              OnOk);
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

    // ----- Query handlers -------------------------------------------------

    private void OnVersion(RemoteCommandContext ctx) =>
        ctx.Reply(AppInfo.DisplayName);

    private void OnHealth(RemoteCommandContext ctx)
    {
        if (!_player.HasPromptData) { ctx.Reply("HP unknown — no prompt observed yet"); return; }
        string mana = _player.ManaType switch
        {
            ManaType.Mana => $", MA {_player.Ma}/{_player.MaxMa}",
            ManaType.Kai  => $", KAI {_player.Ma}/{_player.MaxMa}",
            _             => string.Empty,
        };
        ctx.Reply($"HP {_player.Hp}/{_player.MaxHp}{mana} ({_player.Position})");
    }

    private void OnStatus(RemoteCommandContext ctx)
    {
        if (!_player.HasPromptData) { ctx.Reply("Status unknown"); return; }
        ctx.Reply(_player.Position.ToString());
    }

    private void OnPar(RemoteCommandContext ctx)
    {
        if (_party.Members.Count == 0) { ctx.Reply("No party active"); return; }
        StringBuilder sb = new();
        sb.Append($"Party ({_party.Members.Count}):");
        foreach (PartyMember m in _party.Members)
        {
            string lead = m.IsLeader ? "*" : " ";
            sb.Append($" {lead}{m.Name} H:{m.HpPercent}% M:{m.MpPercent}% ({m.Position})");
        }
        ctx.Reply(sb.ToString());
    }

    private void OnWhere(RemoteCommandContext ctx)
    {
        // Room tracking ships in Phase 7 — emit a placeholder so the
        // sender at least knows their request was received and the
        // engine is alive. Phase 7 PR 7.1 (RoomTracker) replaces this
        // body with a real lookup.
        ctx.Reply("Location unknown (room tracker pending)");
    }

    // ----- Party-whitelist handler (@party <sub>) ------------------------

    /// <summary>
    /// Map the leader's <c>@party &lt;sub&gt;</c> directive onto the local
    /// command a follower would type to perform the action. Unknown
    /// sub-commands are silently ignored — they're not party-essentials
    /// and shouldn't trip the wire from a typo.
    /// </summary>
    private void OnParty(RemoteCommandContext ctx)
    {
        if (_wireSender is null) return;
        if (ctx.Args.Count == 0) return;
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
