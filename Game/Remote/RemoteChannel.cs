namespace FujinTerm.Game.Remote;

/// <summary>
/// Chat channels the <see cref="RemoteCommandManager"/> watches for
/// inbound @-commands and routes replies back through. Subset of
/// <see cref="ChatChannel"/> — out-bound echoes (TelepathOutgoing),
/// operator broadcasts, room-shouts (Yell), realm-events, and the
/// synthetic day-separator are all excluded because @-commands never
/// arrive on those.
/// </summary>
public enum RemoteChannel
{
    /// <summary>"X telepaths: @cmd" — the default channel for remote commands.</summary>
    Telepath,

    /// <summary>"X gossips: @cmd" — realm-wide.</summary>
    Gossip,

    /// <summary>"X gangpaths: @cmd" — gang/guild scope.</summary>
    Gangpath,

    /// <summary>"X says ..." in the local room — rarely used for commands but legal.</summary>
    Local,
}
