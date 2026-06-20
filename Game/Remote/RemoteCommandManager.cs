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
/// / <c>@version</c> / <c>@status</c> / <c>@party</c> status + the
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
    /// <summary>
    /// prefix → (requiredFlag, handler) for suffix-form commands like
    /// <c>@equip-&lt;setname&gt;</c>, where the text after the registered
    /// prefix is the command's single argument. Consulted only after an exact
    /// <see cref="_handlers"/> miss, so an exact registration always wins.
    /// </summary>
    private readonly ConcurrentDictionary<string, Registration> _prefixHandlers
        = new(StringComparer.OrdinalIgnoreCase);
    private Action<byte[]>? _wireSender;
    /// <summary>Test seam — last bytes the engine asked to write to the wire. Inspected by tests when no real sender is bound.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();
    private bool _disposed;

    /// <summary>
    /// Live lives provider. Returns the current Lives count from the
    /// most recent <c>stat</c>-screen parse, or <c>null</c> when no
    /// stat screen has been observed yet (so the hard-block defaults
    /// to blocked rather than trusting a stale value). Wired in
    /// <see cref="Services.AppServices"/> as
    /// <c>() => Stats.HasParsed ? PlayerStats.Lives : (int?)null</c>.
    /// </summary>
    public Func<int?>? LivesProvider { get; set; }

    /// <summary>
    /// Block <c>@do suicide</c> / <c>@party suicide</c> when remaining
    /// lives are at or below this threshold. Default 5; settable to 0 in
    /// Settings.Other to allow forced suicide through all lives. Max
    /// lives in MajorMUD is 9, so the UI clamps this to 0..9.
    /// </summary>
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // ----- Settings.Talk-driven knobs --------------------------------------
    // Pushed by TalkSectionViewModel.ApplyToServices on Apply / on profile
    // load. Defaults match the TalkSettings DTO defaults — anything not yet
    // wired up has permissive defaults that don't change behaviour.

    /// <summary>
    /// Hard kill-switch above every per-channel + per-player permission.
    /// When <c>true</c> the engine ignores every inbound @-command. Pushed
    /// from <see cref="Models.Profile.TalkSettings.DisallowAllRemoteCommands"/>.
    /// </summary>
    public bool MasterDisable { get; set; }

    /// <summary>
    /// Disallows the <c>@party &lt;sub&gt;</c> directive path only. When
    /// <c>true</c>, an active party member's <c>@party attack / rest /
    /// meditate / go / stat / i / par</c> is denied unless the sender
    /// carries an explicit per-player grant. Does NOT affect the rest of
    /// the party-whitelist (<c>@wait</c> / <c>@ok</c> / <c>@comeback</c> /
    /// <c>@share</c>) — those stay allowed for active members regardless.
    /// Pushed from
    /// <see cref="Models.Profile.TalkSettings.DisallowPartyCommands"/>.
    /// </summary>
    public bool DisallowPartyDirectives { get; set; }

    /// <summary>
    /// Leader-side eligibility hook for a stranded follower's
    /// <c>@comeback</c>. A left-behind follower is dropped from the party
    /// server-side, so <see cref="IsActivePartyMember"/> can't authorise
    /// them — yet they're still recoverable. <see cref="PartyComebackManager"/>
    /// wires this to <see cref="PartyManager.WasRecentlyPartied"/>, which
    /// returns true only for senders who departed inside the grace window
    /// and were NOT deliberately uninvited. Consulted ONLY for
    /// <c>@comeback</c> (see <see cref="IsAuthorised"/>); null = no extra
    /// allowance, so the plain party-whitelist gate stands.
    /// </summary>
    public Func<string, bool>? ComebackEligibility { get; set; }

    /// <summary>Drop @-commands arriving on the Telepath channel.</summary>
    public bool DisableTelepathChannel { get; set; }

    /// <summary>Drop @-commands arriving on the Gangpath channel.</summary>
    public bool DisableGangpathChannel { get; set; }

    /// <summary>Drop @-commands arriving on the Local say channel.</summary>
    public bool DisableLocalChannel { get; set; }

    /// <summary>
    /// When <c>true</c>, send <see cref="FailureMessage"/> back to the
    /// originator on per-player denial / unknown-command / party-whitelist
    /// denial. Hard-blocks and user-disabled paths (master / per-channel)
    /// stay silent regardless. Pushed from
    /// <see cref="Models.Profile.TalkSettings.WarnOnInvalidRemoteCommand"/>.
    /// </summary>
    public bool WarnOnDenial { get; set; } = true;

    /// <summary>
    /// Reply text used by the <see cref="WarnOnDenial"/> path. Pushed
    /// from <see cref="Models.Profile.TalkSettings.RemoteCommandFailureMessage"/>.
    /// The engine wraps every reply in <c>{ }</c> at send time, so this
    /// string should be bare text — adding literal braces here would
    /// double them.
    /// </summary>
    public string FailureMessage { get; set; } = "command invalid or not allowed";

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

    /// <summary>
    /// Register a handler for a family of suffix-form commands sharing one
    /// <paramref name="prefix"/> — e.g. prefix <c>@equip-</c> matches
    /// <c>@equip-fighting</c>, <c>@equip-tank</c>, …. The text after the
    /// prefix is folded in as the command's leading argument
    /// (<see cref="RemoteCommandContext.Args"/>[0]), so the handler reads the
    /// dynamic part the same way an ordinary arg-bearing command does. A
    /// prefix is consulted only when no exact <see cref="RegisterHandler"/>
    /// entry matches, and only when the inbound command carries a non-empty
    /// remainder after the prefix.
    /// </summary>
    /// <param name="prefix">Verbatim including the leading <c>@</c> and the trailing separator (e.g. <c>"@equip-"</c>). Matched case-insensitively.</param>
    /// <param name="requiredCategory">The <see cref="PlayerRemoteControls"/> flag the sender must hold.</param>
    /// <param name="handler">Invoked with a <see cref="RemoteCommandContext"/> whose <c>Args[0]</c> is the suffix.</param>
    public void RegisterPrefixHandler(string prefix, PlayerRemoteControls requiredCategory, Action<RemoteCommandContext> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(handler);
        if (!prefix.StartsWith('@'))
            throw new ArgumentException("Remote-command prefix must start with '@'.", nameof(prefix));
        _prefixHandlers[prefix] = new Registration(requiredCategory, handler);
    }

    /// <summary>Drop a previously-registered prefix handler. Idempotent — returns false when nothing was registered.</summary>
    public bool UnregisterPrefixHandler(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return _prefixHandlers.TryRemove(prefix, out _);
    }

    /// <summary>Total handlers currently registered. Useful for tests and the LogService startup line.</summary>
    public int HandlerCount => _handlers.Count;

    /// <summary>Test seam — drives the engine without going through ChatRouter.</summary>
    internal void DispatchForTests(ChatLogEntry entry) => OnChatEntry(entry);

    /// <summary>
    /// Enumerate the catalog commands <paramref name="sender"/> is
    /// permitted to issue, given their merged per-player permission grant.
    /// Backs the <c>@help</c> handler's reply. Party-whitelist commands
    /// (those mapped to <see cref="PlayerRemoteControls.None"/> — @wait /
    /// @ok / @comeback / @share) are excluded: they're gated by party
    /// membership alone, not an explicit permission flag, so they don't
    /// belong in a per-permission command list. Reuses <see cref="IsAuthorised"/> so the
    /// answer always matches what the engine would actually allow. Result
    /// follows the catalog's grouped enumeration order.
    /// </summary>
    public IReadOnlyList<string> GetPermittedCommands(string sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        List<string> permitted = new();
        foreach (KeyValuePair<string, PlayerRemoteControls> entry in RemoteCommandCatalog.Map)
        {
            if (entry.Value == PlayerRemoteControls.None) continue; // party-whitelist, not flag-gated
            if (IsAuthorised(sender, entry.Value, entry.Key))
                permitted.Add(entry.Key);
        }
        return permitted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
    }

    // ----- Engine pipeline ------------------------------------------------

    private void OnChatEntry(ChatLogEntry entry)
    {
        // Master kill-switch (Settings.Talk → Disallow all remote control
        // commands). Above everything else — no logging either, no point
        // spamming the log with denied lines when the user explicitly
        // muted the whole feature.
        if (MasterDisable) return;

        // Channel filter — only inbound chat-style channels carry remote
        // commands. RealmEvent / DaySeparator / TelepathOutgoing / etc.
        // don't. Per-channel Settings.Talk disables fold in here.
        RemoteChannel? channel = MapChannel(entry.Channel);
        if (channel is null) return;
        if (IsChannelDisabled(channel.Value)) return;

        if (string.IsNullOrEmpty(entry.Speaker)) return; // Self-echo / unknown sender.
        // Defence-in-depth: a self-echo whose verb form leaks the literal
        // "You" as the speaker (e.g. a classifier regex that captured it)
        // must never be treated as an inbound command — the local
        // character can't issue remote commands to itself. "You" is never a
        // real player name, so this is a safe hard guard on top of the
        // classifier emitting a null speaker for own-speech.
        if (entry.Speaker.Equals("You", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        if (entry.Message[0] != '@') return;             // Not an @-command.

        // Parse: command = first whitespace token (lower-cased for the
        // registry lookup); args = remaining tokens.
        string[] tokens = entry.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;
        string command = tokens[0];
        string[] args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        // Hard-blocks first — bypass everything else. Always silent (no
        // reply) even when WarnOnDenial is on: never advertise the block
        // to a malicious caller.
        if (IsHardBlocked(command, args, out string? reason))
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Blocked {command} from {entry.Speaker}: {reason}");
            return;
        }

        // Forcible @do / @party suicide redirect — UNCONDITIONALLY
        // blocked but DOES reply (gated on WarnOnDenial) with a hint
        // pointing the sender at the dedicated @suicide handler that
        // owns the elevated-permission + lives-threshold + stored-
        // password contract. Has to run BEFORE the suicide policy
        // block below because that one's permissive when lives >
        // threshold; we don't want @do suicide to slip through
        // just because we happen to have enough lives.
        string? forcedSuicideRedirect = GetForcedSuicideRedirectReply(command, args);
        if (forcedSuicideRedirect is not null)
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Forcible-suicide blocked from {entry.Speaker}: {forcedSuicideRedirect}");
            SendDenialReply(channel.Value, entry.Speaker, specificReason: forcedSuicideRedirect);
            return;
        }

        // Suicide lives-threshold — user-configured policy block.
        // UNLIKE the hard-blocks above, this DOES reply because the
        // caller is typically a trusted party member and the policy
        // is explicit, not a safety net. Routes through
        // SendDenialReply so the WarnOnDenial master gate applies
        // (specific reason wins over the generic FailureMessage
        // when WarnOnDenial is on; nothing sent when it's off).
        string? suicideReply = GetSuicidePolicyBlockReply(command, args);
        if (suicideReply is not null)
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Suicide policy-blocked from {entry.Speaker}: {suicideReply}");
            SendDenialReply(channel.Value, entry.Speaker, specificReason: suicideReply);
            return;
        }

        if (!_handlers.TryGetValue(command, out Registration registration))
        {
            // No exact handler — try the suffix-form prefix handlers
            // (@equip-<set>). A match folds the suffix in as the leading arg.
            if (TryMatchPrefixHandler(command, out registration, out string suffix))
            {
                args = Prepend(suffix, args);
            }
            else
            {
                // Unknown @-command — no handler registered. Surface back to
                // sender per Settings.Talk → Warn on invalid remote command.
                _log?.Log(LogSeverity.Debug, "RemoteCmd",
                    $"Unknown command {command} from {entry.Speaker}.");
                SendDenialReply(channel.Value, entry.Speaker);
                return;
            }
        }

        // Authorisation: party-whitelist OR per-player flag.
        if (!IsAuthorised(entry.Speaker, registration.RequiredCategory, command))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Denied {command} from {entry.Speaker} (lacks {registration.RequiredCategory}).");
            SendDenialReply(channel.Value, entry.Speaker);
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

    private bool IsChannelDisabled(RemoteChannel c) => c switch
    {
        RemoteChannel.Telepath => DisableTelepathChannel,
        RemoteChannel.Gangpath => DisableGangpathChannel,
        RemoteChannel.Local    => DisableLocalChannel,
        _                      => false,
    };

    /// <summary>
    /// Send a denial reply to the sender. <paramref name="specificReason"/>
    /// — when non-null — wins over the generic
    /// <see cref="FailureMessage"/>; null falls through to the
    /// configured generic text. Always gated by
    /// <see cref="WarnOnDenial"/>: when off, ALL invalid / denial
    /// replies are suppressed regardless of whether the reason is
    /// specific or generic.
    /// </summary>
    /// <remarks>
    /// Used by both the engine's own denial paths (unknown command,
    /// per-player flag denial, party-whitelist denial — all use
    /// the generic <see cref="FailureMessage"/>) and the suicide
    /// policy-block path (passes a specific reason).
    /// Handler-side failure replies (e.g.
    /// <see cref="SuicideHandler"/>'s invalid-password telepath)
    /// must check <see cref="WarnOnDenial"/> themselves before
    /// invoking <see cref="RemoteCommandContext.Reply"/>.
    /// </remarks>
    private void SendDenialReply(RemoteChannel channel, string sender, string? specificReason = null)
    {
        if (!WarnOnDenial) return;
        string text = specificReason ?? FailureMessage;
        if (string.IsNullOrWhiteSpace(text)) return;
        SendReply(channel, sender, text);
    }

    /// <summary>
    /// Per user direction: remote commands are accepted from every
    /// inbound chat channel EXCEPT the realm-wide noise channels —
    /// Gossip (also carries auctions), Yell (shout-style noise),
    /// system-level Broadcast / RealmEvent, and our own outbound
    /// echo (TelepathOutgoing).
    /// </summary>
    private static RemoteChannel? MapChannel(ChatChannel c) => c switch
    {
        ChatChannel.TelepathIncoming => RemoteChannel.Telepath,
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
    /// <summary>
    /// Unconditional hard-blocks — never reply, never explain. These
    /// guard against the most destructive misuse paths where any
    /// information leakage to the caller is its own risk. Today:
    /// reroll (always) + <c>@party suicide</c> (always).
    /// </summary>
    private bool IsHardBlocked(string command, string[] args, out string? reason)
    {
        // reroll — token-level match across command + args. Catches
        // direct @reroll (unknown command), @do reroll, @party reroll,
        // and any other passthrough verb's reroll arg.
        if (ContainsToken(command, args, "reroll"))
        {
            reason = "reroll hard-block (always denied)";
            return true;
        }
        reason = null;
        return false;
    }

    /// <summary>
    /// Forcible redirect for <c>@do suicide</c> and
    /// <c>@party suicide</c>: both are unconditionally blocked even
    /// at full permissions, but unlike the silent reroll hard-block
    /// they reply with a hint pointing the sender at the dedicated
    /// <c>@suicide</c> handler (which has its own SysopCommands /
    /// Elevated-Commands grant AND the lives-threshold policy gate).
    /// </summary>
    /// <remarks>
    /// Rationale per user direction: forcible-death actions should
    /// route exclusively through <c>@suicide</c> because that
    /// handler:
    /// <list type="bullet">
    ///   <item>requires the elevated SysopCommands permission tier,
    ///         which is granted independently of the (much
    ///         lower-trust) ExecuteCommands tier @do sits at;</item>
    ///   <item>respects the per-character
    ///         <see cref="MaxSuicideLivesThreshold"/>;</item>
    ///   <item>uses the stored encrypted suicide-password blob
    ///         autonomously via SuicideHandler, so the user's
    ///         intent is captured at the moment they ran
    ///         <c>set suicide</c> rather than inferred mid-session
    ///         from a wire-sniff.</item>
    /// </list>
    /// Returns the reply payload (bare text — engine wraps in <c>{}</c>
    /// at SendReply time) for the sender; <c>null</c> when the
    /// command isn't a forcible @do / @party suicide attempt.
    /// </remarks>
    private static string? GetForcedSuicideRedirectReply(string command, string[] args)
    {
        if (!ContainsToken(command, args, "suicide")) return null;
        bool isDo    = command.Equals("@do",    StringComparison.OrdinalIgnoreCase);
        bool isParty = command.Equals("@party", StringComparison.OrdinalIgnoreCase);
        if (!isDo && !isParty) return null;
        string verb = isDo ? "@do" : "@party";
        return $"{verb} suicide is not allowed, use @suicide";
    }

    /// <summary>
    /// User-configured policy block for direct <c>@suicide</c> based
    /// on the lives threshold. Distinct from
    /// <see cref="IsHardBlocked"/> because policy blocks SHOULD be
    /// communicated back to the sender — the caller is typically a
    /// trusted party member who needs to know why their command
    /// isn't firing (otherwise they'll assume our @-command engine
    /// is broken). Returns the reply text for the sender; <c>null</c>
    /// when the command isn't a suicide attempt or the threshold is
    /// satisfied.
    /// </summary>
    /// <remarks>
    /// Forcible @do / @party suicide variants are caught earlier by
    /// <see cref="GetForcedSuicideRedirectReply"/>; by the time this
    /// runs the only suicide-token-bearing command left is direct
    /// <c>@suicide</c> (or some future verb that legitimately uses
    /// suicide as a sub-command).
    /// </remarks>
    private string? GetSuicidePolicyBlockReply(string command, string[] args)
    {
        if (!ContainsToken(command, args, "suicide")) return null;
        int? lives = LivesProvider?.Invoke();
        if (lives is null)
            return "suicide blocked, lives unknown to client";
        if (lives <= MaxSuicideLivesThreshold)
            return $"suicide blocked, {lives} lives <= threshold {MaxSuicideLivesThreshold}";
        return null;
    }

    private static bool ContainsToken(string command, string[] args, string token)
    {
        if (command.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string a in args)
            if (a.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Find a registered prefix handler whose prefix is a strict prefix of
    /// <paramref name="command"/> (non-empty remainder required). On a match,
    /// <paramref name="suffix"/> is the trailing text — the dynamic argument.
    /// </summary>
    private bool TryMatchPrefixHandler(string command, out Registration registration, out string suffix)
    {
        foreach (KeyValuePair<string, Registration> kvp in _prefixHandlers)
        {
            if (command.Length > kvp.Key.Length
                && command.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                registration = kvp.Value;
                suffix = command[kvp.Key.Length..];
                return true;
            }
        }
        registration = default;
        suffix = string.Empty;
        return false;
    }

    private static string[] Prepend(string head, string[] tail)
    {
        if (tail.Length == 0) return new[] { head };
        string[] result = new string[tail.Length + 1];
        result[0] = head;
        Array.Copy(tail, 0, result, 1, tail.Length);
        return result;
    }

    private bool IsAuthorised(string sender, PlayerRemoteControls requiredCategory, string command)
    {
        // Special case: requiredCategory == None means "party whitelist —
        // allowed for any active party member". Used by @wait / @ok /
        // @comeback / @share. These are NOT affected by Disallow @party
        // commands — that toggle narrows only the @party directive path
        // (handled by the @party fallback branch below), per user spec.
        if (requiredCategory == PlayerRemoteControls.None)
        {
            if (IsActivePartyMember(sender)) return true;
            // @comeback is the one whitelist command a left-behind follower
            // can't satisfy via IsActivePartyMember — the server already
            // dropped them from the party. Honour it only if they departed
            // recently and we didn't uninvite them.
            if (command.Equals("@comeback", StringComparison.OrdinalIgnoreCase))
                return ComebackEligibility?.Invoke(sender) ?? false;
            return false;
        }

        // Per-player flag check. Look up the merged record (BBS observation
        // + Char customisation) and bitmask-test the required category.
        foreach (PlayerRecord rec in _players.Players)
        {
            if (rec.DisplayName.Equals(sender, StringComparison.OrdinalIgnoreCase)
                || rec.GivenName.Equals(sender, StringComparison.OrdinalIgnoreCase))
            {
                if ((rec.RemoteControls & requiredCategory) == requiredCategory)
                    return true;
                break;
            }
        }

        // @party fallback — the Phase 6 spec guarantees that base @party
        // commands are always allowed inside an active party regardless
        // of per-player grants (it's the social baseline of party play).
        // The catalog puts @party at QueryHealthStatus so non-party
        // callers with that grant can reach the status-query path;
        // this fallback restores the party-whitelist semantics for
        // members who don't carry an explicit grant.
        // DisallowPartyDirectives kills this @party member-fallback path —
        // it's the only command the toggle gates.
        if (command.Equals("@party", StringComparison.OrdinalIgnoreCase)
            && !DisallowPartyDirectives
            && IsActivePartyMember(sender))
        {
            return true;
        }

        // Never-seen / un-granted sender → deny.
        return false;
    }

    private bool IsActivePartyMember(string sender)
    {
        // Telepaths arrive with the given-name only; par-derived member
        // rows can be "Given Family". Match on the given-name prefix so
        // a "Buddy" @wait still pairs with a "Buddy Lastname" row.
        string senderGiven = GivenName(sender);
        foreach (PartyMember m in _party.Members)
        {
            if (m.Name.Equals(sender, StringComparison.OrdinalIgnoreCase))
                return true;
            if (GivenName(m.Name).Equals(senderGiven, StringComparison.OrdinalIgnoreCase))
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
    /// <para>
    /// Telepath uses <c>/&lt;name&gt;</c> (slash + given name, no space) —
    /// the verbose <c>t</c> / <c>tel</c> / <c>tell</c> forms are all
    /// interpreted as <c>say</c> on Playpen BBS (verified live).
    /// Recipient is always the GIVEN name (first whitespace-delimited
    /// token of <see cref="ChatLogEntry.Speaker"/>); MajorMUD doesn't
    /// accept "Given Family" as a telepath recipient. Speaker as
    /// classified by ChatRouter is already single-word so the given-name
    /// strip is a no-op for the engine but the rule's worth stating for
    /// any future callers.
    /// </para>
    /// <para>
    /// Reply payload is encapsulated in <c>{ }</c> braces — per user
    /// direction, every remote-command response carries the curly-brace
    /// meta-line convention so the recipient's terminal can visually
    /// distinguish an engine-generated answer from in-character speech.
    /// Handlers provide bare text; the engine adds the braces here, so
    /// nothing upstream has to remember the convention.
    /// </para>
    /// </remarks>
    private void SendReply(RemoteChannel channel, string recipient, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string given = GivenName(recipient);
        string payload = $"{{{text}}}";
        string wire = channel switch
        {
            RemoteChannel.Telepath => $"/{given} {payload}",
            RemoteChannel.Gangpath => $"gang {payload}",
            RemoteChannel.Local    => $"say {payload}",
            _                      => payload,
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
