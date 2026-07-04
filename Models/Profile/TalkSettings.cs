namespace FujinTerm.Models.Profile;

// Per-character "Talk" settings — engine-level policy for
// Game.Remote.RemoteCommandManager. Stored as the "Talk" entry in
// CharacterProfile.Settings.
//
// Per-channel disable rows govern only the three channels
// Game.Remote.RemoteChannel accepts inbound @-commands from. Gossip / Auction /
// Broadcast / Yell are hard-excluded engine-wide; no per-user toggle would
// change that, so they aren't fields.
public sealed class TalkSettings
{
    // Hard kill-switch above every per-channel + per-player permission. When
    // true the engine ignores every inbound @-command — no handler fires, no
    // reply is sent. Default false.
    public bool DisallowAllRemoteCommands { get; set; }

    // Overrides the base @party <sub> whitelist for every party member, not just
    // the leader — when true an inbound @party is a no-op no matter who in the
    // party sends it. Useful when solo-questing inside a party where you don't
    // want party directives steering this character. Default false (whitelist
    // active).
    public bool DisallowPartyCommands { get; set; }

    // Drop @-commands arriving on telepaths / pages.
    public bool DisallowRemoteFromTelepaths { get; set; }

    // Drop @-commands arriving on the gang/guild channel.
    public bool DisallowRemoteFromGangpaths { get; set; }

    // Drop @-commands arriving on the local say channel.
    public bool DisallowRemoteFromLocal { get; set; }

    // When true, the engine sends RemoteCommandFailureMessage back to the
    // originator on per-player denial / unknown-command / party-whitelist
    // denial. Hard-blocks (reroll / suicide) and the user-disabled paths
    // (master / per-channel) stay silent regardless — no point informing the
    // sender that a channel they don't even know is excluded actually was.
    // Default true.
    public bool WarnOnInvalidRemoteCommand { get; set; } = true;

    // Reply text sent back to the originator when an @-command is denied or
    // unrecognised. Variable substitution isn't applied — the string is sent
    // verbatim. Default "command invalid or not allowed". The
    // Game.Remote.RemoteCommandManager wraps every reply in { } braces on the
    // wire (the curly-brace meta-line convention), so this string is bare text —
    // adding literal braces here would double them.
    public string RemoteCommandFailureMessage { get; set; } = "command invalid or not allowed";

    // When true, the first time we spot a non-party player in the room each
    // local-calendar day — whether from an Also here: list or a live arrival
    // line — the client sends greet then look at them. Tracked per-BBS on the
    // player observation row, so re-meeting the same person later the same day
    // is silent. Self and current party members are always skipped. Default
    // false (opt-in). Wired to Game.GreetManager.
    public bool GreetPlayersWhenFirstMet { get; set; }
}
