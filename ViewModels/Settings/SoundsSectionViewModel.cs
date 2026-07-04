using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

// "Sounds" tab stub — master volume + per-event sound assignments. The audio
// backend (LibVLCSharp / NAudio / ManagedBass) is picked when the first feature
// that actually plays a file lands; the surface is here so users see the knobs.
public sealed class SoundsSectionViewModel : StubSectionViewModel
{
    public override string Id => "sounds";
    public override string Title => "Sounds";
    public override string PhaseTag => "Phase 13 (audio backend picked at implementation time)";
    public override string Description =>
        "Per-event sound cues — alarm on a tell, ding on level-up, fanfare on rare drop, etc. The audio backend " +
        "is selected when Phase 13 ships the first feature that actually plays a file (a Trigger action or an " +
        "Event-fire alert).";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Master", new[]
        {
            new StubField("Sounds enabled",  StubFieldKind.Check,  "Global mute when off."),
            new StubField("Master volume",   StubFieldKind.Slider, "0 (silent) → 100 (max)."),
            new StubField("Per-event volume overrides the master multiplier.",
                          StubFieldKind.Note, "Reads as: each event's slider scales against the master."),
        }),
        new StubGroup("Event cues", new[]
        {
            new StubField("Tell received",    StubFieldKind.Text, "Path to the file played on incoming telepath."),
            new StubField("Level-up",         StubFieldKind.Text, "Path to the file played on a level-up message."),
            new StubField("Combat death",     StubFieldKind.Text, "Path to the file played on character death."),
            new StubField("Party invite",     StubFieldKind.Text, "Path played on an inbound party invitation."),
            new StubField("Trigger fire",     StubFieldKind.Text, "Default cue for Trigger actions that don't specify a sound."),
            new StubField("Event fire",       StubFieldKind.Text, "Default cue for scheduled Events that don't specify a sound."),
        }),
    };
}
