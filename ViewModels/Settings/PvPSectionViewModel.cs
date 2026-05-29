using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "PvP" tab stub — flee / hangup / attack / chase rules when a hostile
/// player enters the room.
/// </summary>
public sealed class PvPSectionViewModel : StubSectionViewModel
{
    public override string Id => "pvp";
    public override string Title => "PvP";
    public override string PhaseTag => "Phase 13 PR 13.G (PvPManager)";
    public override string Description =>
        "What to do when a hostile non-party player shows up — flee, hang up, fight back, or chase. Per-player " +
        "friend / enemy flags live on the Phase 5 Players tab; this tab is the engine-level reaction policy.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Flee response", new[]
        {
            new StubField("Flee when hostile player enters", StubFieldKind.Check,   "Master toggle for the flee policy."),
            new StubField("Flee if HP below",                StubFieldKind.Numeric, "Hard threshold — flee regardless of attacker.", "%"),
            new StubField("Rooms between flee and hangup",   StubFieldKind.Numeric, "Run this many rooms away before dropping the line.", "rooms"),
            new StubField("Hang up after fleeing",           StubFieldKind.Check,   "Disconnect once the buffer is reached."),
        }),
        new StubGroup("Fight-back", new[]
        {
            new StubField("Allow chase",         StubFieldKind.Check,   "Pursue when a flagged enemy flees the room first."),
            new StubField("Re-engage if HP above", StubFieldKind.Numeric, "Stop fleeing and turn around once HP recovers.", "%"),
            new StubField("Attack on sight whitelist", StubFieldKind.Note,
                          "Per-player attack-on-sight is set on the Players tab (Phase 5 PR 5.19); this is the engine that consumes it."),
        }),
        new StubGroup("Reconnect", new[]
        {
            new StubField("Auto-reconnect after PvP hangup", StubFieldKind.Check,   "Dial back once the cooldown timer expires."),
            new StubField("Cooldown",                        StubFieldKind.Numeric, "How long to wait before re-dialing.", "s"),
        }),
    };
}
