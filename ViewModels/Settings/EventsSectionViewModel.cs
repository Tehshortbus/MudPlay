using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Events" tab stub — scheduled / lifecycle events (AtTime, Every,
/// Logon / Logoff / Re-log) per Phase 11. The list itself is per-character
/// and lives on the profile.
/// </summary>
public sealed class EventsSectionViewModel : StubSectionViewModel
{
    public override string Id => "events";
    public override string Title => "Events";
    public override string PhaseTag => "Phase 11 (EventManager + EventsListDialog)";
    public override string Description =>
        "User-defined scheduled or lifecycle events — fire at a specific clock time, on a recurring cadence, or " +
        "on connection-state changes. Actions include sending a command, running a macro, playing a sound, " +
        "walking somewhere, or starting / switching a loop.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Event list", new[]
        {
            new StubField("New event…",  StubFieldKind.Button, "Phase 11 — opens the per-event editor dialog."),
            new StubField("Edit selected…", StubFieldKind.Button, "Phase 11."),
            new StubField("Remove selected", StubFieldKind.Button, "Phase 11."),
            new StubField("Empty list — events are per-character and persist on the profile.",
                          StubFieldKind.Note, "List preview here in Phase 11."),
        }),
        new StubGroup("Defaults", new[]
        {
            new StubField("Only fire while AFK by default", StubFieldKind.Check, "Sets the AFK-only flag on newly-created events."),
            new StubField("Disable all events while disconnected", StubFieldKind.Check,
                          "Pauses recurring `Every` timers on disconnect; AtTime never catches up after a missed window."),
        }),
    };
}
