namespace FujinTerm.Game;

/// <summary>
/// Channels recognised by <see cref="ChatRouter"/>. Drives the Phase 2
/// ConversationWindow filter toggles and the Phase 5 Trigger UI's "scope"
/// dropdown (chat-only / specific channel).
/// </summary>
public enum ChatChannel
{
    /// <summary>"X gossips: …" — realm-wide gossip channel.</summary>
    Gossip,

    /// <summary>"X says ..." in the current room.</summary>
    Local,

    /// <summary>Incoming telepath: "X telepaths: …".</summary>
    TelepathIncoming,

    /// <summary>Outgoing telepath echo: "--- Telepath sent to X ---".</summary>
    TelepathOutgoing,

    /// <summary>"X gangpaths: …" — gang/guild channel.</summary>
    Gangpath,

    /// <summary>"Broadcast from X …" — operator broadcasts.</summary>
    Broadcast,

    /// <summary>"X yells …" — room-shouted message, audible across nearby rooms.</summary>
    Yell,

    /// <summary>Player entrance / exit / disconnect notices.</summary>
    RealmEvent,
}
