using System.Collections.Frozen;

namespace FujinTerm.Services;

/// <summary>
/// Central registry of every action that can appear on the main window
/// toolbar. Maps each stable <c>ActionId</c> string (e.g.
/// <c>"ToggleConnection"</c>) to its display label, icon resource key
/// (looked up via <c>StaticResource</c> on <c>Themes/Icons.axaml</c>),
/// tooltip, optional shortcut hint, and the
/// <see cref="ViewModels.MainWindowViewModel"/> command name to bind
/// against.
/// </summary>
/// <remarks>
/// Both the Settings → Toolbar editor and the live
/// <c>MainWindow.axaml</c> dynamic toolbar read from this catalogue, so
/// adding a new toolbar action is a one-line entry plus a glyph in the
/// icons theme — the editor surfaces it automatically and the
/// rendered toolbar resolves it through the same lookup.
/// </remarks>
public static class ToolbarItemCatalogue
{
    /// <summary>
    /// One catalogue entry. <see cref="ActionId"/> is the stable
    /// identifier persisted on the user's profile;
    /// <see cref="CommandName"/> is the MainWindowViewModel property
    /// the button binds to (Avalonia resolves
    /// <c>{Binding {CommandName}}</c> via the DataContext).
    /// </summary>
    public sealed record Entry(
        string ActionId,
        string Label,
        string IconResourceKey,
        string CommandName,
        string? Tooltip = null,
        string? ShortcutHint = null);

    private static readonly Entry[] _entries =
    {
        new("ToggleConnection",   "Connect / Disconnect", "IconPlug",
            "ToggleConnectionCommand", ShortcutHint: "Ctrl+K"),
        new("OpenSettings",       "Settings",             "IconGear",
            "OpenSettingsCommand",     ShortcutHint: "Ctrl+,"),
        new("OpenNavigation",     "Navigation",           "IconMap",
            "OpenNavigationCommand",   ShortcutHint: "F5"),
        new("OpenBackscroll",     "Backscroll",           "IconHistory",
            "OpenBackscrollCommand",   ShortcutHint: "F10"),
        new("ToggleCapture",      "Capture",              "IconRecord",
            "ToggleDumpCommand",       Tooltip: "Capture — toggle session capture"),
        new("OpenWireInspector",  "Wire Inspector",       "IconSearch",
            "OpenWireInspectorCommand",
            Tooltip: "Wire Inspector — view raw + stripped byte streams"),
        new("OpenConversation",   "Conversation",         "IconChat",
            "OpenConversationCommand", ShortcutHint: "F2"),
        new("OpenParty",          "Party",                "IconParty",
            "OpenPartyCommand",        ShortcutHint: "F3"),
        new("OpenWorkshop",       "Player Workshop",      "IconUser",
            "OpenWorkshopCommand",     ShortcutHint: "F4"),
        new("OpenSpellBook",      "Spell Book",           "IconBook",
            "OpenSpellBookCommand",    ShortcutHint: "F7"),
        new("OpenSessionStats",   "Session Stats",        "IconStats",
            "OpenSessionStatsCommand", ShortcutHint: "F11"),
        new("OpenGameDataBrowser","Game Data Browser",    "IconDatabase",
            "OpenGameDataBrowserCommand", ShortcutHint: "Ctrl+G"),
        new("OpenLogPane",        "Log",                  "IconLog",
            "OpenLogPaneCommand",      ShortcutHint: "F9"),
    };

    /// <summary>All entries in their canonical (default-layout) order.</summary>
    public static IReadOnlyList<Entry> AllEntries { get; } = _entries;

    private static readonly FrozenDictionary<string, Entry> _byId =
        _entries.ToFrozenDictionary(e => e.ActionId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up an entry by its <see cref="Entry.ActionId"/>; <c>null</c> if unknown.</summary>
    public static Entry? Find(string? actionId)
        => actionId is null ? null : (_byId.TryGetValue(actionId, out Entry? e) ? e : null);
}
