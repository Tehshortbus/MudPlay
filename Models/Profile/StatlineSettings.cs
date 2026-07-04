namespace FujinTerm.Models.Profile;

// Per-character statline configuration. Wraps the argument we send after
// set statline so the game prints prompts in the format the client knows how to
// parse. Persisted as the "Statline" entry in CharacterProfile.Settings.
//
// MegaMUD's two canonical commands: set statline full uses the class-default
// format (mana: [HP=%h,MA=%m]: %r; kai: [HP=%h,KAI=%m]: %r; hp-only:
// [HP=%h]: %r). set statline full custom <...> renders a user-authored wildcard
// string. Anything else is passed through verbatim — the game accepts a raw
// wildcard string after set statline too.
//
// This one string is the single source of truth for the statline: it's both
// what we send to the BBS and what FujinTerm.Game.StatlinePromptRegexBuilder
// compiles the prompt parser from, so the editor and the on-wire prompt can't
// drift.
public sealed class StatlineSettings
{
    // The argument that follows set statline on the wire. Common values:
    // "full", "full custom [HP=%h]: %r", or a raw wildcard string. null / empty
    // means "use full" — the editor displays this as the default.
    public string? Command { get; set; }
}
