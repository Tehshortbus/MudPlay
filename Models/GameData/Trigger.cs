namespace FujinTerm.Models.GameData;

// User-defined incoming-text pattern → response. Per-character; persisted
// on CharacterProfile. Loaded into TriggerEngine on profile load; the
// engine subscribes to LineExtractor, ChatRouter, and LogService —
// filtering each emitted line by Scope, matching with MatchType + Pattern,
// capturing {name} placeholders into TriggerEngine.Variables, then
// dispatching Response + the optional SoundFile.
//
// Scope selects which subset of incoming lines this trigger considers.
// MatchType is Literal (with * wildcards and {name} captures) or full
// regex; Pattern is in the syntax MatchType indicates. Response is sent to
// the game on match — variable substitution via {name} is applied first,
// multi-step via ^M or ; (same syntax as macros); a blank string is valid
// and sends a bare carriage return. SoundFile is an optional path fired on
// match (currently a no-op + log until sound playback lands).
//
// Location is where this trigger persists on disk:
//   GameData (default) — saved into Data/game data/{set}/triggers.json next
//     to the MDB tables. Travels with the set; every character on the same
//     realm sees it. Right choice for chase / trap / NPC-event patterns
//     that anchor to game-data records.
//   Profile — saved on CharacterProfile.Triggers. Follows the character
//     across realms. Right choice for personal alerts (your name was
//     mentioned, a specific friend logged on, etc.).
// The runtime dispatch is location-agnostic — both buckets merge into the
// live TriggerEngine.Triggers collection.
public sealed record Trigger(
    string Name,
    bool Enabled,
    TriggerScope Scope,
    TriggerMatchType MatchType,
    string Pattern,
    string Response,
    string? SoundFile = null,
    TriggerLocation Location = TriggerLocation.GameData);

// What syntax Trigger.Pattern uses.
public enum TriggerMatchType
{
    // Substring / wildcard match. * matches any run of characters (no
    // capture). {name} matches a run of characters and binds it to name in
    // the shared variable cache.
    Literal,
    // Full PCRE-flavoured regex (.NET). Named groups (?<name>…) populate
    // {name} in the shared variable cache.
    Regex,
}

// Which on-disk file a Trigger persists into. Triggers tab shows the value
// as a "Location" column; the edit dialog exposes a dropdown so the user
// can move triggers between buckets at edit time. Default is GameData —
// most of the seeded defaults are chase / trap / NPC-event patterns that
// belong with the set.
public enum TriggerLocation
{
    // Saved at Data/game data/{set}/triggers.json — travels with the set.
    GameData,
    // Saved on CharacterProfile.Triggers — travels with the character.
    Profile,
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
    // FujinTerm's own LogService emissions.
    SystemLog,
}
