namespace FujinTerm.Models.GameData;

/// <summary>
/// User-defined incoming-text pattern → action(s). Per-character;
/// persisted on <see cref="Profile.CharacterProfile"/>. Loaded into the
/// <see cref="Services.TriggerEngine"/> on profile load; the engine
/// subscribes to <see cref="Services.MessageRouter"/> and fires the
/// configured actions when patterns match.
/// </summary>
/// <param name="Name">Display name shown in the Triggers tab.</param>
/// <param name="Enabled">Master per-trigger on / off — toggleable without deleting.</param>
/// <param name="MatchType">Literal substring vs wildcard vs regex.</param>
/// <param name="Pattern">The pattern itself, in the syntax indicated by <see cref="MatchType"/>.</param>
/// <param name="Scope">Optional channel/source filter; <c>null</c> matches any line.</param>
/// <param name="Actions">Ordered actions fired on match. Variable substitution applies before each action.</param>
public sealed record Trigger(
    string Name,
    bool Enabled,
    TriggerMatchType MatchType,
    string Pattern,
    TriggerScope Scope,
    IReadOnlyList<TriggerAction> Actions);

/// <summary>What syntax the <see cref="Trigger.Pattern"/> uses.</summary>
public enum TriggerMatchType
{
    /// <summary>Substring match — fastest, no metacharacters.</summary>
    Literal,
    /// <summary>Wildcard match — <c>*</c> and <c>?</c> glob-style.</summary>
    Wildcard,
    /// <summary>Full PCRE-like regex with named capture groups.</summary>
    Regex,
}

/// <summary>Which subset of incoming lines the trigger considers.</summary>
public enum TriggerScope
{
    /// <summary>Every line emitted by the LineExtractor.</summary>
    AnyLine,
    /// <summary>Lines classified by ChatRouter as a chat channel (any).</summary>
    ChatOnly,
    /// <summary>Server-emitted system notices.</summary>
    SystemOnly,
    /// <summary>Specific chat channel — Gossip.</summary>
    Gossip,
    /// <summary>Specific chat channel — Telepath.</summary>
    Telepath,
    /// <summary>Specific chat channel — Gangpath.</summary>
    Gangpath,
    /// <summary>Specific chat channel — Say (local-room talk).</summary>
    Say,
    /// <summary>Specific chat channel — Broadcast.</summary>
    Broadcast,
}

/// <summary>One action fired when a <see cref="Trigger"/> matches.</summary>
/// <param name="Kind">Which action verb to perform.</param>
/// <param name="Payload">
/// Kind-specific payload — command string for <see cref="TriggerActionKind.SendCommand"/>,
/// path for <see cref="TriggerActionKind.PlaySound"/>, etc. Variable
/// substitution (<c>$captureName</c>) applied before use.
/// </param>
/// <param name="VariableName">Used only by <see cref="TriggerActionKind.SetVariable"/> / <see cref="TriggerActionKind.ClearVariable"/>.</param>
public sealed record TriggerAction(
    TriggerActionKind Kind,
    string Payload,
    string? VariableName = null);

/// <summary>What a <see cref="TriggerAction"/> does.</summary>
public enum TriggerActionKind
{
    /// <summary>Send <see cref="TriggerAction.Payload"/> verbatim (after variable substitution) to the game.</summary>
    SendCommand,
    /// <summary>Surface an OS notification (or in-app toast on platforms lacking it).</summary>
    ShowNotification,
    /// <summary>Play the audio file at <see cref="TriggerAction.Payload"/>.</summary>
    PlaySound,
    /// <summary>Assign <see cref="TriggerAction.Payload"/> to <see cref="TriggerAction.VariableName"/>.</summary>
    SetVariable,
    /// <summary>Drop the named variable from the in-memory store.</summary>
    ClearVariable,
}
