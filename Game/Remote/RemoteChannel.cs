namespace FujinTerm.Game.Remote;

/// <summary>
/// Chat channels the <see cref="RemoteCommandManager"/> watches for
/// inbound @-commands and routes replies back through. Subset of
/// <see cref="ChatChannel"/> chosen per user direction — exclude
/// system-level (Broadcast / RealmEvent), realm-wide noise channels
/// (Gossip — also carries auctions), and shout-style noise (Yell).
/// The outbound echo (TelepathOutgoing) is excluded because it's our
/// own send-back, not an inbound from another player. The synthetic
/// DaySeparator has no sender.
/// </summary>
public enum RemoteChannel
{
    /// <summary>"X telepaths: @cmd" — the default channel for remote commands.</summary>
    Telepath,

    /// <summary>"X gangpaths: @cmd" — gang/guild scope.</summary>
    Gangpath,

    /// <summary>"X says ..." in the local room.</summary>
    Local,
}
