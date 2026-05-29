using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Party" tab stub — party-coordination knobs (rank, par cadence,
/// wait windows) plus the party-cast spell roles (minor/major heal,
/// 4 bless slots with timeouts) and the in-party behaviour flags.
/// </summary>
public sealed class PartySectionViewModel : StubSectionViewModel
{
    public override string Id => "party";
    public override string Title => "Party";
    public override string PhaseTag => "Phase 6 (PartyManager) + Phase 13 PR 13.D (CastingDirector — party)";
    public override string Description =>
        "Party-coordination knobs plus the party-cast spell picks. Minor + major heal both pick their own spell " +
        "and threshold so the casting director can pick the cheap heal until things get serious. Bless slots take " +
        "an optional timeout — re-cast cadence rather than only-when-missing.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Rank", new[]
        {
            new StubField("Position in party", StubFieldKind.Combo,
                          "Front / Mid / Back — drives default target order and where the @health prompt expects you to be."),
        }),
        new StubGroup("Party heal", new[]
        {
            new StubField("Minor heal spell",       StubFieldKind.Combo,   "Cheap heal used at the upper threshold."),
            new StubField("Minor heal at HP",       StubFieldKind.Numeric, "Cast Minor when any member drops below this.", "%"),
            new StubField("Major heal spell",       StubFieldKind.Combo,   "Expensive heal used at the lower threshold."),
            new StubField("Major heal at HP",       StubFieldKind.Numeric, "Cast Major when any member drops below this (overrides Minor).", "%"),
            new StubField("Request healing at HP",  StubFieldKind.Numeric, "Send @health to the party healer at this HP percentage.", "%"),
        }),
        new StubGroup("Party bless", new[]
        {
            new StubField("Bless 1",         StubFieldKind.Combo,   "Party-buff slot #1."),
            new StubField("Bless 1 timeout", StubFieldKind.Numeric, "Re-cast cadence — 0 means cast only when missing.", "s"),
            new StubField("Bless 2",         StubFieldKind.Combo,   "Party-buff slot #2."),
            new StubField("Bless 2 timeout", StubFieldKind.Numeric, "Re-cast cadence.", "s"),
            new StubField("Bless 3",         StubFieldKind.Combo,   "Party-buff slot #3."),
            new StubField("Bless 3 timeout", StubFieldKind.Numeric, "Re-cast cadence.", "s"),
            new StubField("Bless 4",         StubFieldKind.Combo,   "Party-buff slot #4."),
            new StubField("Bless 4 timeout", StubFieldKind.Numeric, "Re-cast cadence.", "s"),
        }),
        new StubGroup("Party cure priority", new[]
        {
            new StubField("Freedom (paralyze)", StubFieldKind.Combo, "Cure-paralyze spell to apply to teammates."),
            new StubField("Poison",             StubFieldKind.Combo, "Cure-poison spell."),
            new StubField("Disease",            StubFieldKind.Combo, "Cure-disease spell."),
            new StubField("Blindness",          StubFieldKind.Combo, "Cure-blindness spell."),
        }),
        new StubGroup("Options", new[]
        {
            new StubField("Attack last in party",          StubFieldKind.Check, "Match the party's current attack target."),
            new StubField("Attack in reverse order",       StubFieldKind.Check, "Reverse the normal target order (Back-first)."),
            new StubField("Attack what other members attack", StubFieldKind.Check, "Mirror live combat selection across the party."),
            new StubField("Request party health",          StubFieldKind.Check, "Periodic @health probe to track party state."),
            new StubField("Auto-share collected cash",     StubFieldKind.Check, "Split loot evenly via the party-share command."),
            new StubField("Help leader bash doors",        StubFieldKind.Check, "Join in on bash attempts when the leader is forcing a door."),
            new StubField("Ignore party when following",   StubFieldKind.Check, "Don't fire party-coordination commands while in `follow` mode."),
            new StubField("Auto-collect when following",   StubFieldKind.Check, "Pick up items even while following another player."),
            new StubField("Say emote when leading",        StubFieldKind.Text,  "Sent on party-formation transition to leader (e.g. `let's go`)."),
            new StubField("Say emote when joining",        StubFieldKind.Text,  "Sent on party-join (e.g. `following`)."),
            new StubField("Go @panic when injured",        StubFieldKind.Check, "Broadcast @panic when HP drops past the run threshold."),
        }),
        new StubGroup("Capacity + cadence", new[]
        {
            new StubField("Max. monsters when partying",  StubFieldKind.Numeric, "Cap on hostiles to engage as a party (overrides Combat-tab cap)."),
            new StubField("Max. monster experience",      StubFieldKind.Numeric, "Skip mobs worth more than this — avoids party-only rares."),
            new StubField("Wait if members are below",    StubFieldKind.Numeric, "Pause the party action loop while any member is below this HP %.", "%"),
            new StubField("If leading, wait only (mins)", StubFieldKind.Numeric, "Cap the wait window when this character is the leader.", "min"),
            new StubField("`par` poll frequency",         StubFieldKind.Numeric, "Cadence for the par poll — MegaMUD default is 5.", "s"),
            new StubField("Auto-Exp-Reset on loop / Auto-Lair start", StubFieldKind.Check,
                          "Sends `@Reset` so the party EXP counter zeroes at the run boundary."),
            new StubField("Auto-invite reconnecting member", StubFieldKind.Check,
                          "Leader-only — auto-invite a member who returns inside the wait window."),
            new StubField("Wait-for-party-members grace", StubFieldKind.Numeric,
                          "How long to hold the group together while a member reconnects.", "s"),
        }),
    };
}
