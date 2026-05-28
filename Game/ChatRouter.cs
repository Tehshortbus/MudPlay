using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// First <see cref="MessageRouter"/> consumer. Subscribes to the chat /
/// realm-event patterns in <see cref="KnownPatterns"/>, classifies each
/// match into a <see cref="ChatChannel"/>, and emits
/// <see cref="ChatLogEntry"/> events. The Phase 2 ChatHistoryStore (PR 2.4)
/// and ConversationWindow (PR 2.5) subscribe to <see cref="EntryClassified"/>.
/// </summary>
/// <remarks>
/// <para>
/// One pattern-id maps to one channel; the same line never fires twice on
/// this router (fan-out within MessageRouter still applies — other
/// subscribers can also see the line). Speaker / message extraction comes
/// from each pattern's named capture groups.
/// </para>
/// <para>
/// Lifetime: subscriptions live for the router's lifetime. The router is
/// app-singleton, so <see cref="IDisposable"/> isn't strictly necessary;
/// it's implemented anyway to support tests that build / tear down a
/// router per case.
/// </para>
/// </remarks>
public sealed class ChatRouter : IDisposable
{
    private readonly List<IDisposable> _subs = new();
    private bool _disposed;

    /// <summary>Fired once per classified line.</summary>
    public event Action<ChatLogEntry>? EntryClassified;

    public ChatRouter(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);

        Subscribe(router, KnownPatterns.ConversationGossip,
                  ChatChannel.Gossip,         groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationLocal,
                  ChatChannel.Local,          groupPlayer: 0, groupMessage: 1);
        Subscribe(router, KnownPatterns.ConversationTelepathIn,
                  ChatChannel.TelepathIncoming, groupPlayer: 0, groupMessage: 1);
        // Outgoing telepath echo has no message — only the recipient name.
        // Store the recipient as Speaker and leave Message empty.
        Subscribe(router, KnownPatterns.ConversationTelepathOut,
                  ChatChannel.TelepathOutgoing, groupPlayer: 0, groupMessage: -1);
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
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
    }
}
