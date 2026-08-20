using System.Collections.Frozen;

namespace MudPlay.Services;

// Central registry of every action that can appear on the main window toolbar.
// Maps each stable ActionId string (e.g. "ToggleConnection") to its display
// label, icon resource key (looked up via StaticResource on
// Themes/Icons.axaml), tooltip, optional shortcut hint, and the
// MainWindowViewModel command name to bind against.
//
// Both the Settings → Toolbar editor and the live MainWindow.axaml dynamic
// toolbar read from this catalogue, so adding a new toolbar action is a one-line
// entry plus a glyph in the icons theme — the editor surfaces it automatically
// and the rendered toolbar resolves it through the same lookup.
public static class ToolbarItemCatalogue
{
    // One catalogue entry. ActionId is the stable identifier persisted on the
    // user's profile; CommandName is the MainWindowViewModel property the button
    // binds to (Avalonia resolves {Binding {CommandName}} via the DataContext).
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
            "ToggleConnectionCommand", ShortcutHint: "Alt+H"),
        new("OpenSettings",       "Settings",             "IconGear",
            "OpenSettingsCommand"),
        new("OpenNavigation",     "Navigation",           "IconMap",
            "OpenNavigationCommand",   ShortcutHint: "Alt+M"),
        // Movement engine controls. Only one of Start / Pause is shown at a
        // time (the rows flip IsVisible with the engine state via
        // ApplyToolbarRowState); Stop appears whenever an engine is active.
        // Start opens the Manage dialog (or runs the staged loop) when idle.
        new("MovementStart",      "Start movement",       "IconPlay",
            "MovementStartCommand", ShortcutHint: "Alt+V",
            Tooltip: "Start movement — run the staged loop, or open Manage to pick one"),
        new("MovementPause",      "Pause movement",       "IconPause",
            "MovementPauseCommand", ShortcutHint: "Alt+B",
            Tooltip: "Pause the running engine (click again to resume)"),
        new("MovementStop",       "Stop movement",        "IconStop",
            "MovementStopCommand", ShortcutHint: "Alt+N",
            Tooltip: "Stop — back fully out of the running engine"),
        new("ToggleSprintMode",   "Sprint Mode",          "IconSprint",
            "ToggleSprintModeCommand",
            Tooltip: "Sprint Mode — never pause to rest, force Auto Combat off, keep casting heals; only death stops it"),
        new("OpenBackscroll",     "Backscroll",           "IconHistory",
            "OpenBackscrollCommand",   ShortcutHint: "Alt+L"),
        new("ToggleCapture",      "Capture",              "IconRecord",
            "ToggleDumpCommand", ShortcutHint: "Alt+S",
            Tooltip: "Capture — toggle session capture"),
        new("ToggleDisableHangups","Disable hangups",     "IconNoHangup",
            "ToggleDisableHangupsCommand",
            Tooltip: "Disable hangups — block every automatic disconnect; only you can hang up"),
        new("OpenWireInspector",  "Wire Inspector",       "IconSearch",
            "OpenWireInspectorCommand", ShortcutHint: "F5",
            Tooltip: "Wire Inspector — view raw + stripped byte streams"),
        new("OpenConversation",   "Conversation",         "IconChat",
            "OpenConversationCommand", ShortcutHint: "Alt+C"),
        new("OpenParty",          "Party",                "IconParty",
            "OpenPartyCommand"),
        new("OpenBuffWatchdog",   "Buff Watchdog",        "IconBuffWatchdog",
            "OpenBuffWatchdogCommand", Tooltip: "Buff Watchdog — configured buffs, active timers, recast markers"),
        new("OpenWorkshop",       "Player Workshop",      "IconUser",
            "OpenWorkshopCommand",     ShortcutHint: "F1"),
        new("OpenSpellBook",      "Spell Book",           "IconBook",
            "OpenSpellBookCommand",    ShortcutHint: "F2"),
        new("OpenSessionStats",   "Session Stats",        "IconStats",
            "OpenSessionStatsCommand"),
        new("OpenGameDataBrowser","Game Data Browser",    "IconDatabase",
            "OpenGameDataBrowserCommand", ShortcutHint: "F3"),
        new("OpenLogPane",        "Program Log",          "IconLog",
            "OpenLogPaneCommand",      ShortcutHint: "F4"),

        // ----- Action menu surface -------------------------------------------
        // These mirror the Action menu in MainWindow.axaml. The CommandName
        // resolves by reflection to the matching [RelayCommand] on
        // MainWindowViewModel. Adding an entry here also makes the action
        // available in the Settings → Toolbar picker.

        // Bulk one-shot actions.
        new("ActionGetAll",       "Get All",              "IconGetAll",
            "GetAllCommand",
            Tooltip: "Get All — pick up every item on the room floor"),
        new("ActionDropAll",      "Drop All",             "IconDropAll",
            "DropAllCommand",
            Tooltip: "Drop All — drop every carried (unworn) item"),
        new("ActionEquipAll",     "Equip All",            "IconEquipAll",
            "EquipAllCommand",
            Tooltip: "Equip All — wear the Default gear set"),
        new("ActionDepositAll",   "Deposit All",          "IconDepositAll",
            "DepositAllCommand",
            Tooltip: "Deposit All — bank wealth to the keep-on-hand floor"),

        // Sends the in-game "exp" command (experience-to-next readout).
        new("SendExp",            "Exp",                  "IconExp",
            "SendExpCommand",
            Tooltip: "Exp — send the in-game \"exp\" command"),

        // Recovery escape hatch — drop my own conditions and the movement holds
        // / party-wait signals they drive, returning me to an idle, unafflicted
        // state (unsticks a condition that latched but never saw its wear-off).
        new("ResetStates",        "Reset States",         "IconLoop",
            "ResetStatesCommand",
            Tooltip: "Reset States — clear my own stuck ailments, waits, and movement holds (return to idle)"),

        // Master auto-responses switch. Active = auto-engines run; clicking
        // off kills every Auto-* (remembering which were on) and also gates
        // the game-entry command. Clicking back on restores the prior set.
        new("ToggleAllAutoOff",   "All auto-responses",   "IconKillSwitch",
            "AllAutoOffCommand",
            Tooltip: "Master switch — off kills every auto-engine and auto-entry; on restores them"),

        // Auto-engine toggles. Button is depressed (IsActive) while its
        // matching GeneralSettings.AutoMode flag is on; clicking flips it.
        new("ToggleAutoCombat",   "Auto Combat",          "IconAutoCombat",
            "ToggleAutoCombatCommand",
            Tooltip: "Toggle Auto Combat on / off"),
        new("ToggleAutoNuke",     "Auto Nuke",            "IconAutoNuke",
            "ToggleAutoNukeCommand",
            Tooltip: "Toggle Auto Nuke on / off"),
        new("ToggleAutoHealRest", "Auto Rest / Heal",     "IconAutoHeal",
            "ToggleAutoHealRestCommand",
            Tooltip: "Toggle Auto Rest / Heal on / off"),
        new("ToggleAutoBless",    "Auto Bless",           "IconAutoBless",
            "ToggleAutoBlessCommand",
            Tooltip: "Toggle Auto Bless on / off"),
        new("ToggleAutoLight",    "Auto Light",           "IconAutoLight",
            "ToggleAutoLightCommand",
            Tooltip: "Toggle Auto Light on / off"),
        new("ToggleAutoGetItems", "Auto Get Items",       "IconAutoGetItems",
            "ToggleAutoGetItemsCommand",
            Tooltip: "Toggle Auto Get Items on / off"),
        new("ToggleAutoGetCash",  "Auto Get Cash",        "IconAutoGetCash",
            "ToggleAutoGetCashCommand",
            Tooltip: "Toggle Auto Get Cash on / off"),
        new("ToggleAutoSneak",    "Auto Sneak",           "IconAutoSneak",
            "ToggleAutoSneakCommand",
            Tooltip: "Toggle Auto Sneak on / off"),
        new("ToggleAutoHide",     "Auto Hide",            "IconAutoHide",
            "ToggleAutoHideCommand",
            Tooltip: "Toggle Auto Hide on / off"),
        new("ToggleAutoSearch",   "Auto Search",          "IconSearch",
            "ToggleAutoSearchCommand",
            Tooltip: "Toggle Auto Search on / off (search each room on entry)"),

        // (Auto-train is toggled from the Player Workshop → CP Allocation tab and
        // Settings → Auto-Trainer — no toolbar/menu/keybind surface.)
    };

    // Every catalogue entry, in registration order — the pool the Settings →
    // Toolbar picker offers. The default toolbar layout (a curated subset in a
    // specific order, with separators) lives in ToolbarDefaults, not here.
    public static IReadOnlyList<Entry> AllEntries { get; } = _entries;

    private static readonly FrozenDictionary<string, Entry> _byId =
        _entries.ToFrozenDictionary(e => e.ActionId, StringComparer.OrdinalIgnoreCase);

    // Look up an entry by its ActionId; null if unknown.
    public static Entry? Find(string? actionId)
        => actionId is null ? null : (_byId.TryGetValue(actionId, out Entry? e) ? e : null);
}
