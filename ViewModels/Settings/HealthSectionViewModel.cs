using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Health" tab stub — passive HP/MA thresholds (rest / hang / run / regen).
/// Per master plan: NO spell decisions here (see Spells / Party).
/// </summary>
public sealed class HealthSectionViewModel : StubSectionViewModel
{
    public override string Id => "health";
    public override string Title => "Health";
    public override string PhaseTag => "Phase 13 PR 13.B (HealthManager)";
    public override string Description =>
        "Passive HP / MA threshold knobs the HealthManager polls every tick — rest / meditate / run / hang triggers, " +
        "plus pre / post-rest commands. Spell decisions live in the Spells (self) and Party tabs and route through " +
        "the Phase 13 CastingDirector.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Rest / heal cadence", new[]
        {
            new StubField("Rest until HP",        StubFieldKind.Numeric, "Stop resting once HP reaches this percentage.", "%"),
            new StubField("Rest if HP below",     StubFieldKind.Numeric, "Auto-rest threshold during downtime.", "%"),
            new StubField("Hang up if HP below",  StubFieldKind.Numeric, "Drop the line when HP falls to this percentage.", "%"),
            new StubField("Run away if HP below", StubFieldKind.Numeric, "Trigger flee behaviour at this HP percentage.", "%"),
            new StubField("Heal poll period",     StubFieldKind.Numeric, "How often the HealthManager re-evaluates between rounds.", "s"),
        }),
        new StubGroup("Meditation", new[]
        {
            new StubField("Meditate before resting", StubFieldKind.Check, "Sit and meditate first when MA is also low."),
            new StubField("Use 'meditate' ability",  StubFieldKind.Check, "Available on a handful of classes — uses the class-specific cmd."),
        }),
        new StubGroup("Pre / post-rest commands", new[]
        {
            new StubField("Pre-rest command",  StubFieldKind.Text, "Sent right before the rest command (e.g. `peer`)."),
            new StubField("Post-rest command", StubFieldKind.Text, "Sent right after standing up (e.g. `look`)."),
        }),
    };
}
