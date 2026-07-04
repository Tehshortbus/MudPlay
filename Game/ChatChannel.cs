namespace FujinTerm.Game;

// Channels recognised by ChatRouter. Drives the ConversationWindow filter
// toggles and the Trigger UI's "scope" dropdown (chat-only / specific channel).
public enum ChatChannel
{
    // "X gossips: …" — realm-wide gossip channel.
    Gossip,

    // "X says ..." in the current room.
    Local,

    // Incoming telepath: "X telepaths: …".
    TelepathIncoming,

    // Outgoing telepath echo: "--- Telepath sent to X ---".
    TelepathOutgoing,

    // "X gangpaths: …" — gang/guild channel.
    Gangpath,

    // "Broadcast from X …" — operator broadcasts.
    Broadcast,

    // "X yells …" — room-shouted message, audible across nearby rooms.
    Yell,

    // Player entrance / exit / disconnect notices.
    RealmEvent,

    // Synthetic separator inserted by ChatHistoryStore when the wall-clock date
    // rolls over mid-session. Not produced by ChatRouter; the Conversation
    // window renders these as a horizontal rule. ChatLogEntry.Message carries
    // the ISO date string ("2026-05-08").
    DaySeparator,
}
