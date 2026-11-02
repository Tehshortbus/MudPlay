using System.Text.Json.Serialization;

namespace MudPlay.Models.GameData;

// User-defined incoming-text pattern → response. Kept on the character, the realm
// or the BBS (Level). Loaded into TriggerEngine on profile load; the
// engine subscribes to LineExtractor, ChatRouter, and LogService —
// filtering each emitted line by Scope, matching with MatchType + Pattern,
// capturing {name} / {1} placeholders into the trigger-wildcard store
// (TriggerEngine.Variables — trigger-only, not shared with aliases/macros),
// then dispatching Response + the optional SoundFile.
//
// Scope selects which subset of incoming lines this trigger considers.
// MatchType is Literal (with * wildcards and {name} / {1} captures) or full
// regex; Pattern is in the syntax MatchType indicates. Response is sent to
// the game on match — wildcard substitution via {name} is applied first,
// multi-step via ^M or ; (same syntax as macros); a blank string is valid
// and sends a bare carriage return. SoundFile is an optional sound file
// played on match, gated and levelled by the Trigger cue on Settings → Sounds.
//
// A character's own triggers are saved on CharacterProfile.Triggers; a realm's and a
// BBS's in a file every character on it shares (SharedTriggerFile).
public sealed record Trigger(
    string Name,
    bool Enabled,
    TriggerScope Scope,
    TriggerMatchType MatchType,
    string Pattern,
    string Response,
    string? SoundFile = null)
{
    // Where the trigger is kept, and so who it fires for. Not written out: the file
    // it is kept in says.
    [JsonIgnore] public TriggerLevel Level { get; init; }

    // A field a hand-written or carried-in trigger file leaves out reads back null.
    // Blank is what the editor saves: a blank pattern never fires, a blank response
    // sends a bare carriage return.
    public Trigger WithBlanksForMissing() =>
        this with { Name = Name ?? "", Pattern = Pattern ?? "", Response = Response ?? "" };
}

// Where a trigger is kept: on the character, with the realm (every character
// playing it) or with the BBS (every character on it). Of triggers sharing a name,
// the nearest is the one used, as with settings (user, 2026-10-30), so a character
// can change or switch off a shared trigger for itself with its own copy.
public enum TriggerLevel { Character, Realm, Bbs }

// What syntax Trigger.Pattern uses.
public enum TriggerMatchType
{
    // Substring / wildcard match. * matches any run of characters (no
    // capture). {name} (or {1}, {2}…) matches a run of characters and binds it
    // to that name in the trigger-wildcard store.
    Literal,
    // Full PCRE-flavoured regex (.NET). Named groups (?<name>…) populate
    // {name} in the trigger-wildcard store.
    Regex,
}

// Which subset of incoming lines a trigger considers.
public enum TriggerScope
{
    // Game-emitted lines that ChatRouter does not classify as chat.
    GameMessages,
    // Any chat line — catch-all for every chat channel.
    ChatAny,
    // Specific chat channel — Say (local-room talk).
    ChatSay,
    // Specific chat channel — Yell.
    ChatYell,
    // Specific chat channel — Gossip.
    ChatGossip,
    // Specific chat channel — Telepath.
    ChatTelepath,
    // Specific chat channel — Gangpath.
    ChatGangpath,
    // Specific chat channel — Broadcast.
    ChatBroadcast,
    // MudPlay's own LogService emissions.
    SystemLog,
}
