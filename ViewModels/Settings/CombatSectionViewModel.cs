using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Combat" tab stub — weapon-swap matrix (normal + alternate + BS +
/// off-hand slots), per-character combat options, and the spell-combat
/// table (multi-attack / debuff single / debuff AOE / normal attack
/// spell / alternate attack spell, each with their own column knobs).
/// </summary>
/// <remarks>
/// Off-hand slots stay even though MegaMUD's reference only shows BS /
/// Shield — they'll matter once the Phase 9 Equipment Manager ships
/// per-set off-hand picks. Final shape gets re-evaluated then.
/// </remarks>
public sealed class CombatSectionViewModel : StubSectionViewModel
{
    public override string Id => "combat";
    public override string Title => "Combat";
    public override string PhaseTag => "Phase 13 PR 13.A (CombatManager)";
    public override string Description =>
        "Auto-attack engine config — weapon swap, target gating, and which spells fire in which combat role. " +
        "Spell-combat has five rows: multi-attack, debuff (single), debuff (AOE), normal attack spell, alternate " +
        "attack spell. Each row carries its own Min-Enemies / Max-Damage / Max-Cast / Required-Mana column.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Weapon combat", new[]
        {
            new StubField("Normal attack command", StubFieldKind.Text, "Command sent each round to swing (default `a`)."),
            new StubField("Normal weapon",         StubFieldKind.Combo, "Primary weapon — main-hand."),
            new StubField("Normal off-hand",       StubFieldKind.Combo, "Off-hand pairing for the normal weapon."),
            new StubField("Alternate weapon",      StubFieldKind.Combo, "Swap target — used when CombatManager decides to switch (see wiring notes)."),
            new StubField("Alternate off-hand",    StubFieldKind.Combo, "Off-hand pairing for the alternate weapon."),
            new StubField("BS weapon",             StubFieldKind.Combo, "Equipped for the BS attempt round."),
            new StubField("Shield",                StubFieldKind.Combo, "Defensive off-hand swapped in when shield-up logic triggers."),
            new StubField("Use shield with BS weapon",        StubFieldKind.Check, "Keep shield equipped alongside BS attempts."),
            new StubField("Use normal weapon for attack spells", StubFieldKind.Check, "Don't bother swapping to a caster off-hand for spell rounds."),
        }),
        new StubGroup("Options", new[]
        {
            new StubField("Do BS attacks",        StubFieldKind.Check, "Off by default. Stealth-window gating handled by Phase 13 PR 13.F."),
            new StubField("Don't BS if multi-attack", StubFieldKind.Check, "Skip BS when the multi-attack room spell is going off this round."),
            new StubField("Run if BS fails",      StubFieldKind.Check, "Trigger flee behaviour on a failed BS roll."),
            new StubField("Attack all monsters",  StubFieldKind.Check, "Opposite of polite — engages every hostile regardless of who else is fighting."),
            new StubField("Polite attacks",       StubFieldKind.Check, "Skip rooms where non-party players are already engaged."),
            new StubField("Min. monsters",        StubFieldKind.Numeric, "Skip the room if fewer than this many hostiles are present."),
            new StubField("Max. monsters",        StubFieldKind.Numeric, "Skip the room if more than this many hostiles are present."),
            new StubField("Run distance",         StubFieldKind.Numeric, "Rooms to flee before re-evaluating.", "rooms"),
        }),
        new StubGroup("Spell combat — Multi-attack (room spell)", new[]
        {
            new StubField("Spell",          StubFieldKind.Combo,   "Multi-target room spell to fire."),
            new StubField("Min enemies",    StubFieldKind.Numeric, "Only cast when at least this many hostiles are present."),
            new StubField("Max damage cap", StubFieldKind.Numeric, "Skip if the projected damage would over-kill (waste mana)."),
            new StubField("Max consecutive casts", StubFieldKind.Numeric, "Cap on back-to-back fires."),
            new StubField("Required mana floor",   StubFieldKind.Numeric, "Skip if current MA would drop below this.", "%"),
        }),
        new StubGroup("Spell combat — Debuff (single target)", new[]
        {
            new StubField("Spell",          StubFieldKind.Combo,   "Single-target debuff (e.g. weakness, slow)."),
            new StubField("Min enemies",    StubFieldKind.Numeric, "Skip debuffing if the room is below this count (not worth the round)."),
            new StubField("Max damage cap", StubFieldKind.Numeric, "Projection cap for tuning."),
            new StubField("Max casts per fight", StubFieldKind.Numeric, "Re-cast cap for the active room."),
            new StubField("Required mana floor", StubFieldKind.Numeric, "Skip if MA would drop below this.", "%"),
        }),
        new StubGroup("Spell combat — Debuff (AOE)", new[]
        {
            new StubField("Spell",          StubFieldKind.Combo,   "Area-effect debuff (e.g. blind-room, curse-room)."),
            new StubField("Min enemies",    StubFieldKind.Numeric, "Only cast when at least this many hostiles are present."),
            new StubField("Max damage cap", StubFieldKind.Numeric, "Projection cap."),
            new StubField("Max consecutive casts", StubFieldKind.Numeric, "Cap on back-to-back fires."),
            new StubField("Required mana floor", StubFieldKind.Numeric, "Skip if MA would drop below this.", "%"),
        }),
        new StubGroup("Spell combat — Normal attack spell", new[]
        {
            new StubField("Spell",          StubFieldKind.Combo,   "Primary single-target damage spell."),
            new StubField("Min enemies",    StubFieldKind.Numeric, "Threshold for picking this over a different combat option."),
            new StubField("Max damage cap", StubFieldKind.Numeric, "Projection cap."),
            new StubField("Max consecutive casts", StubFieldKind.Numeric, "Cap on back-to-back fires."),
            new StubField("Required mana floor", StubFieldKind.Numeric, "Skip if MA would drop below this.", "%"),
        }),
        new StubGroup("Spell combat — Alternate attack spell", new[]
        {
            new StubField("Spell",          StubFieldKind.Combo,   "Fallback / second-choice damage spell — used when normal can't fire (insufficient mana, monster resistant, etc.)."),
            new StubField("Min enemies",    StubFieldKind.Numeric, "Threshold for picking this over a different combat option."),
            new StubField("Max damage cap", StubFieldKind.Numeric, "Projection cap."),
            new StubField("Max consecutive casts", StubFieldKind.Numeric, "Cap on back-to-back fires."),
            new StubField("Required mana floor", StubFieldKind.Numeric, "Skip if MA would drop below this.", "%"),
        }),
    };
}
