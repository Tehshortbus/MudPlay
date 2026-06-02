namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Talk" settings — engine-level policy for the Phase 6
/// <see cref="Game.Remote.RemoteCommandManager"/>. Stored as the
/// <c>"Talk"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// AFK Mode fields (auto-AFK timer, AFK response message, minimise →
/// AFK, input cancels AFK) live with this section in the UI but
/// aren't persisted here yet — their consumer lands in Phase 11.
/// They'll get their own fields when that phase opens.
/// </para>
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
    /// Overrides the base <c>@party &lt;sub&gt;</c> whitelist. Useful when
    /// solo-questing inside a party where you don't want the leader's
    /// <c>@party</c> directives steering this character. Default
    /// <c>false</c> (whitelist active).
    /// </summary>
    public bool DisallowPartyCommandsFromLeader { get; set; }

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
    /// <c>"{command invalid or not allowed}"</c> (the literal braces
    /// are part of the message — MegaMUD-flavor curly-brace prefix
    /// commonly used for client-side meta lines).
    /// </summary>
    public string RemoteCommandFailureMessage { get; set; } = "{command invalid or not allowed}";
}
