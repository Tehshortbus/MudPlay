using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Talk" tab stub — chat moderation + remote-command master switches +
/// AFK Mode. Per-channel visibility used to live here but moved to the
/// Conversation window's own filter strip; this tab is about *control*
/// (what's allowed, how AFK behaves) not display.
/// </summary>
public sealed class TalkSectionViewModel : StubSectionViewModel
{
    public override string Id => "talk";
    public override string Title => "Talk";
    public override string PhaseTag => "Phase 6 (RemoteCommandManager master switches) + Phase 11 (AFK Mode)";
    public override string Description =>
        "Master switches for remote-control acceptance, AFK Mode behaviour, and per-channel remote-divert routing. " +
        "Per-channel visibility filters live on the Conversation window itself; this tab governs the engine-level " +
        "policy that the Conversation window honours.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Chat behaviour", new[]
        {
            new StubField("Greet players when first met",     StubFieldKind.Check, "Phase 13 — emit a configured greet on first encounter per character."),
            new StubField("Warn user if remote command is invalid", StubFieldKind.Check, "Phase 6 — surface a banner when a denied / malformed @-command is rejected."),
        }),
        new StubGroup("Remote-control master switches", new[]
        {
            new StubField("Disallow all remote control commands", StubFieldKind.Check,
                          "Phase 6 — hard kill-switch above the per-player permissions on the Players tab."),
            new StubField("Disallow @party commands (from leader)", StubFieldKind.Check,
                          "Phase 6 — overrides the base @party whitelist; useful for solo runs inside a party."),
            new StubField("Disallow remote control from gangpaths", StubFieldKind.Check,
                          "Phase 6 — gang-channel @-commands are denied wholesale."),
        }),
        new StubGroup("Remote diverting", new[]
        {
            new StubField("Local talk",         StubFieldKind.Check, "Phase 13 — forward incoming Say lines to the configured destination."),
            new StubField("Telepaths / pages",  StubFieldKind.Check, "Phase 13 — forward incoming tells."),
            new StubField("Gangpaths",          StubFieldKind.Check, "Phase 13 — forward gang chat."),
            new StubField("Gossips / auctions", StubFieldKind.Check, "Phase 13 — forward realm-wide chat."),
            new StubField("Broadcasts",         StubFieldKind.Check, "Phase 13 — forward sysop / global broadcasts."),
            new StubField("Divert destination", StubFieldKind.Text,  "Phase 13 — where forwarded chat goes (e.g. a tell to another character, a file, or a Trigger handler)."),
        }),
        new StubGroup("AFK Mode", new[]
        {
            new StubField("Auto-AFK in N minutes",      StubFieldKind.Numeric, "Phase 11 — auto-flip into AFK after this much input idle.", "min"),
            new StubField("Auto-AFK when minimized",    StubFieldKind.Check,   "Phase 11 — flip AFK as soon as the window minimises."),
            new StubField("User input cancels AFK mode", StubFieldKind.Check,  "Phase 11 — any key in the terminal restores active state."),
            new StubField("AFK response message",          StubFieldKind.Text, "Phase 11 — reply text for incoming tells while AFK (default `{AFK}`)."),
            new StubField("Remote-control failure message", StubFieldKind.Text, "Phase 11 — reply text for denied @-commands (default `{command invalid or not allowed}`)."),
        }),
    };
}
