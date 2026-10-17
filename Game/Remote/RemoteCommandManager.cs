using System.Collections.Concurrent;
using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Extensible engine for MajorMUD @-commands. Subscribes to
// ChatRouter.EntryClassified, identifies @-prefixed messages, resolves the
// sender's per-player permission against the Players tab, enforces hard-blocks,
// and dispatches to the handler registered for the command.
//
// The engine registers no handlers itself — subsystems wire their own by calling
// RegisterHandler; the engine stays untouched.
//
// The order of checks on an inbound line (OnChatEntry). The first that stops a
// command ends it:
//   a. "Disallow all remote control commands" — nothing is read or answered.
//   b. The channel: telepath, gangpath, say or the numbered broadcast channel,
//      each with its own off switch. The broadcast channel's starts on: it is
//      public, so nothing is read there until the user opens it.
//   c. Our own echo, and anything that doesn't start with `@` (or `&@`).
//   d. Reserved words other subsystems own (ailment announces, @panic, sync
//      data) — swallowed.
//   e. The master switch: off, only @auto-all is followed; no answer.
//   f. Hard-blocks (1 below) — no answer, whatever the Warn setting says.
//   g. A word that is no command — no answer.
//   h. A path-only command (@dupe) said aloud or on the broadcast channel — no
//      answer.
//   h2. On the broadcast channel only, for a command and for a relay-back: a
//      speaker already answered there this minute, or a channel that has had
//      its answers for the minute, is passed over before anything is said or
//      run. Counted apart for speakers who hold the permission and for those
//      who do not (BroadcastAnswerBudget).
//   i. The permission (2 and 3 below). A sender without it gets the generic
//      failure message and nothing else: every check that answers with a reason
//      of its own comes after this one, so it can tell a stranger nothing.
//   j. The suicide rules (4 below), then the handler.
// Every answer to a command that was not carried out goes through
// SendDenialReply, which the Warn setting silences.
//
// Permission model:
//   1. Hard-blocks. Any command matching the reroll hard-block (the
//      substring "reroll" appears anywhere in the command name or args) is
//      blocked unconditionally — no per-player flag can override — and so is
//      `@party set suicide`.
//   2. Party whitelist. Handlers registered with PlayerRemoteControls.None as
//      the required category (@wait / @ok / @waiting / @comeback / @forget /
//      @share / @ptrain) are gated on whether the sender is an active party
//      member (per PartyState.Members), with eligibility bridges for a member
//      already dropped server-side (ComebackEligibility, ForgetEligibility,
//      PartyTrainEligibility).
//   3. Per-player flag check. The merged PlayerRecord.RemoteControls (BBS-tier
//      observation + Char-tier customisation) must include the handler's
//      required PlayerRemoteControls flag. Default for never-seen / un-customised
//      players is PlayerRemoteControls.None → every per-flag handler denies.
//      Users grant access via the Players tab edit dialog.
//   4. The suicide rules, for a sender who passed 2 or 3. `@do` with the word
//      `suicide` among what it would send is refused with a pointer at @suicide.
//      `@suicide` itself is refused when LivesProvider reports remaining lives ≤
//      MaxSuicideLivesThreshold; a null LivesProvider is lives unknown, and
//      blocks — the safer default. Both match the exact command words: a word
//      that only contains "suicide" is neither.
//
// Relay-back: `&@<command> [args]` asks us to send `@<command> [args]` back to its
// sender (see HandleRelayBack), so the sender's own client runs it as if we'd sent it.
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
    private bool _disposed;

    // Live lives provider. Returns the current Lives count from the most recent
    // stat-screen parse, or null when no stat screen has been observed yet (so
    // the hard-block defaults to blocked rather than trusting a stale value).
    // Wired in AppServices as
    // () => Stats.HasParsed ? PlayerStats.Lives : (int?)null.
    public Func<int?>? LivesProvider { get; set; }

    // Block @suicide when remaining lives are at or below this threshold. Default
    // 5; settable to 0 in Settings.Other to allow forced suicide through all
    // lives. Max lives in MajorMUD is 9, so the UI clamps this to 0..9.
    public int MaxSuicideLivesThreshold { get; set; } = 5;

    // The local character's own name. Public channels (gangpath / gossip / auction /
    // broadcast) echo the player's OWN line back tagged with their character name — never
    // "You" — so the null / "You" self-echo guards below can't catch a self-echo on those
    // channels. The engine listens on Gangpath, so without this a player's own gangpath'd
    // @-command is processed as if a stranger sent it (and, lacking a self-grant, bounces
    // a denial to the whole gang). Wired in AppServices to Party.LocalCharacterName; null
    // until the name is known (pre-first-stat-parse), where the null/"You" guards still
    // cover say / telepath.
    public Func<string?>? SelfNameProvider { get; set; }

    // True while we're sneaking or hidden. A reply to a command said aloud would be a
    // directed say, and saying anything ends a sneak (GAME_MECHANICS "Sneaking —
    // commands, equip order, and the sneak state machine", its What ends a sneak
    // list), so it goes back by telepath instead (user, 2026-09-28).
    public Func<bool>? StealthedProvider { get; set; }

    // The master switch. Asked with a description of the command; true means the
    // switch is off and the command must not be obeyed (AutoModeController.Blocks
    // counts and logs the skip). With it off no remote command is followed except
    // MasterSwitchCommand, which is how a party member switches it back on (user,
    // 2026-10-09). A skipped command gets no reply at all, WarnOnDenial or not:
    // like a hard-block, an answer would let any caller probe whether we are
    // unattended.
    public Func<string, bool>? BlockedByMasterSwitch { get; set; }

    public const string MasterSwitchCommand = "@auto-all";

    // Senders already told about (in the log) since the switch went off, so the
    // Info line is written once each and a chatty party can't flood it.
    private readonly HashSet<string> _masterOffNoticed = new(StringComparer.OrdinalIgnoreCase);

    // ----- Settings.Talk-driven knobs --------------------------------------
    // Pushed by TalkSectionViewModel.ApplyToServices on Apply / on profile
    // load. Defaults match the TalkSettings DTO defaults — anything not yet
    // wired up has permissive defaults that don't change behaviour.

    // Hard kill-switch above every per-channel + per-player permission. When
    // true the engine ignores every inbound @-command. Pushed from
    // TalkSettings.DisallowAllRemoteCommands.
    public bool MasterDisable { get; set; }

    // Disallows the @party <command> directive path. When true, no member's
    // @party <command> is relayed, whatever they are granted, and a member without a
    // QueryHealthStatus grant also loses the free answer to a bare @party. Does NOT
    // affect the rest of the party-whitelist (@wait / @ok / @comeback / @share …) —
    // those stay allowed for active members regardless. Pushed from
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

    // Leader- and follower-side eligibility hook for a @forget teardown. Like
    // @comeback, a @forget can travel between two parties who no longer share a
    // server-side party row: a follower dropped on reconnect (WasRecentlyPartied
    // on the leader) or a leader we still remember across our own drop
    // (PartyRejoinCoordinator.IsRememberedLeader on the follower). AppServices
    // wires this to the OR of those two predicates. Consulted ONLY for @forget
    // (see IsAuthorised); null = no extra allowance, so the plain party-whitelist
    // gate stands.
    public Func<string, bool>? ForgetEligibility { get; set; }

    // Follower-side eligibility hook for `@ptrain`: the leader whose party train
    // trip we set out on. A follower turned away at an exit on the way is out of
    // that leader's party on our own side too, and still has to be told the trip
    // is over. Consulted ONLY for @ptrain (see IsAuthorised).
    public Func<string, bool>? PartyTrainEligibility { get; set; }

    // Drop @-commands arriving on the Telepath channel.
    public bool DisableTelepathChannel { get; set; }

    // Drop @-commands arriving on the Gangpath channel.
    public bool DisableGangpathChannel { get; set; }

    // Drop @-commands arriving on the Local say channel.
    public bool DisableLocalChannel { get; set; }

    // Drop @-commands arriving on the numbered broadcast channel. Off-limits unless
    // the user opens it (user, 2026-10-11: "it should be defaulted disallowed"): the
    // channel is public, anyone can join one. The asking side of a transfer keeps
    // to it too: with this set, nothing heard on that channel is taken either.
    public bool DisableBroadcastChannel { get; set; } = true;

    // When true, a command that was refused or couldn't be carried out is
    // answered: FailureMessage on a per-player or party-whitelist denial, the
    // handler's own reason otherwise (RemoteCommandContext.Refuse). When false,
    // none of those is sent. A word that is no command, the hard-blocks and the
    // user-disabled paths (master / per-channel) stay silent regardless. Pushed
    // from TalkSettings.WarnOnInvalidRemoteCommand.
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
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(players);
        _chat = chat;
        _party = party;
        _players = players;
        _log = log;
        _chat.EntryClassified += OnChatEntry;
        _party.Members.CollectionChanged += OnPartyMembersChanged;
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
    // (allowed for any active party member regardless of per-player flags — the
    // @wait / @ok / @comeback family). command is verbatim including the leading @, matched
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

    // Outcome of TryInvokeLocal, so the caller can map a refusal to the right
    // status rather than guessing from a bool.
    public enum LocalInvokeResult
    {
        Ok,
        // No handler registered for the command.
        UnknownCommand,
        // The whole remote-command engine is switched off.
        Disabled,
        // An unconditional hard-block (reroll, `@party set suicide`) — denied for
        // anyone, by any route.
        HardBlocked,
        // A command that only means something coming from another player over
        // telepath / gangpath (@dupe), so it can't be driven locally.
        PathChannelOnly,
        // The master switch is off: no remote command but MasterSwitchCommand is
        // followed, by this route as by chat.
        MasterSwitchOff,
    }

    // Run a registered @-command from the LOCAL machine rather than from a chat
    // channel, collecting whatever the handler would have replied.
    //
    // Deliberately skips the per-player PlayerRemoteControls check. That gate
    // answers "may this OTHER PLAYER, over chat, do this to me?" — a question with
    // no meaning here: the caller already has the machine and the client. What it
    // does NOT skip is MasterDisable (the user's own off switch for the whole
    // subsystem) or the hard-blocks, which are absolute rather than
    // permission-shaped. Anything further — whether a destructive command is
    // allowed at all — is the API layer's decision, not this one's.
    //
    // Replies arrive through the collector instead of being sent to a channel, so
    // invoking @health locally answers the caller and puts nothing on the wire.
    public LocalInvokeResult TryInvokeLocal(
        string command, IReadOnlyList<string>? args, Action<string> reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(reply);
        if (MasterDisable) return LocalInvokeResult.Disabled;

        string normalised = command.Trim().ToLowerInvariant();
        if (!normalised.StartsWith('@')) normalised = "@" + normalised;
        normalised = RemoteCommandCatalog.WithoutEmphasis(normalised);
        string[] argv = args?.ToArray() ?? [];

        if (IsHardBlocked(normalised, argv, out string? blockReason))
        {
            _log?.Log(LogSeverity.Warn, "RemoteCmd",
                $"Local invocation of {normalised} denied — {blockReason}.");
            return LocalInvokeResult.HardBlocked;
        }

        if (RemoteCommandCatalog.IsPathChannelOnly(normalised))
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Local invocation of {normalised} refused — telepath / gangpath only.");
            return LocalInvokeResult.PathChannelOnly;
        }

        if (!_handlers.TryGetValue(normalised, out Registration registration))
        {
            if (!TryMatchPrefixHandler(normalised, out registration, out string suffix))
                return LocalInvokeResult.UnknownCommand;
            argv = Prepend(suffix, argv);
        }

        // The rule is about remote commands, not about who sends them ("no remote
        // command except @auto-all on or off should work"; user, 2026-10-09), so
        // it holds here too. Unlike chat, the caller is told why: it already has
        // the machine, so there is nothing to keep from it.
        if (!normalised.Equals(MasterSwitchCommand, StringComparison.OrdinalIgnoreCase)
            && SkippedForMasterSwitch(LocalSenderName, normalised))
            return LocalInvokeResult.MasterSwitchOff;

        // Sender is a literal rather than a player name: handlers use it for reply
        // addressing and logging, and naming the origin keeps a locally-driven
        // action distinguishable from a party member's in the log.
        RemoteCommandContext ctx = new(
            Sender:          LocalSenderName,
            Command:         normalised,
            Args:            argv,
            OriginalMessage: string.Join(' ', new[] { normalised }.Concat(argv)),
            Channel:         RemoteChannel.Telepath,
            Reply:           reply)
        {
            // The caller has the machine: it is told why, whatever the Warn
            // setting says to other players.
            Refusal = why => reply(string.IsNullOrEmpty(why) ? FailureMessage : why),
        };

        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"Local invocation: {normalised}{(argv.Length > 0 ? " " + string.Join(' ', argv) : "")}");
        try { registration.Handler(ctx); }
        catch (Exception ex)
        {
            // Same policy as the chat path: a throwing handler must not tear the
            // engine down. Surfaced to the caller so an API response isn't a
            // silent success.
            _log?.Log(LogSeverity.Warn, "RemoteCmd",
                $"Handler for {normalised} threw on local invocation: {ex.Message}");
            reply($"command failed: {ex.Message}");
        }
        return LocalInvokeResult.Ok;
    }

    // Sender name stamped on locally-invoked commands. Not a real player, and
    // chosen to be obvious in a log line.
    public const string LocalSenderName = "(local api)";

    // Enumerate the catalog commands sender is permitted to issue, given their
    // merged per-player permission grant. Backs the @help handler's reply.
    // Party-whitelist commands (those mapped to PlayerRemoteControls.None — @wait
    // / @ok / @waiting / @comeback / @forget / @share / @ptrain) are excluded:
    // they're gated by party membership
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
        if (IsAuthorised(sender, RelayBackCategory, RelayBackPrefix))
            permitted.Add(RelayBackPrefix + "<command>");
        return permitted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
        _party.Members.CollectionChanged -= OnPartyMembersChanged;
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
        bool relayBack = entry.Message.StartsWith(RelayBackPrefix, StringComparison.Ordinal);
        if (!relayBack && entry.Message[0] != '@') return;   // Not an @-command.
        // Public-channel self-echo: a gangpath (also gossip / auction / broadcast) echoes
        // our OWN line back tagged with our character name, so it slipped past the null /
        // "You" guards above. We never issue remote commands to ourselves, so skip it —
        // otherwise our own @timer-sync / @party broadcast bounces a denial at the gang.
        if (IsSelfEcho(entry.Speaker))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Skipping own {entry.Channel} echo of {entry.Message} (self).");
            return;
        }

        if (relayBack)
        {
            // A relay-back goes out on the channel it was asked on, the broadcast
            // channel included (user, 2026-10-11: "if a remote command was issued on
            // a channel, it should respond on that channel except for the sneak/say
            // case"). The echo is a line said there, so HandleRelayBack holds it to
            // the channel's bound like any answer.
            HandleRelayBack(entry.Speaker, entry.Message, channel.Value);
            return;
        }

        // Parse: command = first whitespace token (the registry lookup ignores
        // case), less a trailing `!` (`@stop!` is `@stop`, as the catalog reads
        // it); args = remaining tokens.
        string[] tokens = entry.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;
        string command = RemoteCommandCatalog.WithoutEmphasis(tokens[0]);
        string[] args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        // Reserved announce tokens (party ailment sync, etc.) are consumed by
        // their owning subsystem — swallow silently so they never reach the
        // unknown-command denial path and bounce a reply at the sender.
        if (_ignored.Contains(command)) return;

        // With its box unticked the broadcast channel is read like the other three
        // (user, 2026-10-11): every remote command, under every gate below, answered
        // on the channel ("refusals should be sent on the same channel received").
        // The transfers keep to their own channels there as anywhere: one of the
        // three may go by it, and the other two are answered with where they do work.
        //
        // What differs is how much is said there (BroadcastAnswerBudget), which is
        // settled below, once it is known whether the speaker holds the permission.

        // Ahead of every block and denial below, so nothing here answers while
        // the switch is off. Only a command this client knows: ordinary chat that
        // starts with '@' is not a command we declined.
        bool known = _handlers.ContainsKey(command) || TryMatchPrefixHandler(command, out _, out _);
        if (known
            && !command.Equals(MasterSwitchCommand, StringComparison.OrdinalIgnoreCase)
            && !IsRecordedWhileMasterSwitchOff(command)
            && SkippedForMasterSwitch(entry.Speaker, command))
        {
            // A stranded member asks once. Keep the latest ask from each, so it
            // can be answered when the switch is back on (ReplayHeldComebacks).
            // Only from a sender we would go back for now: an ask kept from
            // anyone else would be answered with a denial when the switch came
            // on, the reply the off state exists not to give.
            if (command.Equals("@comeback", StringComparison.OrdinalIgnoreCase)
                && _handlers.TryGetValue(command, out Registration comeback)
                && IsAuthorised(entry.Speaker, comeback.RequiredCategory, command))
                _heldComebacks[entry.Speaker] =
                    new HeldComeback(entry.Speaker, entry, _now(), IsActivePartyMember(entry.Speaker));
            return;
        }

        // @help is a pure query — describing a command isn't executing it — so it's
        // exempt from the hard-block below, which scans the args for words meant
        // for action commands (@do reroll, @party set suicide).
        bool isHelp = string.Equals(command, "@help", StringComparison.OrdinalIgnoreCase);

        // Hard-blocks first — bypass everything else. Always silent (no
        // reply) even when WarnOnDenial is on: never advertise the block
        // to a malicious caller.
        if (!isHelp && IsHardBlocked(command, args, out string? reason))
        {
            _log?.Log(SilentBlockSeverity(channel.Value), "RemoteCmd",
                $"Blocked {command} from {entry.Speaker}: {reason}");
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
                // Unknown @-command — no handler registered. Stay SILENT: an
                // @-token that matches no command is almost always ordinary chat
                // that merely starts with '@' (someone said "@because" in gang
                // chat), so a reply would bounce noise at the channel and advertise
                // the client. WarnOnInvalidRemoteCommand still governs the denial
                // reply for a KNOWN command the sender lacks permission for (just
                // below) — a real command attempt, not a stray word.
                _log?.Log(LogSeverity.Debug, "RemoteCmd",
                    $"Ignoring unknown command {command} from {entry.Speaker} (no reply).");
                return;
            }
        }

        // A command that hands out trust is accepted only over the path channels.
        // Say is room-wide and the broadcast channel is open to anyone who joins
        // it, so a @dupe on either is dropped outright — no denial reply, which
        // would only announce the command to everyone there.
        if (channel.Value is RemoteChannel.Local or RemoteChannel.Broadcast
            && RemoteCommandCatalog.IsPathChannelOnly(command))
        {
            _log?.Log(SilentBlockSeverity(channel.Value), "RemoteCmd",
                $"Ignoring {command} from {entry.Speaker} on {TransferChannels.NameOf(channel.Value)} — telepath / gangpath only.");
            return;
        }

        // Authorisation: party-whitelist OR per-player flag.
        bool authorised = IsAuthorised(entry.Speaker, registration.RequiredCategory, command);

        // On the broadcast channel a speaker already answered this minute, or a
        // channel that has had its answers for the minute, is passed over here,
        // before anything is said or run for the line: a command whose answer would
        // be held back is not carried out. Refusals to players without the
        // permission and answers to players with it are counted apart, so players
        // nobody granted anything cannot use up a granted player's turn.
        if (!OpenBroadcastAnswer(channel.Value, entry.Speaker, command, authorised, out BroadcastAnswer? answer)) return;

        if (!authorised)
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Denied {command} from {entry.Speaker} on {TransferChannels.NameOf(channel.Value)} (lacks {registration.RequiredCategory}).");
            // Only the commands let through with the master switch off reach here
            // then, and they must not be the ones that answer a stranger: no
            // denial either.
            if (BlockedByMasterSwitch?.Invoke($"{command} from {entry.Speaker} (not granted)") == true)
                return;
            SendDenialReply(channel.Value, entry.Speaker, answer: answer);
            return;
        }

        // The two suicide rules, for a sender who holds the command's permission.
        // They come after the permission check so that a sender without it learns
        // nothing here: the lives rule's answer names our lives count. Both answer
        // through SendDenialReply, so the Warn setting silences them.
        //
        // `@do … suicide` is refused whatever our lives are, with a pointer at
        // @suicide, which has the elevated permission, the lives rule and the
        // stored password. It is checked first: the lives rule lets a command
        // through when lives are above the number set.
        string? forcedSuicideRedirect = GetForcedSuicideRedirectReply(command, args);
        if (forcedSuicideRedirect is not null)
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Forcible-suicide blocked from {entry.Speaker}: {forcedSuicideRedirect}");
            SendDenialReply(channel.Value, entry.Speaker, specificReason: forcedSuicideRedirect, answer: answer);
            return;
        }

        string? suicideReply = GetSuicidePolicyBlockReply(command);
        if (suicideReply is not null)
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Suicide policy-blocked from {entry.Speaker}: {suicideReply}");
            SendDenialReply(channel.Value, entry.Speaker, specificReason: suicideReply, answer: answer);
            return;
        }

        // The channel is public: what is carried out for a line said on it is on
        // record, with who asked.
        if (channel.Value == RemoteChannel.Broadcast)
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Following {command} from {entry.Speaker}, said on the broadcast channel.");

        // Invoke. Engine supplies the Reply callback that routes back via
        // the same channel. On the broadcast channel both callbacks carry this
        // line's answer, so a result the handler gives later is still part of it.
        RemoteCommandContext ctx = new(
            Sender:          entry.Speaker,
            Command:         command,
            Args:            args,
            OriginalMessage: entry.Message,
            Channel:         channel.Value,
            Reply:           text => SendReply(channel.Value, entry.Speaker, text, answer))
        {
            Refusal = why => SendDenialReply(channel.Value, entry.Speaker, specificReason: why, answer: answer),
        };
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

    // `&@<command> [args]` — "send this remote command to me". We echo the @-command
    // back to its sender on the channel it arrived on, so the sender's client runs it
    // under the sender's own grants for us: a leader's `&@invite` makes the member ask
    // to be invited. Gated at the @do tier, since `@do /<sender> @<command>` could already
    // put the same line on the wire. Only a command this client knows is relayed — the
    // echo is never arbitrary text — and a nested `&@&@…` isn't a known command, so it
    // can't ping-pong. The reroll / set-suicide hard-blocks apply to the payload too.
    private void HandleRelayBack(string sender, string message, RemoteChannel channel)
    {
        string payload = message[1..].Trim();
        string[] tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0].Length < 2) return;   // a bare "&@"
        // Looked up as the dispatcher would read it; sent back as it was typed.
        string command = RemoteCommandCatalog.WithoutEmphasis(tokens[0]);
        string[] args = tokens[1..];

        if (IsHardBlocked(command, args, out string? reason))
        {
            _log?.Log(SilentBlockSeverity(channel), "RemoteCmd",
                $"Blocked relay-back of {command} for {sender}: {reason}");
            return;
        }
        if (!_handlers.ContainsKey(command) && !TryMatchPrefixHandler(command, out _, out _))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Ignoring relay-back of unknown command {command} from {sender} (no reply).");
            return;
        }
        // A command taken on telepath and gangpath only (@dupe) is not put on a
        // channel by us either: relayed, it would go out as our own command on
        // whatever channel the asker chose. Dropped without a word, like the
        // command itself where it is not taken.
        if (RemoteCommandCatalog.IsPathChannelOnly(command))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Ignoring relay-back of {command} from {sender}: it is taken by telepath / gangpath only and is not relayed (no reply).");
            return;
        }
        // The broadcast channel shows us our own line on some boards, under our own
        // name. Until that name is known the echo of what we relayed can't be told
        // from a stranger's command, so nothing is relayed there yet.
        if (channel == RemoteChannel.Broadcast && string.IsNullOrEmpty(SelfNameProvider?.Invoke()))
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Ignoring relay-back of {command} from {sender} on the broadcast channel: our own name is not known yet, so our own echo there could not be told apart (no reply).");
            return;
        }
        // A relay-back makes us send, so it is a remote command like any other:
        // `&@auto-all` included, which asks us to switch the SENDER, not ourselves.
        if (SkippedForMasterSwitch(sender, "&" + command)) return;
        bool granted = IsAuthorised(sender, RelayBackCategory, RelayBackPrefix);
        // The echo, or the denial, is a line said on the channel: on the broadcast
        // channel it is one of the minute's answers, and a relay whose line would be
        // held back sends nothing.
        if (!OpenBroadcastAnswer(channel, sender, "&" + command, granted, out BroadcastAnswer? answer)) return;
        if (!granted)
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Denied relay-back of {command} for {sender} (lacks {RelayBackCategory}).");
            SendDenialReply(channel, sender, answer: answer);
            return;
        }
        // What `@do` may not send for a player, a relay may not send for them
        // either: the words go out as ours all the same (`&@suicide` as our
        // `@suicide` to whoever hears it, `&@do suicide` as our `@do suicide`). The
        // same test, over every word of the payload, with the same reply policy:
        // after the grant, so a sender without it learns nothing, and by the Warn
        // setting.
        if (SendsSuicide(tokens))
        {
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Relay-back of {command} for {sender} refused: suicide is not relayed.");
            SendDenialReply(channel, sender, specificReason: "suicide is not relayed", answer: answer);
            return;
        }

        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"Relaying {payload} back to {sender} on {channel} (&@ request).");
        SendLine(channel, sender, payload, answer);
    }

    // On the broadcast channel: whether a line from speaker may be answered now,
    // asked before anything is said or done for it. False, with the reason logged,
    // when the speaker was answered there this minute or the channel has had its
    // answers for the minute; the line is then passed over whole. answer is what
    // the replies to the line are counted as. On any other channel there is no
    // bound: true, and no answer to count.
    //
    // granted: the speaker holds the permission for what they sent. The two kinds
    // are bounded apart, each to one line of theirs a minute for a speaker and
    // BroadcastAnswerBudget.AnswersPerWindow speakers a minute: what is said to a
    // granted speaker (their command's answer, a refusal with a reason included),
    // and the Failure message to one who is not. A channel full of players nobody
    // granted anything draws its few refusals and costs a granted player nothing.
    private bool OpenBroadcastAnswer(
        RemoteChannel channel, string speaker, string what, bool granted, out BroadcastAnswer? answer)
    {
        answer = null;
        if (channel != RemoteChannel.Broadcast) return true;
        BroadcastAnswerBudget budget = granted ? _broadcastAnswers : _broadcastRefusals;
        if (WhyNotOnBroadcast(budget, speaker, _now()) is { } spent)
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Passing over {what} from {speaker} on the broadcast channel: {spent}.");
            return false;
        }
        answer = new BroadcastAnswer(budget);
        return true;
    }

    // Why speaker would not be answered now out of budget, or null. A speaker has
    // one answer a minute whichever of the two kinds it was: one who holds some
    // permissions and not others does not draw an answer and a refusal. The few
    // speakers a minute are each kind's own.
    private string? WhyNotOnBroadcast(BroadcastAnswerBudget budget, string speaker, DateTimeOffset now)
    {
        BroadcastAnswerBudget other = ReferenceEquals(budget, _broadcastAnswers) ? _broadcastRefusals : _broadcastAnswers;
        return other.AnsweredLately(speaker, now) ? BroadcastAnswerBudget.AnsweredLatelyReason : budget.WhyNot(speaker, now);
    }

    // A block that never answers is on record at Info where its sender had to reach
    // us by telepath, in our gang or in our room. On the broadcast channel anyone
    // who has joined can say such a line as often as they like, each one a line of
    // the program log: Debug there.
    private static LogSeverity SilentBlockSeverity(RemoteChannel channel) =>
        channel == RemoteChannel.Broadcast ? LogSeverity.Debug : LogSeverity.Info;

    // A `@comeback` dropped for the master switch. FromMember: the sender was in
    // the party when they asked (not a member who had already dropped out of it).
    private readonly record struct HeldComeback(string Sender, ChatLogEntry Entry, DateTimeOffset At, bool FromMember);

    // The latest one from each sender.
    private readonly Dictionary<string, HeldComeback> _heldComebacks = new(StringComparer.OrdinalIgnoreCase);

    // "If leading, accept @comeback for" (Settings → Party): an ask kept longer
    // than this is about a place the party has long left.
    public TimeSpan ComebackWindow { get; set; } = TimeSpan.FromMinutes(2);

    private readonly Func<DateTimeOffset> _now;

    // For the bug report: whose ask is kept.
    public IReadOnlyCollection<string> HeldComebackSenders => _heldComebacks.Keys.ToList();

    // The asks kept for the master switch belong to the stay, the character and
    // the party they were made in: a profile load or a dropped connection ends
    // all three.
    public void DropHeldComebacks(string why)
    {
        if (_heldComebacks.Count == 0) return;
        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"Dropping the @comeback kept from {string.Join(", ", _heldComebacks.Keys)} for the master switch — {why}.");
        _heldComebacks.Clear();
    }

    // A member who asked and has since left the party is not gone back for.
    private void OnPartyMembersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_heldComebacks.Count == 0) return;
        foreach (string speaker in _heldComebacks.Keys.ToList())
        {
            if (!_heldComebacks[speaker].FromMember || IsActivePartyMember(speaker)) continue;
            _heldComebacks.Remove(speaker);
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Dropping the @comeback kept from {speaker} for the master switch — they left the party.");
        }
    }

    // The master switch is back on: a member who asked to be come back for while
    // it was off is still where they were left, and asks only once. Their ask is
    // put through now, if it is still fresh and still from someone we would go
    // back for; one that is not is dropped without a reply.
    public void ReplayHeldComebacks()
    {
        if (_heldComebacks.Count == 0) return;
        List<HeldComeback> held = _heldComebacks.Values.ToList();
        _heldComebacks.Clear();
        _handlers.TryGetValue("@comeback", out Registration comeback);
        foreach ((string sender, ChatLogEntry entry, DateTimeOffset at, _) in held)
        {
            TimeSpan age = _now() - at;
            if (age > ComebackWindow)
            {
                _log?.Log(LogSeverity.Info, "RemoteCmd",
                    $"Master switch back on — the {entry.Message} from {sender} is {age.TotalMinutes:0.#} min old, past the {ComebackWindow.TotalMinutes:0} min @comeback window: dropped, no reply.");
                continue;
            }
            if (!IsAuthorised(sender, comeback.RequiredCategory, "@comeback"))
            {
                _log?.Log(LogSeverity.Info, "RemoteCmd",
                    $"Master switch back on — {sender} is no longer one we go back for: their {entry.Message} is dropped, no reply.");
                continue;
            }
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Master switch back on — answering the {entry.Message} {sender} sent while it was off.");
            OnChatEntry(entry);
        }
    }

    // `@wait` and `@ok` are not followed with the master switch off, but what a
    // follower last said is still taken down: their handlers only update who is
    // waiting, answer nothing, and the hold that set would raise is parked. If
    // they were dropped, a leader switching back on would walk off from a
    // follower who had asked to wait, or stand out the whole wait limit for one
    // who had since said `@ok`.
    private static bool IsRecordedWhileMasterSwitchOff(string command) =>
        command.Equals("@wait", StringComparison.OrdinalIgnoreCase)
        || command.Equals("@ok", StringComparison.OrdinalIgnoreCase);

    // True when the master switch is off and this command is therefore dropped,
    // silently. The first one from each sender is logged at Info; the rest only
    // at Debug, by the switch itself.
    private bool SkippedForMasterSwitch(string sender, string command)
    {
        if (BlockedByMasterSwitch is not { } blocked) return false;
        if (!blocked($"{command} from {sender}"))
        {
            _masterOffNoticed.Clear();
            return false;
        }
        if (_masterOffNoticed.Add(sender))
            _log?.Log(LogSeverity.Info, "RemoteCmd",
                $"Ignoring {command} from {sender}: the master switch is off, so no remote command is followed except {MasterSwitchCommand} (no reply sent; further commands from {sender} are logged at Debug).");
        return true;
    }

    // The relay-back marker and the grant it needs (see HandleRelayBack).
    public const string RelayBackPrefix = "&@";
    public const PlayerRemoteControls RelayBackCategory = PlayerRemoteControls.ExecuteCommands;

    private bool IsChannelDisabled(RemoteChannel c) => c switch
    {
        RemoteChannel.Telepath => DisableTelepathChannel,
        RemoteChannel.Gangpath => DisableGangpathChannel,
        RemoteChannel.Local    => DisableLocalChannel,
        RemoteChannel.Broadcast => DisableBroadcastChannel,
        _                      => false,
    };

    // Send a denial reply to the sender. specificReason — when non-null — wins
    // over the generic FailureMessage; null falls through to the configured
    // generic text. Always gated by WarnOnDenial: when off, ALL invalid / denial
    // replies are suppressed regardless of whether the reason is specific or
    // generic.
    //
    // Used by the engine's own denial paths (per-player flag denial,
    // party-whitelist denial — the generic FailureMessage), by the suicide rules
    // (a specific reason), and by every handler: RemoteCommandContext.Refuse is
    // bound to it, so a handler's failure answer is gated here and nowhere else.
    private void SendDenialReply(
        RemoteChannel channel, string sender, string? specificReason = null, BroadcastAnswer? answer = null)
    {
        if (!WarnOnDenial) return;
        string text = specificReason ?? FailureMessage;
        if (string.IsNullOrWhiteSpace(text)) return;
        SendReply(channel, sender, text, answer);
    }

    // Tell a player what became of something they sent because the user here asked
    // for it: a loop taken with `@loop send`. It answers no command of theirs, so no
    // handler's context carries it, but it leaves the way every reply does (braced,
    // plain ASCII), and by telepath: a loop goes by telepath only, and so does what
    // is said about it. Returns whether it was sent.
    //
    // What holds it back:
    // - The master switch. With it off nothing goes out by itself, this included.
    //   The loop is still taken: the user typed the yes (decided 2026-10-11).
    // - failed, with the Warn setting off. "It did not arrive, send again" is a
    //   failure answer and follows that setting like every other.
    // An answer that says what was done with what the user accepted (saved, or
    // already held) is not a refusal and the Warn setting does not silence it.
    // Neither the "disallow all remote commands" setting nor a channel's own off
    // switch applies: those stop players' commands from being obeyed, and this is
    // the end of something the user started.
    public bool SendOutcome(string recipient, string text, bool failed)
    {
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(text)) return false;
        if (BlockedByMasterSwitch?.Invoke($"answer to {recipient}: {text}") == true) return false;
        if (failed)
        {
            if (!WarnOnDenial) return false;
            SendDenialReply(RemoteChannel.Telepath, recipient, specificReason: text);
            return true;
        }
        SendReply(RemoteChannel.Telepath, recipient, text);
        return true;
    }

    // Remote commands are accepted from every inbound chat channel EXCEPT the
    // realm-wide noise channels — Gossip (also carries auctions), Yell
    // (shout-style noise), system-level RealmEvent, and our own outbound echo
    // (TelepathOutgoing). The numbered broadcast channel is taken in only once the
    // user opens it (DisableBroadcastChannel).
    private static RemoteChannel? MapChannel(ChatChannel c) => c switch
    {
        ChatChannel.TelepathIncoming => RemoteChannel.Telepath,
        ChatChannel.Gangpath         => RemoteChannel.Gangpath,
        ChatChannel.Local            => RemoteChannel.Local,
        ChatChannel.Broadcast        => RemoteChannel.Broadcast,
        _                            => null,
    };

    // Unconditional hard-blocks — never reply, never explain. These guard against
    // the most destructive misuse paths where any information leakage to the
    // caller is its own risk. Anything with "reroll" anywhere in the command or
    // an argument is blocked, part of a longer word included: the block is silent
    // and costs a caller nothing, so it errs wide where the suicide rules, which
    // answer, match the exact word.
    private bool IsHardBlocked(string command, string[] args, out string? reason)
    {
        // Catches a direct @reroll (unknown command), @do reroll, @party reroll,
        // and any other passthrough verb's reroll arg.
        if (ContainsToken(command, args, "reroll"))
        {
            reason = "reroll hard-block (always denied)";
            return true;
        }
        // @party is a party-membership-gated passthrough (the @do analogue). Per
        // user policy its ONLY forbidden payloads are the `set suicide` phrase and
        // reroll (caught above); everything else relays, including a plain
        // `suicide`. `set suicide` is two adjacent tokens, so match the phrase
        // rather than the lone "suicide" token the @do redirect uses. Silent like
        // reroll — never advertise the block to a caller probing for it.
        if (command.Equals("@party", StringComparison.OrdinalIgnoreCase)
            && ContainsAdjacent(args, "set", "suicide"))
        {
            reason = "@party set-suicide hard-block (always denied)";
            return true;
        }
        reason = null;
        return false;
    }

    // Forcible redirect for @do suicide: refused even at full permissions, but
    // unlike the silent reroll hard-block it replies with a hint pointing the
    // sender at the dedicated @suicide handler (which has its own SysopCommands /
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
    // time) for the sender; null when the command isn't a forcible @do suicide.
    //
    // Only @do, and only when one of the words it would send is `suicide` itself.
    // Punctuation and quotes around the word don't hide it (`suicide!`,
    // `"suicide"`, `suicide;`): the game may well take those as the command. A
    // longer word that merely contains it (`suicidal`) is not the command.
    // @party is a passthrough whose suicide policy is enforced entirely by
    // IsHardBlocked (only the `set suicide` phrase is refused); a plain
    // @party suicide relays like any other command, so it must NOT redirect.
    private static string? GetForcedSuicideRedirectReply(string command, string[] args)
    {
        if (!command.Equals("@do", StringComparison.OrdinalIgnoreCase)) return null;
        return SendsSuicide(args) ? "@do suicide is not allowed, use @suicide" : null;
    }

    // The test of the rule above, for whatever words this client is asked to put
    // on the wire for another player: `@do`'s, and a relay-back's payload. Each word
    // is read as it goes out as well, after WireText: a letter outside ASCII leaves
    // as `?`, so `suicideé` would reach the game as `suicide?`.
    private static bool SendsSuicide(IEnumerable<string> words)
    {
        foreach (string word in words)
            if (HasLetterRun(word, "suicide") || HasLetterRun(WireText.Ascii(word), "suicide"))
                return true;
        return false;
    }

    // True when one of the unbroken runs of letters in text is word, ignoring case.
    private static bool HasLetterRun(string text, string word)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool letter = i < text.Length && char.IsLetter(text[i]);
            if (letter && start < 0) start = i;
            if (letter || start < 0) continue;
            if (i - start == word.Length
                && string.Compare(text, start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            start = -1;
        }
        return false;
    }

    // User-configured policy block for @suicide based on the lives threshold.
    // Distinct from IsHardBlocked because policy blocks SHOULD be communicated
    // back to the sender — the caller holds the Elevated Commands permission and
    // needs to know why the command isn't firing (otherwise they'll assume our
    // @-command engine is broken). Returns the reply text for the sender; null
    // when the command isn't @suicide or the threshold is satisfied.
    //
    // The rule is @suicide's own. No other command is held to it for having
    // `suicide` among its words: @do is refused outright above, and @party's
    // suicide policy lives entirely in IsHardBlocked.
    private string? GetSuicidePolicyBlockReply(string command)
    {
        if (!command.Equals("@suicide", StringComparison.OrdinalIgnoreCase)) return null;
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

    // Whole-phrase match: true when args contains first immediately followed by
    // second (case-insensitive). Used for the `set suicide` @party hard-block,
    // where the two adjacent tokens carry the meaning a lone "suicide" token
    // doesn't — a bare `suicide` is a permitted @party passthrough.
    private static bool ContainsAdjacent(string[] args, string first, string second)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(first, StringComparison.OrdinalIgnoreCase)
                && args[i + 1].Equals(second, StringComparison.OrdinalIgnoreCase))
                return true;
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
        // allowed for any active party member": @wait, @ok, @waiting,
        // @comeback, @forget, @share, @ptrain. These are NOT affected by Disallow @party
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
            // @forget mirrors @comeback: both ends of a reconnect-recovery pair
            // may no longer share a server-side party row, so honour the teardown
            // for a recently-partied member or a remembered leader.
            if (command.Equals("@forget", StringComparison.OrdinalIgnoreCase))
                return ForgetEligibility?.Invoke(sender) ?? false;
            if (command.Equals("@ptrain", StringComparison.OrdinalIgnoreCase))
                return PartyTrainEligibility?.Invoke(sender) ?? false;
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

        // @reset fallback — @reset is a party-rhythm coordination function, not
        // a settings mutation: the leader fires it at the start of a lap so
        // every member zeroes their per-lap session counters (exp/hour,
        // combat-round samples). The catalog files it under AlterSettings only
        // for permission-grouping with the other "alter something on my behalf"
        // verbs, but any active party member should be able to issue it without
        // an explicit AlterSettings grant — the same social baseline as @health
        // / @party. It touches nothing but the sender-visible tracking totals,
        // so it's safe to un-gate for members. (Report 222201.)
        if (command.Equals("@reset", StringComparison.OrdinalIgnoreCase)
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
    // Local (say) replies are DIRECTED at the sender with the `>` verb
    // (`><name> <msg>`, confirmed mechanic): say is room-wide, so in a room with
    // several players the `>name` prefix tells the specific player who used the
    // @-command that the reply is for them. (The period say-precursor `.<msg>` is the
    // UNDIRECTED say form; the `>` directed verb needs no precursor.) Every @-command
    // reply routes through here, so this one switch fixes the channel for all of them.
    //
    // Reply payload is encapsulated in { } braces — every remote-command response
    // carries the curly-brace meta-line convention so the recipient's terminal
    // can visually distinguish an engine-generated answer from in-character
    // speech. Handlers provide bare text; the engine adds the braces here, so
    // nothing upstream has to remember the convention.
    private void SendReply(RemoteChannel channel, string recipient, string text, BroadcastAnswer? answer = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendLine(channel, recipient, $"{{{text}}}", answer);
    }

    // Address a line to recipient on channel and put it on the wire. A reply wraps its
    // text in { } first; a relay-back sends its @-command bare so the recipient's
    // engine runs it.
    //
    // Sneaking or hidden, nothing is said aloud: a line for the say channel goes by
    // telepath instead (StealthedProvider). That holds for a relay-back as for a
    // reply — either would end the sneak.
    private void SendLine(RemoteChannel channel, string recipient, string payload, BroadcastAnswer? answer = null)
    {
        if (channel == RemoteChannel.Local && StealthedProvider?.Invoke() == true)
            channel = RemoteChannel.Telepath;
        if (channel == RemoteChannel.Broadcast && !MayAnswerOnBroadcast(recipient, answer)) return;
        // Nothing outside ASCII goes out: see WireText.
        payload = WireText.Ascii(payload);
        string given = GivenName(recipient);
        string wire = channel switch
        {
            RemoteChannel.Telepath => $"/{given} {payload}",
            RemoteChannel.Gangpath => $"bg {payload}",   // gang-channel speak verb is `bg`, not `gang`
            RemoteChannel.Local    => $">{given} {payload}",   // directed say at the @-command's sender
            // The numbered channel's speak verb (GAME_MECHANICS "Channel broadcast").
            RemoteChannel.Broadcast => $"br {payload}",
            _                      => payload,
        };
        byte[] bytes = Encoding.Latin1.GetBytes(wire + "\r");
        _wireSender?.Invoke(bytes);
    }

    // What is said on the broadcast channel to speakers who hold the permission for
    // what they sent, and the refusals to those who do not: two bounds of the same
    // size, kept apart (OpenBroadcastAnswer).
    private readonly BroadcastAnswerBudget _broadcastAnswers = new();
    private readonly BroadcastAnswerBudget _broadcastRefusals = new();

    // Everything this client says for one line of a speaker's on the broadcast
    // channel, and the bound it is counted against. The reply callbacks made for
    // that line hold it, so what a handler says after the dispatch has returned (a
    // disarm's result after its acceptance, a trip's arrival) is the same answer
    // and not a second one, which the minute would swallow.
    private sealed class BroadcastAnswer(BroadcastAnswerBudget budget)
    {
        public BroadcastAnswerBudget Budget { get; } = budget;
        public bool Begun;
    }

    // Every line this client says on the broadcast channel passes here, so the
    // bound on them (BroadcastAnswerBudget) has one gate. The first line said for
    // a line of a speaker's begins an answer and is counted; the lines that follow
    // for the same line of theirs are that answer, so a timer sync's data goes out
    // whole. A line said for nothing of theirs is an answer of its own, counted
    // with the refusals: nobody was granted it.
    private bool MayAnswerOnBroadcast(string recipient, BroadcastAnswer? answer)
    {
        if (answer is { Begun: true }) return true;
        BroadcastAnswerBudget budget = answer?.Budget ?? _broadcastRefusals;
        DateTimeOffset now = _now();
        if (WhyNotOnBroadcast(budget, recipient, now) is { } spent)
        {
            _log?.Log(LogSeverity.Debug, "RemoteCmd",
                $"Not said to {recipient} on the broadcast channel: {spent}.");
            return false;
        }
        budget.Note(recipient, now);
        if (answer is not null) answer.Begun = true;
        return true;
    }

    // For the bug report: the turns spent this minute of the two broadcast bounds,
    // each out of BroadcastAnswerBudget.AnswersPerWindow, and how many speakers they
    // hold a time for (a speaker past the minute is dropped at the next look). Counts
    // only: the speakers are other players.
    public readonly record struct BroadcastAnswerCounts(int Permitted, int Refused, int Remembered);

    public BroadcastAnswerCounts BroadcastAnswers()
    {
        DateTimeOffset now = _now();
        return new(_broadcastAnswers.SpentAt(now), _broadcastRefusals.SpentAt(now),
            _broadcastAnswers.Remembered + _broadcastRefusals.Remembered);
    }

    // True when speaker is the local character (a public-channel self-echo). The gang
    // echo carries a single-word given name, while our own name may be "Given Family", so
    // compare on the given-name form — the same convention IsActivePartyMember uses.
    public bool IsSelfEcho(string speaker)
    {
        string? self = SelfNameProvider?.Invoke();
        if (string.IsNullOrEmpty(self)) return false;
        return GivenName(self).Equals(GivenName(speaker), StringComparison.OrdinalIgnoreCase);
    }

    // The name a telepath is addressed by and a chat line is tagged with: the first
    // word of "Given Family".
    internal static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private readonly record struct Registration(
        PlayerRemoteControls RequiredCategory,
        Action<RemoteCommandContext> Handler);
}
