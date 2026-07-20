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

    // When true, seeing the wire line "<name> is looking at you." makes the
    // client send `look <name>` straight back — a reactive social mirror. No
    // dedup (each look-at gets a look-back); self is skipped, party members are
    // not. Default false (opt-in). Wired to Game.PlayerLookManager.
    public bool LookBackWhenLookedAt { get; set; }

    // When true, any non-party player walking into our room triggers a
    // `look <name>` so we learn/refresh their kit. One look per arrival; self
    // and current party members are skipped. Default false (opt-in). Wired to
    // Game.PlayerLookManager.
    public bool LookAtPlayersOnArrival { get; set; }

    // Persist the Conversation window to Data/Logs/<char>.<bbs>.talk.log so it
    // survives restarts. Default on. Wired to Services.SessionLogService.
    public bool LogConversations { get; set; } = true;

    // Persist the Session Stats → Transaction history to
    // Data/Logs/<char>.<bbs>.transactions.log. Default on.
    public bool LogTransactions { get; set; } = true;

    // Rolling line cap shared by both logs — the file keeps only its last N
    // lines, matching the in-memory window's scrollback feel. Default 2000.
    public int LogMaxLines { get; set; } = 2000;

    // Conversation window font override. Empty string means "use the window's
    // default row font" — the bundled JetBrains Mono (the {default} option).
    // Holds an avares:// FontFamily URI.
    public string ConvoFont { get; set; } = "";

    // Conversation window message font size. 0 means "use the window's built-in
    // default size" (the {default} option).
    public double ConvoFontSize { get; set; }

    // Per-channel Conversation colours, keyed by the seven filter-toggle group
    // names (Gossip / Local / Telepath / Gangpath / Broadcast / Yell /
    // RealmEvent — both telepath directions and Server/RealmEvent collapse onto
    // their group). Absent channels, and null members within a present one, fall
    // back to the built-in theme brushes. Lets the user recolour both the accent
    // (channel tag / speaker / toggle) and the message-body text.
    public Dictionary<string, ChannelColor>? ChannelColors { get; set; }
}

// A user-picked color override for one Conversation channel: the color of its
// toggle in the window header (Label) and the color of its message text (Text).
// Null members fall back to the built-in theme brush for that channel.
public sealed class ChannelColor
{
    public string? Label { get; set; }
    public string? Text { get; set; }
}
