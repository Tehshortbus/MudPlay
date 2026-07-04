namespace FujinTerm.Game.Spells;

// One spell a given class can learn/cast at a given level — the output row of
// KnownSpellCatalog. Carries the display identity (Short cast-code + full Name) plus
// the Formula needed to compute level-scaled damage / heal / duration via
// SpellCalculator.
//
// Short is the verbatim Spells.Short shortcode the player types to cast (and that
// spells / pow rows print); Name is the full Spells.Name the learn-scroll line
// reports. The Spell Book matches catalog rows against the live spells list by Short
// and against the learn-scroll signal by Name.
//
// Targets is the raw Spells.Targets scope code. It tells the party-buff caster
// whether a buff blankets the whole party in one cast (Full / Divided Party Area) or
// must be cast per member.
public readonly record struct KnownSpell(
    int Number,
    string Short,
    string Name,
    int Magery,
    int MageryLvl,
    int ReqLevel,
    int Targets,
    SpellFormulaInput Formula);
