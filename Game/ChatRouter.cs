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
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    // Most recent `/recipient message` line the user typed. Outgoing telepath
    // confirmations from the server don't echo the message text, so we have to
    // correlate with the typed command on the line above. Cleared after each
    // consume so we don't re-attribute it to a later telepath.
    private string? _pendingTelepathMessage;

    // Fired once per classified line.
    public event Action<ChatLogEntry>? EntryClassified;

    public ChatRouter(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;

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

        // Sniff every dispatched line for the user's /recipient message
        // pattern so we can attribute the message text when the server's
        // confirmation arrives.
        _router.LineDispatched += OnLineDispatched;
    }

    private void OnLineDispatched(Terminal.LineExtractor.EmittedLine line)
    {
        // Form is "/<prefix> <message>" — the user's slash-shortcut for
        // sending a telepath. Match conservatively: leading slash + at
        // least one word char, then space, then any message text.
        // Anything that looks like that becomes the pending message; if no
        // telepath confirmation follows on the next line, it's stale and
        // gets overwritten by the next /-command.
        string text = line.Text;
        if (text.Length < 3 || text[0] != '/') return;
        int sp = text.IndexOf(' ');
        if (sp <= 1 || sp == text.Length - 1) return;

        // Skip any /-line that's clearly not a telepath shortcut — the
        // game has other slash-commands; if a slash-command turns out
        // not to produce a telepath, the pending message just gets
        // overwritten or dropped on the next /-line.
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
