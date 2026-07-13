using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// A MessageRouter consumer. Subscribes to the chat / realm-event patterns in
// KnownPatterns, classifies each match into a ChatChannel, and emits
// ChatLogEntry events. ChatHistoryStore and ConversationWindow subscribe to
// EntryClassified.
//
// One pattern-id maps to one channel; the same line never fires twice on this
// router (fan-out within MessageRouter still applies — other subscribers can
// also see the line). Speaker / message extraction comes from each pattern's
// named capture groups.
//
// Lifetime: subscriptions live for the router's lifetime. The router is
// app-singleton, so IDisposable isn't strictly necessary; it's implemented
// anyway to support tests that build / tear down a router per case.
public sealed class ChatRouter : IDisposable
{
    private readonly MessageRouter _router;
    private readonly Func<bool>? _isParadigmRealm;
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    // Most recent `/recipient message` line the user typed. Outgoing telepath
    // confirmations from the server don't echo the message text, so we have to
    // correlate with the typed command on the line above. Cleared after each
    // consume so we don't re-attribute it to a later telepath.
    private string? _pendingTelepathMessage;

    // Board-specific disconnect line, for the conversation window's realm
    // category. Returns the active BBS's raw DisconnectPattern ({name}/* syntax,
    // empty/null when the board uses only the standard lines). Set by AppServices
    // — the same provider PartyManager uses for roster eviction. The built-in
    // "just disconnected!!!." / "just hung up!!!" patterns only match a
    // single-word name with a trailing period, so a board whose logoff line
    // differs (no period, spaced name, account-name form) never reaches the
    // realm-event subscriptions below; this additive sniff classifies it too.
    public Func<string?>? DisconnectPatternProvider { get; set; }

    // Compiled form of the current DisconnectPattern, cached against its raw
    // source so we only recompile when the user edits the pattern or swaps BBS.
    private string? _compiledDisconnectSource;
    private Regex? _compiledDisconnect;

    // Fired once per classified line.
    public event Action<ChatLogEntry>? EntryClassified;

    // isParadigmRealm gates the server-PvP channel: paradigm is the only realm
    // that emits the "Server PvP Message:" line, so on any other realm the sub
    // is a no-op. Optional (null → gate open) so tests can build a bare router.
    public ChatRouter(MessageRouter router, Func<bool>? isParadigmRealm = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _isParadigmRealm = isParadigmRealm;

        Subscribe(router, KnownPatterns.ConversationGossip,
                  ChatChannel.Gossip,         groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationLocal,
                  ChatChannel.Local,          groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationTelepathIn,
                  ChatChannel.TelepathIncoming, groupPlayer: 0, groupMessage: 1);
        // Outgoing telepath is special: the server's "--- Telepath Sent
        // to X ---" line carries only the recipient, never the message.
        // Pair with the typed /recipient-message line captured below.
        _subs.Add(_router.Subscribe(KnownPatterns.ConversationTelepathOut, result =>
        {
            string? recipient = SafeGroup(result, 0);
            string message = _pendingTelepathMessage ?? string.Empty;
            _pendingTelepathMessage = null;

            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.TelepathOutgoing,
                NullIfEmpty(recipient),
                message,
                result.Line.Text));
        }));
        Subscribe(router, KnownPatterns.ConversationGangpath,
                  ChatChannel.Gangpath,       groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationBroadcast,
                  ChatChannel.Broadcast,      groupPlayer: 0, groupMessage: 1);
        // Yell combines "X yells …" + "You yell …". The player group is
        // empty in the self-yell case; ClassifyAndEmit handles that.
        Subscribe(router, KnownPatterns.ConversationYell,
                  ChatChannel.Yell,           groupPlayer: 0, groupMessage: 1);

        // Realm events: speaker is the moving / dropping player. Message
        // is a short verb phrase that ChatHistoryStore / Conversation
        // window can render.
        SubscribeRealmEvent(router, KnownPatterns.PlayerEnters,       "entered the Realm");
        SubscribeRealmEvent(router, KnownPatterns.PlayerExits,        "left the Realm");
        SubscribeRealmEvent(router, KnownPatterns.PlayerDisconnects,  "disconnected");

        // Paradigm server PvP announcements (kills and other "Server PvP
        // Message: …" lines) — realm-gated. Speaker is null (the server, not a
        // player); the captured body is the whole line after the prefix.
        _subs.Add(router.Subscribe(KnownPatterns.ConversationServerPvp, result =>
        {
            if (_isParadigmRealm is not null && !_isParadigmRealm()) return;
            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.Server,
                null,
                SafeGroup(result, 0) ?? result.Line.Text,
                result.Line.Text));
        }));

        // Sniff every dispatched line for the user's /recipient message
        // pattern so we can attribute the message text when the server's
        // confirmation arrives.
        _router.LineDispatched += OnLineDispatched;
    }

    private void OnLineDispatched(Terminal.LineExtractor.EmittedLine line)
    {
        TryCaptureTelepath(line.Text);
        TryCustomDisconnectLine(line);
    }

    // Match the active BBS's custom disconnect line against a freshly-emitted
    // terminal line and, on a hit, emit a "disconnected" realm event so the
    // conversation window logs it under the realm category — the same treatment
    // the built-in PlayerDisconnects pattern gets. Additive to that built-in
    // subscription; a board that only emits the custom form (the reason the user
    // configured one) would otherwise never appear in the conversation.
    private void TryCustomDisconnectLine(Terminal.LineExtractor.EmittedLine line)
    {
        string text = line.Text;
        if (string.IsNullOrEmpty(text)) return;
        Regex? rx = CurrentDisconnectRegex();
        if (rx is null) return;
        Match m = rx.Match(text);
        if (!m.Success) return;
        Group g = m.Groups["name"];
        string captured = g.Success ? g.Value.Trim() : string.Empty;
        if (captured.Length == 0) return;

        EntryClassified?.Invoke(new ChatLogEntry(
            line.Timestamp,
            ChatChannel.RealmEvent,
            captured,
            "disconnected",
            text));
    }

    // Lazily (re)compile the BBS DisconnectPattern into a Regex, caching against
    // the raw source so recompilation only happens on an edit / BBS swap. Uses the
    // same literal→regex translation the trigger engine applies ({name} → named
    // capture, * → greedy run, everything else escaped). A malformed pattern
    // disables the custom path rather than throwing on every line. Mirrors
    // PartyManager.CurrentDisconnectRegex.
    private Regex? CurrentDisconnectRegex()
    {
        string? raw = DisconnectPatternProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _compiledDisconnectSource = null;
            _compiledDisconnect = null;
            return null;
        }
        if (!string.Equals(raw, _compiledDisconnectSource, StringComparison.Ordinal))
        {
            _compiledDisconnectSource = raw;
            try
            {
                _compiledDisconnect = new Regex(
                    TriggerEngine.LiteralToRegex(raw),
                    RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                _compiledDisconnect = null;
            }
        }
        return _compiledDisconnect;
    }

    // Engine-sent telepaths (party @-command broadcasts, join nags) reach the
    // wire through SendUserInput without ever rendering on the terminal, so the
    // on-screen /-line sniff in OnLineDispatched never sees them — the outgoing
    // conversation entry would then come up blank. Peek the raw outbound bytes
    // here too: a complete "/<recipient> <message>\r" burst captures the same
    // pending message a typed line would. Char-by-char terminal typing carries
    // no complete line in a single buffer, so nothing is captured here and the
    // typed case stays handled by the on-screen path.
    public void ObserveOutbound(byte[] data)
    {
        if (data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string outLine in text.Split(LineBreaks,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            TryCaptureTelepath(outLine);
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };

    // Form is "/<prefix> <message>" — the slash-shortcut for sending a telepath.
    // Match conservatively: leading slash + at least one word char, then space,
    // then any message text. Anything that looks like that becomes the pending
    // message; if no telepath confirmation follows, it's stale and gets
    // overwritten by the next /-line. The game has other slash-commands, so a
    // /-line that turns out not to produce a telepath just gets overwritten or
    // dropped on the next one.
    private void TryCaptureTelepath(string text)
    {
        if (text.Length < 3 || text[0] != '/') return;
        int sp = text.IndexOf(' ');
        if (sp <= 1 || sp == text.Length - 1) return;
        _pendingTelepathMessage = text[(sp + 1)..];
    }

    private void Subscribe(
        MessageRouter router,
        string patternId,
        ChatChannel channel,
        int groupPlayer,
        int groupMessage)
    {
        _subs.Add(router.Subscribe(patternId, result =>
        {
            string? speaker = SafeGroup(result, groupPlayer);
            string message  = groupMessage >= 0 ? SafeGroup(result, groupMessage) ?? string.Empty : string.Empty;

            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                channel,
                NullIfEmpty(speaker),
                message,
                result.Line.Text));
        }));
    }

    private void SubscribeRealmEvent(MessageRouter router, string patternId, string verbPhrase)
    {
        _subs.Add(router.Subscribe(patternId, result =>
        {
            string? player = SafeGroup(result, 0);
            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.RealmEvent,
                NullIfEmpty(player),
                verbPhrase,
                result.Line.Text));
        }));
    }

    private static string? SafeGroup(MatchResult result, int index)
        => (uint)index < (uint)result.Groups.Count ? result.Groups[index] : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLineDispatched;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }
}
