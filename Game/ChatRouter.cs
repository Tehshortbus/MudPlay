using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

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
// app-singleton; Dispose unhooks every subscription for a router that is torn
// down before the MessageRouter it listens to.
public sealed class ChatRouter : IDisposable
{
    private readonly MessageRouter _router;
    private readonly Func<bool>? _isParadigmRealm;
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    // The `/recipient message` lines sent but not yet confirmed, oldest first, each
    // with the name it was addressed to and when it was captured. Outgoing telepath
    // confirmations from the server don't echo the message text, so we have to
    // correlate with the typed/sent command. A list (not a single slot) because an
    // engine can fire several sends back-to-back before any confirmation arrives —
    // e.g. a @roomba reply answering with one telepath per item — and the server's
    // "--- Telepath Sent to X ---" lines then arrive as their own burst; each takes
    // the oldest pending send addressed to the player it names
    // (TakeTelepathConfirmed, which applies the staleness check first). A send the
    // game is known not to have sent is dropped before then (DropUnsentTelepath).
    private readonly List<PendingTelepath> _pendingTelepaths = new();
    // Same pairing for an outgoing directed say: the typed `>Target message` carries
    // the message, the server's `--- Message Directed to X ---` confirmation the target.
    private readonly Queue<(string Message, DateTimeOffset EnqueuedAt)> _pendingDirectedMessages = new();
    // Hard cap on either pending queue. Bounding drop-oldest keeps a runaway of
    // unconfirmed captures from growing without limit; a real burst is a handful
    // of lines, so 16 never trims live work.
    private const int MaxPendingChatCaptures = 16;

    // A captured `/`-line that never yields a confirmation (some other
    // slash-command that isn't actually a telepath, or a genuine reply whose own
    // "--- Telepath Sent ---" line the server never sent / this client never
    // recognized) used to leave a stale entry sitting at the front of the queue
    // FOREVER — the next real confirmation, however much later, wrongly dequeued
    // it, and the pairing then drifted for every following exchange (report
    // paradigm-20260915-055754: a @roomba reply to "martial sleeve" queued
    // 2026-09-14 20:55 sat unconfirmed until 2026-09-15 05:35, when it got wrongly
    // paired with that morning's unrelated "dark blue orb" query). A real
    // confirmation arrives within a couple of seconds of the send; TryDequeueFresh
    // discards anything older than this before trusting it as the real pairing —
    // generous enough to survive a paced multi-line burst, nowhere near long
    // enough for a stale cross-session entry to still count as "the" match.
    internal static readonly TimeSpan PendingCaptureStaleAfter = TimeSpan.FromSeconds(15);

    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;

    // Guards against double-enqueuing ONE physical send. An engine-fired reply
    // is captured via ObserveOutbound (the raw bytes, immediately). If that
    // reply happens to land on the same screen row as the still-displayed
    // prompt, LineExtractor's prompt-split (EmitLine) re-emits its text as an
    // ordinary content line, which OnLineDispatched also sniffs — capturing the
    // identical text a second time before any confirmation ever consumes the
    // first. Left unguarded, every reply leaves one extra stale entry in the
    // queue, so pairing drifts further off with every exchange for the rest of
    // the session. Holds the most recent still-unconfirmed capture; a repeat of
    // that exact text is dropped instead of re-queued. Cleared on the next
    // confirmation (of either kind) so a later, genuinely new send that happens
    // to produce identical text isn't wrongly suppressed.
    //
    // For a telepath this one-line guard covers only a screen row shown twice
    // whose send was not seen going out. The screen copy of a line that was is
    // told by the lines sent lately (_sentLately): there may be several awaiting
    // their copies at once.
    private string? _lastEnqueuedTelepath;
    private string? _lastEnqueuedDirected;

    // Board-specific disconnect line, for the conversation window's realm
    // category. Returns the active BBS's raw DisconnectPattern ({name}/* syntax,
    // empty/null when the board uses only the standard lines). Set by AppServices
    // — the same provider PartyManager uses for roster eviction. The built-in
    // "just disconnected!!!." / "just hung up!!!" patterns only match a
    // single-word name with a trailing period, so a board whose logoff line
    // differs (no period, spaced name, account-name form) never reaches the
    // realm-event subscriptions below; this additive sniff classifies it too.
    public Func<string?>? DisconnectPatternProvider { get; set; }

    // Resolves whether a name is a real player currently in the room / party — the
    // known-player gate for others'-POV actions/emotes (rejects room names, monsters
    // and ambient flavour that share the action green). Set by AppServices; null →
    // gate closed, so no others'-POV action is captured until wired.
    public Func<string, bool>? IsKnownPlayer { get; set; }

    // Compiled form of the current DisconnectPattern, cached against its raw
    // source so we only recompile when the user edits the pattern or swaps BBS.
    private string? _compiledDisconnectSource;
    private Regex? _compiledDisconnect;

    // Fired once per classified line.
    public event Action<ChatLogEntry>? EntryClassified;

    // isParadigmRealm gates the server-PvP channel: paradigm is the only realm
    // that emits the "Server PvP Message:" line, so on any other realm the sub
    // is a no-op. Optional (null → gate open).
    // log is optional (null → the stale-discard notice just doesn't surface,
    // same fail-open convention as everywhere else in this codebase); clock is
    // optional (defaults to the real wall clock) and drives the staleness check.
    public ChatRouter(MessageRouter router, Func<bool>? isParadigmRealm = null,
        LogService? log = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _isParadigmRealm = isParadigmRealm;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);

        Subscribe(router, KnownPatterns.ConversationGossip,
                  ChatChannel.Gossip,         groupPlayer: 0, groupMessage: 1);
        // Say — custom sub (not the generic player+message helper) so it can carry
        // the directed-say target (group 1): a directed say `X says (to Target) "msg"`
        // is seen by the target AND third parties, so the window shows "X (to Target)".
        _subs.Add(router.Subscribe(KnownPatterns.ConversationLocal, result =>
        {
            string? speaker  = SafeGroup(result, 0);
            string? directed = SafeGroup(result, 1);
            string  message  = SafeGroup(result, 2) ?? string.Empty;
            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.Local,
                NullIfEmpty(speaker),
                message,
                result.Line.Text,
                DirectedTo: NullIfEmpty(directed)));
        }));
        // Our OWN outgoing directed say: the server confirms only the target
        // (`--- Message Directed to X ---`), so pair it with the typed `>X message`
        // line captured below to log "You (to X): message".
        _subs.Add(router.Subscribe(KnownPatterns.ConversationDirectedSayOut, result =>
        {
            string? target = SafeGroup(result, 0);
            string message = TryDequeueFresh(_pendingDirectedMessages, "directed say");
            _lastEnqueuedDirected = null;
            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.Local,
                null,                       // us — renders as "You"
                message,
                result.Line.Text,
                DirectedTo: NullIfEmpty(target)));
        }));
        Subscribe(router, KnownPatterns.ConversationTelepathIn,
                  ChatChannel.TelepathIncoming, groupPlayer: 0, groupMessage: 1);
        // Outgoing telepath is special: the server's "--- Telepath Sent
        // to X ---" line carries only the recipient, never the message.
        // Pair with the typed /recipient-message line captured below.
        _subs.Add(_router.Subscribe(KnownPatterns.ConversationTelepathOut, result =>
        {
            string? recipient = SafeGroup(result, 0);
            PendingTelepath? sent = TakeTelepathConfirmed(recipient);
            _lastEnqueuedTelepath = null;

            OutgoingTelepathWasUsers = sent is { UsersOwn: true };
            try
            {
                EntryClassified?.Invoke(new ChatLogEntry(
                    result.Line.Timestamp,
                    ChatChannel.TelepathOutgoing,
                    NullIfEmpty(recipient),
                    sent?.Message ?? string.Empty,
                    result.Line.Text));
            }
            finally { OutgoingTelepathWasUsers = false; }
        }));
        Subscribe(router, KnownPatterns.ConversationGangpath,
                  ChatChannel.Gangpath,       groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationBroadcast,
                  ChatChannel.Broadcast,      groupPlayer: 0, groupMessage: 1);
        // Yell combines every form: "You yell …" (player group empty → speaker null =
        // us), "From X (Yelling): …", and "Someone yells from …" (speaker "Someone",
        // with where it came from in HeardFrom; empty for the other forms).
        _subs.Add(router.Subscribe(KnownPatterns.ConversationYell, result =>
        {
            EntryClassified?.Invoke(new ChatLogEntry(
                result.Line.Timestamp,
                ChatChannel.Yell,
                NullIfEmpty(SafeGroup(result, 0)),
                SafeGroup(result, 2) ?? string.Empty,
                result.Line.Text,
                HeardFrom: NullIfEmpty(SafeGroup(result, 1))));
        }));

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

        // Sniff every dispatched line for the /recipient message pattern, so a
        // telepath whose send was not seen going out still has its text when the
        // server's confirmation arrives.
        _router.LineDispatched += OnLineDispatched;
    }

    private void OnLineDispatched(Terminal.LineExtractor.EmittedLine line)
    {
        TrackAmbiguousReply(line.Text);
        TryCaptureShownTelepath(line.Text);
        TryCaptureDirectedSay(line.Text);
        TryCustomDisconnectLine(line);
        TryActionEmote(line);
    }

    // Capture a BBS action / emote — a full-green line whose head is an emote shape
    // (own "You …", or "<room player> …") — into the conversation as a Social entry.
    // Colour is read off the line's attributes (still present here). The all-green
    // gate drops the label-only greens (Wealth: / Encumbrance: / stat rows) and the
    // non-green "You are carrying …"; the head + known-player gates drop the one
    // fully-green non-action line, "Obvious exits:". See ActionEmoteClassifier.
    private void TryActionEmote(Terminal.LineExtractor.EmittedLine line)
    {
        if (string.IsNullOrEmpty(line.Text)) return;
        if (!ActionEmoteClassifier.IsAllGreen(line)) return;
        if (ActionEmoteClassifier.Classify(line.Text, IsKnownPlayer ?? (_ => false), out _)
            == ActionEmoteClassifier.Kind.None)
            return;

        // The full green sentence is self-contained (the actor is in the text), so
        // no speaker split — the row renders the whole line green under the say chip.
        EntryClassified?.Invoke(new ChatLogEntry(
            line.Timestamp, ChatChannel.Social, null, line.Text, line.Text));
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

    // Every line on its way to the game is told here as it goes out. Engine-sent
    // telepaths (party @-command broadcasts, join nags, @roomba replies) and the
    // user's own whole lines (a window's button, a macro, an alias) arrive as
    // complete "/<recipient> <message>\r" bytes. What the user types at the
    // terminal is told here too, as the line their keys made up when its Enter
    // went out (SentCommandTrail.TypedLineEntered), whether the terminal sent it
    // whole or a key at a time.
    //
    // An engine-fired reply also tends to land ON SCREEN: the terminal
    // composites it onto the still-live prompt row, and LineExtractor's
    // prompt-split re-emits that as an ordinary content line, which
    // OnLineDispatched's /-line sniff below ALSO captures a moment later —
    // the identical text, for the same one physical send. _sentLately (for a
    // telepath) and _lastEnqueuedDirected exist to drop that redundant second
    // capture; without them every engine reply left one extra stale entry in the
    // queue, permanently drifting every later pairing for the rest of the session.
    //
    // usersOwn: the send is the user's own doing (typed at the terminal, a
    // window's button, a macro, a trigger, an event or an alias of theirs), as
    // against one the client makes by itself or for another player's remote
    // command. Carried with the pending telepath to its confirmation
    // (OutgoingTelepathWasUsers), and remembered for UserSentLately. Nobody
    // vouching, it is not.
    public void ObserveOutbound(byte[] data, bool usersOwn = false)
    {
        if (data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        DateTimeOffset now = _now();
        foreach (string outLine in text.Split(LineBreaks,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (usersOwn)
            {
                _usersLinesLately.RemoveAll(s => now - s.At > PendingCaptureStaleAfter);
                if (_usersLinesLately.Count >= MaxUsersLinesKept) _usersLinesLately.RemoveAt(0);
                _usersLinesLately.Add((outLine, now));
            }
            // Every line seen going out is a send, the same words twice included,
            // and its screen copy is expected.
            if (IsTelepathLine(outLine, out _))
            {
                AddPendingTelepath(outLine, usersOwn ? SendOrigin.User : SendOrigin.Client);
                _sentLately.RemoveAll(s => now - s.At > PendingCaptureStaleAfter);
                _sentLately.Add((outLine, now));
            }
            TryCaptureDirectedSay(outLine);
            if (usersOwn) UsersLineSent?.Invoke(outLine);
        }
    }

    // A line of the user's own going out, as ObserveOutbound is told it: the moment
    // they pressed Enter or a button of theirs sent it, before the game has shown
    // anything for it. Raised after a telepath has been noted as awaiting its answer.
    public event Action<string>? UsersLineSent;

    // A telepath the game answered with something other than a confirmation, as it
    // went out: it reached nobody (DropUnsentTelepath).
    public event Action<string>? UnsentTelepathDropped;

    // Whether a telepath the user sent, one isTheOne says is the kind asked about
    // (told the line as it went out), is still waiting for the game's answer:
    // neither confirmed, nor known not to have been sent, nor older than a
    // confirmation ever comes. Asked of the pending sends themselves, so a
    // subscriber that came into being after the line went out still sees it.
    public bool UsersTelepathAwaiting(Func<string, bool> isTheOne)
    {
        ArgumentNullException.ThrowIfNull(isTheOne);
        DateTimeOffset now = _now();
        return _pendingTelepaths.Exists(p => p.UsersOwn
                                             && now - p.EnqueuedAt <= PendingCaptureStaleAfter
                                             && isTheOne($"/{p.Target} {p.Message}"));
    }

    // The telepath lines seen going out lately, so that the row the board shows
    // back for one is not queued a second time for the Conversation window
    // (TryCaptureShownTelepath). Kept for as long as a pending send is, not until
    // its copy has come: a board may show a line back more than once.
    private readonly List<(string Line, DateTimeOffset At)> _sentLately = new();

    // The user's own lines seen going out lately, of every kind, for a subscriber
    // that has to know whether a request it sees come back on a channel was the
    // user's doing (UserSentLately). This is the only word there is on whose send
    // a line was: the line as it went out. What the screen shows afterwards says
    // what was sent, by this client, and never by whom.
    private readonly List<(string Line, DateTimeOffset At)> _usersLinesLately = new();
    private const int MaxUsersLinesKept = 64;

    // Whether the user, by their own doing (typed, or sent by a button, macro,
    // trigger, event or alias of theirs), has in the last few seconds sent a line
    // that isTheOne says is the one asked about. The echo of a line of ours on a
    // shared channel shows that this client said it, not that the user did: the
    // client also says such lines for other players (a `@do bg …`, a relay-back).
    public bool UserSentLately(Func<string, bool> isTheOne)
    {
        ArgumentNullException.ThrowIfNull(isTheOne);
        DateTimeOffset now = _now();
        _usersLinesLately.RemoveAll(s => now - s.At > PendingCaptureStaleAfter);
        return _usersLinesLately.Exists(s => isTheOne(s.Line));
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };

    // Form is "/<prefix> <message>" — the slash-shortcut for sending a telepath.
    // Match conservatively: leading slash + at least one word char, then space,
    // then any message text. Anything that looks like that is kept as a pending
    // send, with the name it was addressed to, until a confirmation naming that
    // player takes it — see _pendingTelepaths. The game has other slash-commands,
    // so a /-line that turns out not to produce a telepath (or one whose own
    // confirmation the server never sends / this client never recognizes) leaves
    // a stale entry, discarded once it's too old to plausibly be the real match.
    //
    // origin: the line was seen going out as the user's own doing, or as one the
    // client sent by itself or for another player, or is known only from a row on
    // the screen.
    private void AddPendingTelepath(string text, SendOrigin origin)
    {
        if (!IsTelepathLine(text, out int sp)) return;
        if (_pendingTelepaths.Count >= MaxPendingChatCaptures) _pendingTelepaths.RemoveAt(0);
        _pendingTelepaths.Add(new PendingTelepath(text[1..sp], text[(sp + 1)..], _now(), origin));
    }

    private static bool IsTelepathLine(string text, out int space)
    {
        space = text.Length < 3 || text[0] != '/' ? -1 : text.IndexOf(' ');
        return space > 1 && space < text.Length - 1;
    }

    // A screen row that reads as a telepath. It is kept for one thing: to give the
    // Conversation window the text of a telepath whose send was not seen going out
    // (its confirmation carries only the name). It is never a send of the user's.
    // A row says a line was shown, not who sent it: the board shows back what the
    // client sent for another player the same as what the user typed, and not
    // always as it was sent (cut by an incoming line, wrapped, late, its 8-bit
    // characters drawn as other ones). Whose send a line was is known only from
    // the line as it went out (ObserveOutbound).
    //
    // A row that is a line sent lately, begins one, or that one begins is that
    // send's copy and is not queued again.
    private void TryCaptureShownTelepath(string text)
    {
        if (!IsTelepathLine(text, out _)) return;
        DateTimeOffset now = _now();
        _sentLately.RemoveAll(s => now - s.At > PendingCaptureStaleAfter);
        if (_sentLately.Exists(s => s.Line.StartsWith(text, StringComparison.Ordinal)
                                    || text.StartsWith(s.Line, StringComparison.Ordinal)))
            return;
        if (text == _lastEnqueuedTelepath) return; // the same row seen twice — see _lastEnqueuedTelepath
        AddPendingTelepath(text, SendOrigin.Screen);
        _lastEnqueuedTelepath = text;
    }

    // The pending send a "--- Telepath Sent to <name> ---" confirmation is for:
    // of those still fresh whose addressed name begins the name confirmed, the one
    // addressed by the most letters, and the oldest of those when several are
    // addressed alike. The game takes the first letters of a name for a player and
    // confirms with the name in full ("/raij" reaches Raijin), but only when the
    // letters fit that one player: letters that fit two reach neither. So of two
    // pending sends that both begin this player's name, the one with fewer letters
    // may be the one the game refused as fitting somebody else as well, and the one
    // with more letters fits no player the other does not. The confirmation is the
    // more specific one's; the full name is simply the most specific there is.
    // Without this, a shortened request of the user's that reached nobody would be
    // handed the confirmation of a later telepath to any player its letters begin,
    // one the client sent for another player included, and that player would be
    // the one the user asked. A send addressed to anyone else is never the one
    // confirmed, however long it has waited: its own reply was something other
    // than a confirmation (no such player, letters that fit two players, the
    // telepath not sent), and handing it to this one would say the player named
    // here was sent a message they never were. Null when no pending send fits.
    private PendingTelepath? TakeTelepathConfirmed(string? name)
    {
        DateTimeOffset now = _now();
        for (int i = _pendingTelepaths.Count - 1; i >= 0; i--)
        {
            PendingTelepath old = _pendingTelepaths[i];
            if (now - old.EnqueuedAt <= PendingCaptureStaleAfter) continue;
            _pendingTelepaths.RemoveAt(i);
            _log?.Warn("ChatRouter",
                $"discarding stale unconfirmed telepath capture (queued {(now - old.EnqueuedAt).TotalSeconds:F0}s ago, "
                + $"never confirmed): {Describe(old)} — its own confirmation never arrived, so it can't be the "
                + "match for this one.");
        }
        if (string.IsNullOrEmpty(name)) return null;
        int at = -1;
        for (int i = 0; i < _pendingTelepaths.Count; i++)
        {
            string addressed = _pendingTelepaths[i].Target;
            if (!name.StartsWith(addressed, StringComparison.OrdinalIgnoreCase)) continue;
            if (at < 0 || addressed.Length > _pendingTelepaths[at].Target.Length) at = i;
        }
        if (at < 0) return null;
        PendingTelepath sent = _pendingTelepaths[at];
        _pendingTelepaths.RemoveAt(at);
        return sent;
    }

    // Only a line the client sent by itself is quoted: that is its own protocol
    // text. What the user sent, or a row off the screen that may be the copy of
    // it, is the user's words, and no log keeps those.
    private static string Describe(PendingTelepath send) =>
        send.Origin == SendOrigin.Client ? $"\"{send.Message}\""
        : send.Origin == SendOrigin.User ? "a telepath the user sent (its words are not kept)"
        : "a row shown on screen (its words are not kept)";

    // The telepath line (`/<name> <text>`) that was the last line written to the
    // wire and is the only one awaiting an answer, or null when anything else has
    // gone out since or another telepath is unanswered. Set by AppServices
    // (TelepathPacer.LastWrittenTelepath); null → no ambiguous-name reply is ever
    // taken for a telepath's.
    public Func<string?>? LastTelepathOnWire { get; set; }

    // TelepathPacer has given up on this telepath: the game answered every try
    // with "--- Telepath Not Sent ---". Its resends do not come through here, so
    // a pending send is dropped only now, never on a refusal that is resent: a
    // resend that goes through is confirmed against it.
    public void TelepathGivenUp(string line) =>
        DropUnsentTelepath(line, "the game did not send it (--- Telepath Not Sent --- on every try)");

    // A telepath whose letters fit more than one player is answered with the
    // ambiguous-name reply, and nothing is sent. That reply is shared with `look`,
    // `ask` and every command that names something, so it is taken for a
    // telepath's only when that telepath was the last line written to the wire as
    // the reply began, and the only one awaiting an answer, and every row it lists
    // begins with the letters the telepath was addressed to. A command written
    // just before the telepath whose reply comes after it, with rows that fit,
    // drops the telepath's pending send: the safe way round, since a confirmation
    // then takes nothing. Judged once the rows have ended: at the next line that is
    // not one, which comes before any confirmation is paired (LineDispatched runs
    // ahead of the pattern subscribers).
    private string? _ambiguousFor;
    private bool _ambiguousRowSeen;

    private void TrackAmbiguousReply(string text)
    {
        if (_ambiguousFor is { } line)
        {
            if (text.StartsWith(EngineReplyLines.AmbiguousNameRow, StringComparison.Ordinal))
            {
                IsTelepathLine(line, out int sp);
                string row = text[EngineReplyLines.AmbiguousNameRow.Length..].Trim();
                if (row.StartsWith(line[1..sp], StringComparison.OrdinalIgnoreCase)) _ambiguousRowSeen = true;
                else _ambiguousFor = null;   // a row the telepath's letters don't begin: some other command's reply
                return;
            }
            _ambiguousFor = null;
            if (_ambiguousRowSeen)
                DropUnsentTelepath(line, "the game answered that its letters fit more than one player");
        }
        if (text.TrimEnd() == EngineReplyLines.AmbiguousName
            && LastTelepathOnWire?.Invoke()?.Trim() is { } sent && IsTelepathLine(sent, out _))
        {
            _ambiguousFor = sent;
            _ambiguousRowSeen = false;
        }
    }

    // A telepath the game answered with something other than a confirmation
    // reached nobody: its pending send is dropped, so that a later confirmation
    // for a player its letters begin cannot take it. line is the telepath as it
    // went out; of identical pending sends, the oldest goes, the one a
    // confirmation would have taken first.
    private void DropUnsentTelepath(string line, string why)
    {
        string sent = line.Trim();
        int at = _pendingTelepaths.FindIndex(p => $"/{p.Target} {p.Message}" == sent);
        if (at < 0) return;
        PendingTelepath dropped = _pendingTelepaths[at];
        _pendingTelepaths.RemoveAt(at);
        DroppedUnsentTelepaths++;
        _log?.Debug("ChatRouter", $"dropping pending telepath {Describe(dropped)}: {why}; it asked nobody.");
        UnsentTelepathDropped?.Invoke(sent);
    }

    // Bug-report surface.
    public int PendingTelepathCount => _pendingTelepaths.Count;
    public int DroppedUnsentTelepaths { get; private set; }

    // Where a pending send is known from: the line as the client sent it by itself
    // or for another player, the line as the user sent it, or a row on the screen.
    private enum SendOrigin { Client, User, Screen }

    private readonly record struct PendingTelepath(string Target, string Message, DateTimeOffset EnqueuedAt, SendOrigin Origin)
    {
        public bool UsersOwn => Origin == SendOrigin.User;
    }

    // True while EntryClassified is being raised for an outgoing telepath that the
    // confirmation was paired with and that was the user's own. A subscriber that
    // acts on a request the user telepathed (the boss-timer merge window, which
    // writes a telepathed timer without a pick only from a player the user asked) reads it
    // there: a telepath the client sent for another player's remote command, one
    // known only from a row on the screen, and a confirmation paired with nothing,
    // are none of them the user's.
    public bool OutgoingTelepathWasUsers { get; private set; }

    // Form is ">Target message" — the directed-say verb. The typed line carries the
    // message and the server's later "--- Message Directed to X ---" confirmation the
    // target; the confirmation doesn't repeat the text, so captures are paired with
    // confirmations first in, first out, and the pair fires the conversation entry.
    private void TryCaptureDirectedSay(string text)
    {
        if (text.Length < 3 || text[0] != '>') return;
        int sp = text.IndexOf(' ');
        if (sp <= 1 || sp == text.Length - 1) return;
        string message = text[(sp + 1)..];
        if (message == _lastEnqueuedDirected) return; // duplicate capture of the same send — see _lastEnqueuedDirected
        EnqueueBounded(_pendingDirectedMessages, message);
        _lastEnqueuedDirected = message;
    }

    // Enqueue with a hard cap: once a pending queue reaches MaxPendingChatCaptures,
    // drop the oldest so a stream of unconfirmed captures can't grow without bound.
    private void EnqueueBounded(Queue<(string Message, DateTimeOffset EnqueuedAt)> queue, string message)
    {
        if (queue.Count >= MaxPendingChatCaptures) queue.Dequeue();
        queue.Enqueue((message, _now()));
    }

    // Dequeue the next pending capture for a just-arrived confirmation, first
    // discarding anything at the front of the queue that's too old to plausibly
    // be what THIS confirmation is for (see PendingCaptureStaleAfter) — a send
    // whose own confirmation never arrived shouldn't get to steal a later,
    // unrelated one and drift every pairing after it. Returns "" (same as an
    // empty queue) when nothing fresh is left to match.
    private string TryDequeueFresh(Queue<(string Message, DateTimeOffset EnqueuedAt)> queue, string kind)
    {
        DateTimeOffset now = _now();
        while (queue.TryPeek(out (string Message, DateTimeOffset EnqueuedAt) head)
               && now - head.EnqueuedAt > PendingCaptureStaleAfter)
        {
            queue.Dequeue();
            _log?.Warn("ChatRouter",
                $"discarding stale unconfirmed {kind} capture (queued {(now - head.EnqueuedAt).TotalSeconds:F0}s ago, "
                + $"never confirmed): \"{head.Message}\" — its own confirmation never arrived, so it can't be the "
                + "match for this one.");
        }
        return queue.TryDequeue(out (string Message, DateTimeOffset EnqueuedAt) fresh) ? fresh.Message : string.Empty;
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
