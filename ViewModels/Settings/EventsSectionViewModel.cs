using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Events" tab stub — scheduled / lifecycle events (Logon / Logoff /
/// Re-log / AtTime / Every) per Phase 8. The list itself is
/// per-character and lives on the profile. Promoted to a bespoke
/// wired section in PR 8.3.
/// </summary>
public sealed class EventsSectionViewModel : StubSectionViewModel
{
    public override string Id => "events";
    public override string Title => "Events";
    public override string PhaseTag => "Phase 8 (EventManager + Events tab promotion)";
    public override string Description =>
        "User-defined scheduled or lifecycle events — fire at a specific clock time, on a recurring cadence, or " +
        "on connection-state changes. Actions include walking to a room, starting a saved loop or auto-lair setup, " +
        "or sending a free-form command (with ^M / ; multi-fire).";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Event list", new[]
        {
            new StubField("New event…",  StubFieldKind.Button, "Phase 8 PR 8.3 — opens the per-event editor dialog."),
            new StubField("Edit selected…", StubFieldKind.Button, "Phase 8 PR 8.3."),
            new StubField("Remove selected", StubFieldKind.Button, "Phase 8 PR 8.3."),
            new StubField("Empty list — events are per-character and persist on the profile.",
                          StubFieldKind.Note, "List preview here in PR 8.3."),
        }),
        // Header omitted (empty Header → StubSectionView's
        // IsNotNullOrEmpty gate hides the SectionHeader TextBlock).
        new StubGroup("", new[]
        {
            new StubField("Only fire while AFK by default", StubFieldKind.Check, "Sets the AFK-only flag on newly-created events."),
            new StubField("Disable all events while disconnected", StubFieldKind.Check,
                          "Pauses recurring `Every` timers on disconnect; AtTime never catches up after a missed window."),
        }),
    };
}
