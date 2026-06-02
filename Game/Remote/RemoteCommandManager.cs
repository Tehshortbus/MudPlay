using System.Collections.Concurrent;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Extensible engine for MajorMUD @-commands. Subscribes to
/// <see cref="ChatRouter.EntryClassified"/>, identifies @-prefixed
/// messages, resolves the sender's per-player permission against the
/// Phase 5 Players tab, enforces hard-blocks, and dispatches to the
/// handler registered for the command.
/// </summary>
/// <remarks>
/// <para>
/// PR 6.2 ships the engine only — no handlers are registered here.
/// PR 6.3 lands the party-essential set (<c>@health</c> / <c>@where</c>
/// / <c>@version</c> / <c>@status</c> / <c>@par</c> + the
/// <c>@party &lt;sub&gt;</c> whitelist + receive-side <c>@wait</c> /
/// <c>@ok</c>). Phase 7 / Phase 12 register more handlers by calling
/// <see cref="RegisterHandler"/>; the engine stays untouched.
/// </para>
/// <para>
/// Permission model:
/// <list type="number">
///   <item><b>Hard-blocks first</b>. Any command matching the reroll
///         hard-block (the substring <c>"reroll"</c> appears anywhere
///         in the command name or args) is blocked unconditionally —
///         no per-player flag can override. Suicide commands
///         (<c>@do suicide</c>, <c>@party suicide</c>) are blocked
///         when <see cref="LivesProvider"/> reports remaining lives
///         ≤ <see cref="MaxSuicideLivesThreshold"/>. If
///         <see cref="LivesProvider"/> is <c>null</c> we treat lives
///         as unknown and block — safer default until the Phase 9
///         DEATH section wires up live-lives tracking.</item>
///   <item><b>Party whitelist</b>. Handlers registered with
///         <see cref="PlayerRemoteControls.None"/> as the required
///         category are gated on whether the sender is an active party
///         member (per <see cref="PartyState.Members"/>). Used for the
///         base <c>@party &lt;sub&gt;</c> commands that every party
///         member should be able to issue regardless of trust level.</item>
///   <item><b>Per-player flag check</b>. The merged
///         <see cref="PlayerRecord.RemoteControls"/> (BBS-tier
///         observation + Char-tier customisation) must include the
///         handler's required <see cref="PlayerRemoteControls"/> flag.
///         Default for never-seen / un-customised players is
///         <see cref="PlayerRemoteControls.None"/> → every per-flag
///         handler denies. Users grant access via the Phase 5 Players
///         tab edit dialog.</item>
/// </list>
/// </para>
/// <para>
/// Threading: <see cref="ChatRouter.EntryClassified"/> fires on the
/// dispatcher's thread (the MessageRouter already marshalled the line
/// upstream). Handler invocation happens on the same thread. Long work
/// inside a handler must offload via <see cref="System.Threading.Tasks.Task.Run(System.Action)"/>
/// per the project-wide MessageRouter convention.
/// </para>
/// </remarks>
public sealed class RemoteCommandManager : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly PlayerDatabase _players;
    private readonly LogService? _log;
    /// <summary>cmd-name (lower-case) → (requiredFlag, handler).</summary>
    private readonly ConcurrentDictionary<string, Registration> _handlers
        = new(StringComparer.OrdinalIgnoreCase);
    private Action<byte[]>? _wireSender;
    /// <summary>Test seam — last bytes the engine asked to write to the wire. Inspected by tests when no real sender is bound.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();
    private bool _disposed;

    /// <summary>
    /// Optional callback returning the local character's remaining lives.
    /// Consulted for the <c>@do suicide</c> / <c>@party suicide</c>
    /// hard-block check. When <c>null</c> the engine treats lives as
    /// unknown and blocks every suicide-bearing command unconditionally —
    /// the conservative default until the Phase 9 DEATH section wires
    /// up live-lives tracking.
    /// </summary>
    public Func<int>? LivesProvider { get; set; }

    /// <summary>
    /// Block <c>@do suicide</c> / <c>@party suicide</c> when remaining
    /// lives are at or below this threshold. Default 3 per the Phase 6
    /// spec; settable to 0 in Settings.Other (PR 6.9 wires the UI) to
    /// allow forced suicide through all lives.
    /// </summary>
    public int MaxSuicideLivesThreshold { get; set; } = 3;

    public RemoteCommandManager(
        ChatRouter chat,
        PartyState party,
        PlayerDatabase players,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(players);
        _chat = chat;
        _party = party;
        _players = players;
        _log = log;
        _chat.EntryClassified += OnChatEntry;
    }

    /// <summary>
    /// Bind a callback that sends raw bytes to the wire. Same shape as
    /// <see cref="MacroDispatcher.SetSender"/> — the main-window VM
    /// supplies <c>SendUserInput</c> at construction-time. Required for
    /// <see cref="RemoteCommandContext.Reply"/> to actually transmit.
    /// </summary>
    public void SetWireSender(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _wireSender = send;
    }

    /// <summary>
    /// Register a handler for one @-command. <paramref name="requiredCategory"/>
    /// declares which <see cref="PlayerRemoteControls"/> flag the sender
    /// must hold; pass <see cref="PlayerRemoteControls.None"/> to mark the
    /// handler as "party-whitelist only" (allowed for any active party
    /// member regardless of per-player flags — used by <c>@party &lt;sub&gt;</c>).
    /// </summary>
    /// <param name="command">Verbatim including the leading <c>@</c>. Matched case-insensitively.</param>
    /// <param name="requiredCategory">The <see cref="PlayerRemoteControls"/> flag the sender must hold, or <see cref="PlayerRemoteControls.None"/> for the party-whitelist case.</param>
    /// <param name="handler">Invoked with a <see cref="RemoteCommandContext"/> when the command authorises.</param>
    public void RegisterHandler(string command, PlayerRemoteControls requiredCategory, Action<RemoteCommandContext> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);
        if (!command.StartsWith('@'))
            throw new ArgumentException("Remote-command name must start with '@'.", nameof(command));
        _handlers[command] = new Registration(requiredCategory, handler);
    }

    /// <summary>Drop a previously-registered handler. Idempotent — returns false when nothing was registered.</summary>
    public bool UnregisterHandler(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return _handlers.TryRemove(command, out _);
    }

    /// <summary>Total handlers currently registered. Useful for tests and the LogService startup line.</summary>
    public int HandlerCount => _handlers.Count;

    /// <summary>Test seam — drives the engine without going through ChatRouter.</summary>
    internal void DispatchForTests(ChatLogEntry entry) => OnChatEntry(entry);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
    }

    // ----- Engine pipeline ------------------------------------------------

    private void OnChatEntry(ChatLogEntry entry)
    {
        // Channel filter — only inbound chat-style channels carry remote
        // commands. RealmEvent / DaySeparator / TelepathOutgoing / etc.
        // don't.
        RemoteChannel? channel = MapChannel(entry.Channel);
        if (channel is null) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return; // Self-echo / unknown sender.
        if (string.IsNullOrEmpty(entry.Message)) return;
        if (entry.Message[0] != '@') return;             // Not an @-command.

        // Parse: command = first whitespace token (lower-cased for the
        // registry lookup); args = remaining tokens.
        string[] tokens = entry.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;
        string command = tokens[0];
        string[] args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        // Hard-blocks first — bypass everything else.
        if (IsHardBlocked(command, args, out string? reason))
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Blocked {command} from {entry.Speaker}: {reason}");
            return;
        }

        if (!_handlers.TryGetValue(command, out Registration registration))
        {
            // Unknown @-command — no handler registered. Silent by design:
            // many @-commands are valid (Phase 7 / Phase 12 register more)
            // and an unfamiliar one isn't necessarily malicious.
            return;
        }

        // Authorisation: party-whitelist OR per-player flag.
        if (!IsAuthorised(entry.Speaker, registration.RequiredCategory))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Denied {command} from {entry.Speaker} (lacks {registration.RequiredCategory}).");
            return;
        }

        // Invoke. Engine supplies the Reply callback that routes back via
        // the same channel.
        RemoteCommandContext ctx = new(
            Sender:          entry.Speaker,
            Command:         command,
            Args:            args,
            OriginalMessage: entry.Message,
            Channel:         channel.Value,
            Reply:           text => SendReply(channel.Value, entry.Speaker, text));
        try { registration.Handler(ctx); }
        catch (Exception ex)
        {
            // A handler throwing shouldn't tear down the engine — log and
            // move on. The handler author is responsible for keeping its
            // own work robust.
            _log?.Log(LogSeverity.Warn, "RemoteCmd",
                $"Handler for {command} threw on {entry.Speaker}'s invocation: {ex.Message}");
        }
    }

    private static RemoteChannel? MapChannel(ChatChannel c) => c switch
    {
        ChatChannel.TelepathIncoming => RemoteChannel.Telepath,
        ChatChannel.Gossip           => RemoteChannel.Gossip,
        ChatChannel.Gangpath         => RemoteChannel.Gangpath,
        ChatChannel.Local            => RemoteChannel.Local,
        _                            => null,
    };

    /// <summary>
    /// Apply the engine-wide hard-block list. Order matters — reroll
    /// blocks first (always denied), suicide depends on the lives
    /// threshold. Anything containing <c>"reroll"</c> as a whole token in
    /// command or args is blocked because MajorMUD requires the literal
    /// word reroll for the destructive action.
    /// </summary>
    private bool IsHardBlocked(string command, string[] args, out string? reason)
    {
        // reroll — token-level match across command + args
        if (ContainsToken(command, args, "reroll"))
        {
            reason = "reroll hard-block (always denied)";
            return true;
        }
        // suicide — token-level match, conditional on lives
        if (ContainsToken(command, args, "suicide"))
        {
            // @party suicide is always blocked (the Phase 6 spec lists it
            // alongside @party reroll as unconditional). @do suicide is
            // gated on lives.
            if (command.Equals("@party", StringComparison.OrdinalIgnoreCase))
            {
                reason = "@party suicide hard-block (always denied)";
                return true;
            }
            int? lives = LivesProvider?.Invoke();
            if (lives is null)
            {
                reason = "suicide gated but lives unknown — defaulting to blocked";
                return true;
            }
            if (lives <= MaxSuicideLivesThreshold)
            {
                reason = $"suicide blocked at lives={lives} (threshold {MaxSuicideLivesThreshold})";
                return true;
            }
        }
        reason = null;
        return false;
    }

    private static bool ContainsToken(string command, string[] args, string token)
    {
        if (command.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string a in args)
            if (a.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool IsAuthorised(string sender, PlayerRemoteControls requiredCategory)
    {
        // Special case: requiredCategory == None means "party whitelist —
        // allowed for any active party member". Used by @party <sub>.
        if (requiredCategory == PlayerRemoteControls.None)
            return IsActivePartyMember(sender);

        // Per-player flag check. Look up the merged record (BBS observation
        // + Char customisation) and bitmask-test the required category.
        foreach (PlayerRecord rec in _players.Players)
        {
            if (rec.DisplayName.Equals(sender, StringComparison.OrdinalIgnoreCase)
                || rec.GivenName.Equals(sender, StringComparison.OrdinalIgnoreCase))
            {
                return (rec.RemoteControls & requiredCategory) == requiredCategory;
            }
        }
        // Never-seen sender → deny.
        return false;
    }

    private bool IsActivePartyMember(string sender)
    {
        foreach (PartyMember m in _party.Members)
        {
            if (m.Name.Equals(sender, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Default reply formatting per channel. The wire syntax matches
    /// MajorMUD's standard commands; specific BBSes may vary and the
    /// format can be tuned by replacing the wire-sender or providing a
    /// custom <see cref="Reply"/> callback in a future PR. Bytes are
    /// Latin-1 + trailing CR — same encoding the macro / trigger paths
    /// use because BBSes expect 8-bit-clean bytes, not UTF-8.
    /// </summary>
    /// <remarks>
    /// Telepath uses <c>tel &lt;name&gt;</c> not <c>t</c> — the short
    /// form is interpreted as <c>say</c> on Playpen BBS (verified live).
    /// Recipient is always the GIVEN name (first whitespace-delimited
    /// token of <see cref="ChatLogEntry.Speaker"/>); MajorMUD doesn't
    /// accept "Given Family" as a telepath recipient. Speaker as
    /// classified by ChatRouter is already single-word so this is a
    /// no-op for the engine but the rule's worth stating for any
    /// future callers.
    /// </remarks>
    private void SendReply(RemoteChannel channel, string recipient, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string given = GivenName(recipient);
        string wire = channel switch
        {
            RemoteChannel.Telepath => $"tel {given} {text}",
            RemoteChannel.Gossip   => $"gos {text}",
            RemoteChannel.Gangpath => $"gang {text}",
            RemoteChannel.Local    => $"say {text}",
            _                      => text,
        };
        byte[] bytes = Encoding.Latin1.GetBytes(wire + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private readonly record struct Registration(
        PlayerRemoteControls RequiredCategory,
        Action<RemoteCommandContext> Handler);
}
