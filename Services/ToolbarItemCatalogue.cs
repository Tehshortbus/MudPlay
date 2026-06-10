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
        string? ShortcutHint = null,
        bool InDefaultLayout = true);

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
        new("OpenLogPane",        "Program Log",          "IconLog",
            "OpenLogPaneCommand",      ShortcutHint: "F9"),

        // ----- Action menu surface (PR 4.6b) ---------------------------------
        // These mirror the Action menu in MainWindow.axaml. Commands aren't
        // wired yet — the owning phase column below names the PR that will
        // hook them up. Adding an entry here makes the action available in
        // the Settings → Toolbar picker; on the live toolbar the button
        // renders disabled (null Command) with the tooltip below until its
        // command property exists on MainWindowViewModel.

        // Bulk one-shot actions.
        new("ActionGetAll",       "Get All",              "IconGetAll",
            "GetAllCommand",
            Tooltip: "Get All — wired in Phase 12 PR 12.4 (Quick Tools)",
            InDefaultLayout: false),
        new("ActionDropAll",      "Drop All",             "IconDropAll",
            "DropAllCommand",
            Tooltip: "Drop All — wired in Phase 12 PR 12.4 (Quick Tools)",
            InDefaultLayout: false),
        new("ActionEquipAll",     "Equip All",            "IconEquipAll",
            "EquipAllCommand",
            Tooltip: "Equip All — wired in Phase 9 PR 9.11 (Workshop EQUIP)",
            InDefaultLayout: false),
        new("ActionDepositAll",   "Deposit All",          "IconDepositAll",
            "DepositAllCommand",
            Tooltip: "Deposit All — wired in Phase 13 PR 13.E (CashManager)",
            InDefaultLayout: false),

        // Master kill-switch — flips every Auto-* off in one click.
        new("ToggleAllAutoOff",   "All Auto-Responses Off","IconKillSwitch",
            "AllAutoOffCommand",
            Tooltip: "Master kill-switch — wired in Phase 13 (clears every auto-toggle below)",
            InDefaultLayout: false),

        // Auto-engine toggles. Active visual is wired through ToolbarButtonItem's
        // IsActive flag in their owning PR.
        new("ToggleAutoCombat",   "Auto Combat",          "IconAutoCombat",
            "ToggleAutoCombatCommand",
            Tooltip: "Auto Combat — wired in Phase 13 PR 13.A (CombatManager)",
            InDefaultLayout: false),
        new("ToggleAutoNuke",     "Auto Nuke",            "IconAutoNuke",
            "ToggleAutoNukeCommand",
            Tooltip: "Auto Nuke — wired in Phase 13 PR 13.A (multi-attack room spells)",
            InDefaultLayout: false),
        new("ToggleAutoHealRest", "Auto Rest / Heal",     "IconAutoHeal",
            "ToggleAutoHealRestCommand",
            Tooltip: "Auto Rest / Heal — wired in Phase 13 PRs 13.B + 13.D",
            InDefaultLayout: false),
        new("ToggleAutoBless",    "Auto Bless",           "IconAutoBless",
            "ToggleAutoBlessCommand",
            Tooltip: "Auto Bless — wired in Phase 13 PR 13.D (CastingDirector)",
            InDefaultLayout: false),
        new("ToggleAutoLight",    "Auto Light",           "IconAutoLight",
            "ToggleAutoLightCommand",
            Tooltip: "Auto Light — wired in Phase 9 PR 9.K (AutoLightManager)",
            InDefaultLayout: false),
        new("ToggleAutoGetItems", "Auto Get Items",       "IconAutoGetItems",
            "ToggleAutoGetItemsCommand",
            Tooltip: "Auto Get Items — wired in Phase 13 (Phase 5 Items auto-collect flags)",
            InDefaultLayout: false),
        new("ToggleAutoGetCash",  "Auto Get Cash",        "IconAutoGetCash",
            "ToggleAutoGetCashCommand",
            Tooltip: "Auto Get Cash — wired in Phase 13 PR 13.E (CashManager)",
            InDefaultLayout: false),
        new("ToggleAutoSneak",    "Auto Sneak",           "IconAutoSneak",
            "ToggleAutoSneakCommand",
            Tooltip: "Auto Sneak — wired in Phase 13 PR 13.F (StealthManager)",
            InDefaultLayout: false),
        new("ToggleAutoHide",     "Auto Hide",            "IconAutoHide",
            "ToggleAutoHideCommand",
            Tooltip: "Auto Hide — wired in Phase 13 PR 13.F (StealthManager)",
            InDefaultLayout: false),
        new("ToggleAutoSearch",   "Auto Search",          "IconSearch",
            "ToggleAutoSearchCommand",
            Tooltip: "Auto Search — wired in Phase 7 (AutoWalkManager — search rooms while running)",
            InDefaultLayout: false),
    };

    /// <summary>All entries in their canonical (default-layout) order.</summary>
    public static IReadOnlyList<Entry> AllEntries { get; } = _entries;

    private static readonly FrozenDictionary<string, Entry> _byId =
        _entries.ToFrozenDictionary(e => e.ActionId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up an entry by its <see cref="Entry.ActionId"/>; <c>null</c> if unknown.</summary>
    public static Entry? Find(string? actionId)
        => actionId is null ? null : (_byId.TryGetValue(actionId, out Entry? e) ? e : null);
}
