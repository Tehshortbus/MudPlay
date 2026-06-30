namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character statline configuration. Wraps the argument we send
/// after <c>set statline</c> so the game prints prompts in the format
/// the client knows how to parse. Persisted as the <c>"Statline"</c>
/// entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// MegaMUD's two canonical commands:
/// <c>set statline full</c> uses the class-default format
/// (mana: <c>[HP=%h,MA=%m]: %r</c>; kai: <c>[HP=%h,KAI=%m]: %r</c>;
/// hp-only: <c>[HP=%h]: %r</c>). <c>set statline full custom &lt;...&gt;</c>
/// renders a user-authored wildcard string. Anything else is passed
/// through verbatim — the game accepts a raw wildcard string after
/// <c>set statline</c> too.
/// </para>
/// <para>
/// This one string is the single source of truth for the statline: it's
/// both what we send to the BBS and what
/// <see cref="FujinTerm.Game.StatlinePromptRegexBuilder"/> compiles the
/// prompt parser from, so the editor and the on-wire prompt can't drift.
/// </para>
/// </remarks>
public sealed class StatlineSettings
{
    /// <summary>
    /// The argument that follows <c>set statline</c> on the wire. Common
    /// values: <c>"full"</c>, <c>"full custom [HP=%h]: %r"</c>, or a raw
    /// wildcard string. <c>null</c> / empty means "use full" — the
    /// editor displays this as the default.
    /// </summary>
    public string? Command { get; set; }
}
