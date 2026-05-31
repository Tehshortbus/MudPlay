namespace FujinTerm.Models.GameData;

/// <summary>
/// User-defined incoming-text pattern → response. Per-character;
/// persisted on <see cref="Profile.CharacterProfile"/>. Loaded into
/// <see cref="Services.TriggerEngine"/> on profile load; the engine
/// subscribes to <see cref="Terminal.LineExtractor"/>,
/// <see cref="Game.ChatRouter"/>, and <see cref="Services.LogService"/>
/// — filtering each emitted line by <see cref="Scope"/>, matching with
/// <see cref="MatchType"/> + <see cref="Pattern"/>, capturing
/// <c>{name}</c> placeholders into <see cref="Services.TriggerEngine.Variables"/>,
/// then dispatching <see cref="Response"/> + the optional
/// <see cref="SoundFile"/>.
/// </summary>
/// <param name="Name">Display name shown in the Triggers tab.</param>
/// <param name="Enabled">Master per-trigger on / off — toggleable without deleting.</param>
/// <param name="Scope">Which subset of incoming lines this trigger considers.</param>
/// <param name="MatchType">Literal (with <c>*</c> wildcards and <c>{name}</c> captures) vs full regex.</param>
/// <param name="Pattern">The pattern in the syntax indicated by <see cref="MatchType"/>.</param>
/// <param name="Response">
/// Sent to the game on match. Variable substitution via <c>{name}</c>
/// is applied first. Multi-step via <c>^M</c> or <c>;</c> (same syntax
/// as macros). Blank string is valid — sends a bare carriage return.
/// </param>
/// <param name="SoundFile">Optional path to a sound file fired on match. Phase 13 plays it; today it's a no-op + log.</param>
public sealed record Trigger(
    string Name,
    bool Enabled,
    TriggerScope Scope,
    TriggerMatchType MatchType,
    string Pattern,
    string Response,
    string? SoundFile = null);

/// <summary>What syntax <see cref="Trigger.Pattern"/> uses.</summary>
public enum TriggerMatchType
{
    /// <summary>
    /// Substring / wildcard match. <c>*</c> matches any run of characters
    /// (no capture). <c>{name}</c> matches a run of characters and binds
    /// it to <c>name</c> in the shared variable cache.
    /// </summary>
    Literal,
    /// <summary>
    /// Full PCRE-flavoured regex (.NET). Named groups <c>(?&lt;name&gt;…)</c>
    /// populate <c>{name}</c> in the shared variable cache.
    /// </summary>
    Regex,
}

/// <summary>Which subset of incoming lines a trigger considers.</summary>
public enum TriggerScope
{
    /// <summary>Game-emitted lines that <see cref="Game.ChatRouter"/> does not classify as chat.</summary>
    GameMessages,
    /// <summary>Any chat line — catch-all for every chat channel.</summary>
    ChatAny,
    /// <summary>Specific chat channel — Say (local-room talk).</summary>
    ChatSay,
    /// <summary>Specific chat channel — Yell.</summary>
    ChatYell,
    /// <summary>Specific chat channel — Gossip.</summary>
    ChatGossip,
    /// <summary>Specific chat channel — Telepath.</summary>
    ChatTelepath,
    /// <summary>Specific chat channel — Gangpath.</summary>
    ChatGangpath,
    /// <summary>Specific chat channel — Broadcast.</summary>
    ChatBroadcast,
    /// <summary>FujinTerm's own <see cref="Services.LogService"/> emissions.</summary>
    SystemLog,
}
