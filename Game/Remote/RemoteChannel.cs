namespace FujinTerm.Game.Remote;

// Chat channels the RemoteCommandManager watches for inbound @-commands and
// routes replies back through. Subset of ChatChannel — exclude system-level
// (Broadcast / RealmEvent), realm-wide noise channels (Gossip — also carries
// auctions), and shout-style noise (Yell). The outbound echo (TelepathOutgoing)
// is excluded because it's our own send-back, not an inbound from another
// player. The synthetic DaySeparator has no sender.
public enum RemoteChannel
{
    // "X telepaths: @cmd" — the default channel for remote commands.
    Telepath,

    // "X gangpaths: @cmd" — gang/guild scope.
    Gangpath,

    // "X says ..." in the local room.
    Local,
}
