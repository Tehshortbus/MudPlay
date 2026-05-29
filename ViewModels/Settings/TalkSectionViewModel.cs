using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Talk" tab stub — per-channel default visibility for the Conversation
/// window. The window has its own runtime filter toggles (Phase 2 PR 2.5);
/// this tab seeds them on profile load.
/// </summary>
public sealed class TalkSectionViewModel : StubSectionViewModel
{
    public override string Id => "talk";
    public override string Title => "Talk";
    public override string PhaseTag => "Phase 2 PR 2.5 (Conversation window seed)";
    public override string Description =>
        "Default visibility of each chat / system channel — applied to the Conversation window on profile load. " +
        "The window keeps its own runtime toggles, so per-session overrides don't write back here unless the user " +
        "explicitly saves them.";

    public override IReadOnlyList<StubGroup> Groups { get; } = new[]
    {
        new StubGroup("Channels shown by default", new[]
        {
            new StubField("Gossip",                StubFieldKind.Check, "Realm-wide chat."),
            new StubField("Telepath",              StubFieldKind.Check, "Tells / whispers between individual players."),
            new StubField("Gangpath",              StubFieldKind.Check, "Gang-only chat (when the character is in a gang)."),
            new StubField("Say",                   StubFieldKind.Check, "Local-room talk."),
            new StubField("Broadcast",             StubFieldKind.Check, "Sysop / global broadcasts."),
            new StubField("System messages",       StubFieldKind.Check, "Server-emitted notices."),
            new StubField("Realm entrance / exit", StubFieldKind.Check, "\"X has entered the realm.\" lines."),
        }),
        new StubGroup("Outgoing", new[]
        {
            new StubField("Echo my own gossip / tell into the window", StubFieldKind.Check,
                          "Mirrors outgoing chat into the Conversation surface."),
        }),
    };
}
