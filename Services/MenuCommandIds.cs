namespace FujinTerm.Services;

// Stable identifiers for every menu-bar / context-menu command. The main
// menu and the terminal right-click context menu both reference these IDs;
// keeping them in one place prevents drift between the two surfaces and gives
// a single rename target.
//
// Naming convention: group.action in lower-kebab. Example: file.connect,
// view.log, tools.replay-capture.
public static class MenuCommandIds
{
    // ----- File ----------------------------------------------------------
    public const string FileConnect           = "file.connect";
    public const string FileDisconnect        = "file.disconnect";
    public const string FileBbsList           = "file.bbs-list";
    public const string FileNewProfile        = "file.new-profile";
    public const string FileOpenProfile       = "file.open-profile";
    public const string FileSaveProfile       = "file.save-profile";
    public const string FileRecentProfiles    = "file.recent-profiles";
    public const string FileQuit              = "file.quit";

    // ----- View ----------------------------------------------------------
    public const string ViewConversation      = "view.conversation";
    public const string ViewParty             = "view.party";
    public const string ViewPlayerStatus      = "view.player-status";
    public const string ViewMap               = "view.map";
    public const string ViewWorkshop          = "view.workshop";
    public const string ViewSpellBook         = "view.spell-book";
    public const string ViewLog               = "view.log";
    public const string ViewBackscroll        = "view.backscroll";
    public const string ViewSessionStats      = "view.session-stats";
    public const string ViewResetLayout       = "view.reset-layout";

    // ----- Settings ------------------------------------------------------
    public const string SettingsOpen          = "settings.open";
    public const string SettingsOpenEvents    = "settings.open-events";

    // ----- Game Data -----------------------------------------------------
    public const string GameDataActiveSet     = "game-data.active-set";
    public const string GameDataBrowser       = "game-data.browser";
    public const string GameDataMacros        = "game-data.macros";
    public const string GameDataImportMdb     = "game-data.import-mdb";
    public const string GameDataImportSpellMsg = "game-data.import-spell-messages";

    // ----- Tools ---------------------------------------------------------
    public const string ToolsReplayCapture    = "tools.replay-capture";
    public const string ToolsWireInspector    = "tools.wire-inspector";
    public const string ToolsOpenLogsFolder   = "tools.open-logs-folder";

    // ----- Help ----------------------------------------------------------
    public const string HelpTopics            = "help.topics";
    public const string HelpKeyboardShortcuts = "help.keyboard-shortcuts";
    public const string HelpMajorMudWiki      = "help.majormud-wiki";
    public const string HelpMajorMudReddit    = "help.majormud-reddit";
    public const string HelpBbsSite           = "help.bbs-site";
    public const string HelpReportIssue       = "help.report-issue";
    public const string HelpLicense           = "help.license";
    public const string HelpAbout             = "help.about";

    // ----- Edit (terminal context menu only) -----------------------------
    public const string EditCopy              = "edit.copy";
    public const string EditPaste             = "edit.paste";
    public const string EditSelectAll         = "edit.select-all";
    public const string EditFindInScrollback  = "edit.find-in-scrollback";
    public const string EditClearScreen       = "edit.clear-screen";
}
