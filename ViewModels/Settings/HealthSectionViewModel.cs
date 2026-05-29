using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Health" tab stub — per-character HP and Mana/Kai threshold knobs
/// the HealthManager + CastingDirector poll between rounds. Per
/// MegaMUD's reference layout: parallel columns for HP and MA/KAI, plus
/// heal-cast thresholds (rest vs combat) so the casting director has a
/// per-character source of truth without flipping back to Spells.
/// </summary>
public sealed class HealthSectionViewModel : StubSectionViewModel
{
    public override string Id => "health";
    public override string Title => "Health";
    public override string PhaseTag => "Phase 13 PR 13.B (HealthManager) + 13.D (CastingDirector heal thresholds)";
    public override string Description =>
        "Two parallel columns, one for HP and one for Mana / Kai. Each column has the same shape: rest-max, " +
        "rest-if-below, heal (rest), heal (combat), run-if-below, plus a column-specific extra (HP: hang-if-below; " +
        "MA: bless-if-above). Heal-cast thresholds are passed straight to the CastingDirector; rest / hang / run " +
        "are HealthManager's own.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Health (HP)", new[]
        {
            new StubField("Rest max",          StubFieldKind.Numeric, "Stop resting once HP reaches this percentage.", "%"),
            new StubField("Rest if below",     StubFieldKind.Numeric, "Auto-rest trigger threshold.", "%"),
            new StubField("Heal (rest)",       StubFieldKind.Numeric, "Cast heal spell during rest when HP drops below this.", "%"),
            new StubField("Heal (combat)",     StubFieldKind.Numeric, "Cast heal spell during combat when HP drops below this.", "%"),
            new StubField("Run if below",      StubFieldKind.Numeric, "Flee threshold.", "%"),
            new StubField("Hang up if below",  StubFieldKind.Numeric, "Drop the line when HP falls to this percentage.", "%"),
        }),
        new StubGroup("Mana / Kai", new[]
        {
            new StubField("Rest max",          StubFieldKind.Numeric, "Stop resting once MA / KAI reaches this percentage.", "%"),
            new StubField("Rest if below",     StubFieldKind.Numeric, "Auto-rest trigger threshold for MA / KAI.", "%"),
            new StubField("Heal (rest)",       StubFieldKind.Numeric, "Cast meditate / regen-mana spell during rest when MA drops below this.", "%"),
            new StubField("Heal (combat)",     StubFieldKind.Numeric, "Cast regen-mana spell during combat when MA drops below this.", "%"),
            new StubField("Run if below",      StubFieldKind.Numeric, "Flee threshold when caster runs out of mana to cast.", "%"),
            new StubField("Bless if above",    StubFieldKind.Numeric, "Re-cast party / self buffs when MA recovers past this point.", "%"),
        }),
        new StubGroup("Meditation", new[]
        {
            new StubField("Use 'meditate' ability",  StubFieldKind.Check, "Available on a handful of classes — uses the class-specific cmd."),
            new StubField("Meditate before resting", StubFieldKind.Check, "Sit and meditate first when MA is also low."),
            new StubField("Heal poll period",        StubFieldKind.Numeric, "How often the HealthManager re-evaluates between rounds.", "s"),
        }),
        new StubGroup("Resting commands", new[]
        {
            new StubField("Pre-rest command",  StubFieldKind.Text, "Sent right before the rest command (e.g. `peer`)."),
            new StubField("Post-rest command", StubFieldKind.Text, "Sent right after standing up (e.g. `look`)."),
            new StubField("Party wait command",   StubFieldKind.Text, "Sent to the party when we need a hold (default `@wait`)."),
            new StubField("Party resume command", StubFieldKind.Text, "Sent to the party when we're ready to roll again (default `@ok`)."),
        }),
    };
}
