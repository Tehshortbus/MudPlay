using System.Collections.Concurrent;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Extensible engine for MajorMUD @-commands. Subscribes to
// ChatRouter.EntryClassified, identifies @-prefixed messages, resolves the
// sender's per-player permission against the Players tab, enforces hard-blocks,
// and dispatches to the handler registered for the command.
//
// The engine registers no handlers itself — subsystems wire their own by calling
// RegisterHandler; the engine stays untouched.
//
// Permission model:
//   1. Hard-blocks first. Any command matching the reroll hard-block (the
//      substring "reroll" appears anywhere in the command name or args) is
//      blocked unconditionally — no per-player flag can override. Suicide
//      commands (@do suicide, @party suicide) are blocked when LivesProvider
//      reports remaining lives ≤ MaxSuicideLivesThreshold. If LivesProvider is
//      null we treat lives as unknown and block — the safer default.
//   2. Party whitelist. Handlers registered with PlayerRemoteControls.None as
//      the required category are gated on whether the sender is an active party
//      member (per PartyState.Members). Used for the base @party <sub> commands
//      that every party member should be able to issue regardless of trust
//      level.
//   3. Per-player flag check. The merged PlayerRecord.RemoteControls (BBS-tier
//      observation + Char-tier customisation) must include the handler's
//      required PlayerRemoteControls flag. Default for never-seen / un-customised
//      players is PlayerRemoteControls.None → every per-flag handler denies.
//      Users grant access via the Players tab edit dialog.
//
// Threading: ChatRouter.EntryClassified fires on the dispatcher's thread (the
// MessageRouter already marshalled the line upstream). Handler invocation happens
// on the same thread. Long work inside a handler must offload via Task.Run per
// the project-wide MessageRouter convention.
public sealed class RemoteCommandManager : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly PlayerDatabase _players;
    private readonly LogService? _log;
    // cmd-name (lower-case) → (requiredFlag, handler).
    private readonly ConcurrentDictionary<string, Registration> _handlers
        = new(StringComparer.OrdinalIgnoreCase);
    // prefix → (requiredFlag, handler) for suffix-form commands like
    // @equip-<setname>, where the text after the registered prefix is the
    // command's single argument. Consulted only after an exact _handlers miss, so
    // an exact registration always wins.
    private readonly ConcurrentDictionary<string, Registration> _prefixHandlers
        = new(StringComparer.OrdinalIgnoreCase);
    // Reserved @-tokens owned by OTHER subsystems that happen to ride the chat
    // channels (e.g. the party ailment-sync announces @poisoned / @blind / @held
    // that PartyAilmentTracker consumes on its own ChatRouter subscription).
    // They aren't commands, so the engine swallows them before the unknown-command
    // path — otherwise every announcing member gets a "{command invalid}" reply.
    private readonly HashSet<string> _ignored
        = new(StringComparer.OrdinalIgnoreCase);
    private Action<byte[]>? _wireSender;
    // Test seam — last bytes the engine asked to write to the wire. Inspected by
    // tests when no real sender is bound.
    internal List<byte[]> LastSentForTests { get; } = new();
    private bool _disposed;

    // Live lives provider. Returns the current Lives count from the most recent
    // stat-screen parse, or null when no stat screen has been observed yet (so
    // the hard-block defaults to blocked rather than trusting a stale value).
    // Wired in AppServices as
    // () => Stats.HasParsed ? PlayerStats.Lives : (int?)null.
    public Func<int?>? LivesProvider { get; set; }

    // Block @do suicide / @party suicide when remaining lives are at or below
    // this threshold. Default 5; settable to 0 in Settings.Other to allow forced
    // suicide through all lives. Max lives in MajorMUD is 9, so the UI clamps this
    // to 0..9.
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // ----- Settings.Talk-driven knobs --------------------------------------
    // Pushed by TalkSectionViewModel.ApplyToServices on Apply / on profile
    // load. Defaults match the TalkSettings DTO defaults — anything not yet
    // wired up has permissive defaults that don't change behaviour.

    // Hard kill-switch above every per-channel + per-player permission. When
    // true the engine ignores every inbound @-command. Pushed from
    // TalkSettings.DisallowAllRemoteCommands.
    public bool MasterDisable { get; set; }

    // Disallows the @party <sub> directive path only. When true, an active party
    // member's @party attack / rest / meditate / go / stat / i / par is denied
    // unless the sender carries an explicit per-player grant. Does NOT affect the
    // rest of the party-whitelist (@wait / @ok / @comeback / @share) — those stay
    // allowed for active members regardless. Pushed from
    // TalkSettings.DisallowPartyCommands.
    public bool DisallowPartyDirectives { get; set; }

    // Leader-side eligibility hook for a stranded follower's @comeback. A
    // left-behind follower is dropped from the party server-side, so
    // IsActivePartyMember can't authorise them — yet they're still recoverable.
    // PartyComebackManager wires this to PartyManager.WasRecentlyPartied, which
    // returns true only for senders who departed inside the grace window and were
    // NOT deliberately uninvited. Consulted ONLY for @comeback (see
    // IsAuthorised); null = no extra allowance, so the plain party-whitelist gate
    // stands.
    public Func<string, bool>? ComebackEligibility { get; set; }

    // Drop @-commands arriving on the Telepath channel.
    public bool DisableTelepathChannel { get; set; }

    // Drop @-commands arriving on the Gangpath channel.
    public bool DisableGangpathChannel { get; set; }

    // Drop @-commands arriving on the Local say channel.
    public bool DisableLocalChannel { get; set; }

    // When true, send FailureMessage back to the originator on per-player denial
    // / unknown-command / party-whitelist denial. Hard-blocks and user-disabled
    // paths (master / per-channel) stay silent regardless. Pushed from
    // TalkSettings.WarnOnInvalidRemoteCommand.
    public bool WarnOnDenial { get; set; } = true;

    // Reply text used by the WarnOnDenial path. Pushed from
    // TalkSettings.RemoteCommandFailureMessage. The engine wraps every reply in
    // { } at send time, so this string should be bare text — adding literal
    // braces here would double them.
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

    // Bind a callback that sends raw bytes to the wire. Same shape as
    // MacroDispatcher.SetSender — the main-window VM supplies SendUserInput at
    // construction-time. Required for RemoteCommandContext.Reply to actually
    // transmit.
    public void SetWireSender(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _wireSender = send;
    }

    // Register a handler for one @-command. requiredCategory declares which
    // PlayerRemoteControls flag the sender must hold; pass
    // PlayerRemoteControls.None to mark the handler as "party-whitelist only"
    // (allowed for any active party member regardless of per-player flags — used
    // by @party <sub>). command is verbatim including the leading @, matched
    // case-insensitively; handler is invoked with a RemoteCommandContext when the
    // command authorises.
    public void RegisterHandler(string command, PlayerRemoteControls requiredCategory, Action<RemoteCommandContext> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);
        if (!command.StartsWith('@'))
            throw new ArgumentException("Remote-command name must start with '@'.", nameof(command));
        _handlers[command] = new Registration(requiredCategory, handler);
    }

    // Drop a previously-registered handler. Idempotent — returns false when
    // nothing was registered.
    public bool UnregisterHandler(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return _handlers.TryRemove(command, out _);
    }

    // Mark an @-token as reserved: the engine swallows it silently (no handler
    // dispatch, no denial reply) so a subsystem that consumes it on its own
    // ChatRouter subscription — the party ailment-sync announces are the live
    // case — isn't undercut by an "{command invalid}" bounce. command is verbatim
    // including the leading @, matched case-insensitively.
    public void RegisterIgnored(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('@'))
            throw new ArgumentException("Ignored token must start with '@'.", nameof(command));
        _ignored.Add(command);
    }

    // Register a handler for a family of suffix-form commands sharing one prefix
    // — e.g. prefix @equip- matches @equip-fighting, @equip-tank, …. The text
    // after the prefix is folded in as the command's leading argument (Args[0]),
    // so the handler reads the dynamic part the same way an ordinary arg-bearing
    // command does. A prefix is consulted only when no exact RegisterHandler entry
    // matches, and only when the inbound command carries a non-empty remainder
    // after the prefix. prefix is verbatim including the leading @ and the
    // trailing separator (e.g. "@equip-"), matched case-insensitively; the
    // handler's Args[0] is the suffix.
    public void RegisterPrefixHandler(string prefix, PlayerRemoteControls requiredCategory, Action<RemoteCommandContext> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(handler);
        if (!prefix.StartsWith('@'))
            throw new ArgumentException("Remote-command prefix must start with '@'.", nameof(prefix));
        _prefixHandlers[prefix] = new Registration(requiredCategory, handler);
    }

    // Drop a previously-registered prefix handler. Idempotent — returns false
    // when nothing was registered.
    public bool UnregisterPrefixHandler(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return _prefixHandlers.TryRemove(prefix, out _);
    }

    // Total handlers currently registered. Useful for tests and the LogService
    // startup line.
    public int HandlerCount => _handlers.Count;

    // Test seam — drives the engine without going through ChatRouter.
    internal void DispatchForTests(ChatLogEntry entry) => OnChatEntry(entry);

    // Enumerate the catalog commands sender is permitted to issue, given their
    // merged per-player permission grant. Backs the @help handler's reply.
    // Party-whitelist commands (those mapped to PlayerRemoteControls.None — @wait
    // / @ok / @comeback / @share) are excluded: they're gated by party membership
    // alone, not an explicit permission flag, so they don't belong in a
    // per-permission command list. Reuses IsAuthorised so the answer always
    // matches what the engine would actually allow. Result follows the catalog's
    // grouped enumeration order.
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

        // Reserved announce tokens (party ailment sync, etc.) are consumed by
        // their owning subsystem — swallow silently so they never reach the
        // unknown-command denial path and bounce a reply at the sender.
        if (_ignored.Contains(command)) return;

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

    // Send a denial reply to the sender. specificReason — when non-null — wins
    // over the generic FailureMessage; null falls through to the configured
    // generic text. Always gated by WarnOnDenial: when off, ALL invalid / denial
    // replies are suppressed regardless of whether the reason is specific or
    // generic.
    //
    // Used by both the engine's own denial paths (unknown command, per-player
    // flag denial, party-whitelist denial — all use the generic FailureMessage)
    // and the suicide policy-block path (passes a specific reason). Handler-side
    // failure replies (e.g. SuicideHandler's invalid-password telepath) must
    // check WarnOnDenial themselves before invoking RemoteCommandContext.Reply.
    private void SendDenialReply(RemoteChannel channel, string sender, string? specificReason = null)
    {
        if (!WarnOnDenial) return;
        string text = specificReason ?? FailureMessage;
        if (string.IsNullOrWhiteSpace(text)) return;
        SendReply(channel, sender, text);
    }

    // Remote commands are accepted from every inbound chat channel EXCEPT the
    // realm-wide noise channels — Gossip (also carries auctions), Yell
    // (shout-style noise), system-level Broadcast / RealmEvent, and our own
    // outbound echo (TelepathOutgoing).
    private static RemoteChannel? MapChannel(ChatChannel c) => c switch
    {
        ChatChannel.TelepathIncoming => RemoteChannel.Telepath,
        ChatChannel.Gangpath         => RemoteChannel.Gangpath,
        ChatChannel.Local            => RemoteChannel.Local,
        _                            => null,
    };

    // Unconditional hard-blocks — never reply, never explain. These guard against
    // the most destructive misuse paths where any information leakage to the
    // caller is its own risk. Anything containing "reroll" as a whole token in
    // command or args is blocked because MajorMUD requires the literal word
    // reroll for the destructive action.
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

    // Forcible redirect for @do suicide and @party suicide: both are
    // unconditionally blocked even at full permissions, but unlike the silent
    // reroll hard-block they reply with a hint pointing the sender at the
    // dedicated @suicide handler (which has its own SysopCommands /
    // Elevated-Commands grant AND the lives-threshold policy gate).
    //
    // Forcible-death actions route exclusively through @suicide because that
    // handler:
    //   - requires the elevated SysopCommands permission tier, which is granted
    //     independently of the (much lower-trust) ExecuteCommands tier @do sits
    //     at;
    //   - respects the per-character MaxSuicideLivesThreshold;
    //   - uses the stored encrypted suicide-password blob autonomously via
    //     SuicideHandler, so the user's intent is captured at the moment they ran
    //     set suicide rather than inferred mid-session from a wire-sniff.
    //
    // Returns the reply payload (bare text — engine wraps in {} at SendReply
    // time) for the sender; null when the command isn't a forcible @do / @party
    // suicide attempt.
    private static string? GetForcedSuicideRedirectReply(string command, string[] args)
    {
        if (!ContainsToken(command, args, "suicide")) return null;
        bool isDo    = command.Equals("@do",    StringComparison.OrdinalIgnoreCase);
        bool isParty = command.Equals("@party", StringComparison.OrdinalIgnoreCase);
        if (!isDo && !isParty) return null;
        string verb = isDo ? "@do" : "@party";
        return $"{verb} suicide is not allowed, use @suicide";
    }

    // User-configured policy block for direct @suicide based on the lives
    // threshold. Distinct from IsHardBlocked because policy blocks SHOULD be
    // communicated back to the sender — the caller is typically a trusted party
    // member who needs to know why their command isn't firing (otherwise they'll
    // assume our @-command engine is broken). Returns the reply text for the
    // sender; null when the command isn't a suicide attempt or the threshold is
    // satisfied.
    //
    // Forcible @do / @party suicide variants are caught earlier by
    // GetForcedSuicideRedirectReply; by the time this runs the only
    // suicide-token-bearing command left is direct @suicide (or some future verb
    // that legitimately uses suicide as a sub-command).
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

    // Find a registered prefix handler whose prefix is a strict prefix of command
    // (non-empty remainder required). On a match, suffix is the trailing text —
    // the dynamic argument.
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

        // Health-query fallback — checking a member's HP/MA/lives is a
        // party social baseline: any active party member may ask, grant
        // or no grant. These (@health / @status / @lives) are pure
        // queries with no directive path, so DisallowPartyDirectives —
        // which gates only @party's action sub-commands — doesn't apply.
        // @party is excluded here because it keeps its own gated fallback
        // below.
        if (requiredCategory == PlayerRemoteControls.QueryHealthStatus
            && !command.Equals("@party", StringComparison.OrdinalIgnoreCase)
            && IsActivePartyMember(sender))
        {
            return true;
        }

        // @party fallback — base @party commands are always allowed inside an
        // active party regardless of per-player grants (it's the social
        // baseline of party play).
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

    // Default reply formatting per channel. The wire syntax matches MajorMUD's
    // standard commands; specific BBSes may vary and the format can be tuned by
    // replacing the wire-sender or providing a custom Reply callback. Bytes are
    // Latin-1 + trailing CR — same encoding the macro / trigger paths use because
    // BBSes expect 8-bit-clean bytes, not UTF-8.
    //
    // Telepath uses /<name> (slash + given name, no space) — the verbose t / tel
    // / tell forms are all interpreted as say on Playpen BBS (verified live).
    // Recipient is always the GIVEN name (first whitespace-delimited token of
    // ChatLogEntry.Speaker); MajorMUD doesn't accept "Given Family" as a telepath
    // recipient. Speaker as classified by ChatRouter is already single-word so
    // the given-name strip is a no-op for the engine but the rule's worth stating
    // for any future callers.
    //
    // Local (say) replies use the period say-precursor (`.<msg>`), not the `say`
    // verb: on this realm the keyboard period is the say precursor (confirmed
    // mechanic), and a command arriving on say must be answered back on say the
    // same way the player would. Every @-command reply routes through here, so
    // this one switch fixes the channel for all of them.
    //
    // Reply payload is encapsulated in { } braces — every remote-command response
    // carries the curly-brace meta-line convention so the recipient's terminal
    // can visually distinguish an engine-generated answer from in-character
    // speech. Handlers provide bare text; the engine adds the braces here, so
    // nothing upstream has to remember the convention.
    private void SendReply(RemoteChannel channel, string recipient, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string given = GivenName(recipient);
        string payload = $"{{{text}}}";
        string wire = channel switch
        {
            RemoteChannel.Telepath => $"/{given} {payload}",
            RemoteChannel.Gangpath => $"gang {payload}",
            RemoteChannel.Local    => $".{payload}",
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
