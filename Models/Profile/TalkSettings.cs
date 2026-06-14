namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Talk" settings — engine-level policy for the Phase 6
/// <see cref="Game.Remote.RemoteCommandManager"/>. Stored as the
/// <c>"Talk"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per-channel disable rows govern only the three channels
/// <see cref="Game.Remote.RemoteChannel"/> accepts inbound @-commands
/// from. Gossip / Auction / Broadcast / Yell are hard-excluded engine-
/// wide; no per-user toggle would change that, so they aren't fields.
/// </para>
/// </remarks>
public sealed class TalkSettings
{
    /// <summary>
    /// Hard kill-switch above every per-channel + per-player permission.
    /// When <c>true</c> the engine ignores every inbound @-command — no
    /// handler fires, no reply is sent. Default <c>false</c>.
    /// </summary>
    public bool DisallowAllRemoteCommands { get; set; }

    /// <summary>
    /// Overrides the base <c>@party &lt;sub&gt;</c> whitelist for every
    /// party member, not just the leader — when <c>true</c> an inbound
    /// <c>@party</c> is a no-op no matter who in the party sends it.
    /// Useful when solo-questing inside a party where you don't want
    /// party directives steering this character. Default <c>false</c>
    /// (whitelist active).
    /// </summary>
    public bool DisallowPartyCommands { get; set; }

    /// <summary>Drop @-commands arriving on telepaths / pages.</summary>
    public bool DisallowRemoteFromTelepaths { get; set; }

    /// <summary>Drop @-commands arriving on the gang/guild channel.</summary>
    public bool DisallowRemoteFromGangpaths { get; set; }

    /// <summary>Drop @-commands arriving on the local say channel.</summary>
    public bool DisallowRemoteFromLocal { get; set; }

    /// <summary>
    /// When <c>true</c>, the engine sends <see cref="RemoteCommandFailureMessage"/>
    /// back to the originator on per-player denial / unknown-command /
    /// party-whitelist denial. Hard-blocks (reroll / suicide) and the
    /// user-disabled paths (master / per-channel) stay silent regardless —
    /// no point informing the sender that a channel they don't even know
    /// is excluded actually was. Default <c>true</c>.
    /// </summary>
    public bool WarnOnInvalidRemoteCommand { get; set; } = true;

    /// <summary>
    /// Reply text sent back to the originator when an @-command is
    /// denied or unrecognised. Variable substitution isn't applied —
    /// the string is sent verbatim. Default
    /// <c>"command invalid or not allowed"</c>. The
    /// <see cref="Game.Remote.RemoteCommandManager"/> wraps every reply
    /// in <c>{ }</c> braces on the wire (the curly-brace meta-line
    /// convention), so this string is bare text — adding literal braces
    /// here would double them.
    /// </summary>
    public string RemoteCommandFailureMessage { get; set; } = "command invalid or not allowed";

    /// <summary>
    /// When <c>true</c>, the first time we spot a non-party player in the
    /// room each local-calendar day — whether from an <c>Also here:</c>
    /// list or a live arrival line — the client sends <c>greet</c> then
    /// <c>look</c> at them. Tracked per-BBS on the player observation
    /// row, so re-meeting the same person later the same day is silent.
    /// Self and current party members are always skipped. Default
    /// <c>false</c> (opt-in). Wired to <see cref="Game.GreetManager"/>.
    /// </summary>
    public bool GreetPlayersWhenFirstMet { get; set; }
}
