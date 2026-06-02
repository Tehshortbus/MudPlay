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
                          "Phase 6 — hard kill-switch above every per-channel row below and above the per-player permissions on the Players tab."),
            new StubField("Disallow @party commands (from leader)", StubFieldKind.Check,
                          "Phase 6 — overrides the base @party whitelist; useful for solo runs inside a party."),
            // Per-channel disable rows below cover only the three channels the
            // engine accepts @-commands from. Gossip / Auction / Broadcast / Yell
            // are hard-excluded by RemoteCommandManager.MapChannel — they're
            // realm-wide noise; no per-user toggle would make sense for them.
            new StubField("Disallow @-commands from telepaths / pages", StubFieldKind.Check,
                          "Phase 6 — direct tells from individual players."),
            new StubField("Disallow @-commands from gangpaths", StubFieldKind.Check,
                          "Phase 6 — gang-channel @-commands."),
            new StubField("Disallow @-commands from say (local)", StubFieldKind.Check,
                          "Phase 6 — local-room talk."),
            new StubField("Remote-control failure message", StubFieldKind.Text,
                          "Phase 6 — reply text sent back to the originator when an @-command is denied or unrecognised. Default `{command invalid or not allowed}`. Subscribed by RemoteCommandManager's denial path."),
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
            new StubField("Disable all AFK settings", StubFieldKind.Check,
                          "Phase 11 — master kill-switch above every AFK behaviour below. When on, the engine never auto-flips AFK regardless of the other rows."),
            new StubField("Auto-AFK in",                StubFieldKind.Numeric, "Phase 11 — auto-flip into AFK after this many minutes of input idle. Set to 0 to disable the timer specifically.", "minutes"),
            new StubField("Auto-AFK when minimized",    StubFieldKind.Check,   "Phase 11 — flip AFK as soon as the window minimises."),
            new StubField("User input cancels AFK mode", StubFieldKind.Check,  "Phase 11 — any key in the terminal restores active state."),
            new StubField("AFK response message",          StubFieldKind.Text, "Phase 11 — reply text for incoming tells while AFK (default `{AFK}`)."),
        }),
    };
}
