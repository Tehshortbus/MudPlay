namespace MudPlay.ViewModels.Guides;

// The actions a guide line can tick on. Each is reported — through
// AppServices.NotifyTourAction, or straight to MainWindowViewModel.Guide — from the
// point where the user really does the thing, so a line ticks however the user got
// there (menu, hotkey, toolbar, right-click). A test checks every key a guide uses
// is declared here and raised somewhere in the code.
public static class GuideActions
{
    // Report an action from any window. A no-op until MainWindow wires the bridge.
    public static void Report(string key) => Services.AppServices.CurrentOrNull?.NotifyTourAction?.Invoke(key);

    // ----- First-time setup ----------------------------------------------
    public const string ProfileManagement = "ProfileManagement";
    public const string AddBbs = "AddBbs";
    public const string BbsHostPort = "BbsHostPort";
    public const string BbsSaved = "BbsSaved";
    public const string AddCharacter = "AddCharacter";
    public const string CharacterAdded = "CharacterAdded";
    public const string ImportMdb = "ImportMdb";
    public const string GameDataImported = "GameDataImported";

    // ----- Navigation -----------------------------------------------------
    public const string NavigationOpened = "NavigationOpened";
    public const string LoopHasTwoRooms = "LoopHasTwoRooms";
    public const string LoopSaved = "LoopSaved";
    public const string LoopStarted = "LoopStarted";
    public const string TwoLairsMarked = "TwoLairsMarked";
    public const string LairSetupSaved = "LairSetupSaved";
    public const string AutoLairStarted = "AutoLairStarted";
    public const string DestinationQueued = "DestinationQueued";
    public const string WalkStarted = "WalkStarted";
    public const string WaypointActionSaved = "WaypointActionSaved";
    public const string MpImportStarted = "MpImportStarted";
    public const string MpImportAccepted = "MpImportAccepted";

    // ----- Automation -----------------------------------------------------
    public const string MacrosShown = "MacrosShown";
    public const string MacroAdded = "MacroAdded";
    public const string AliasesShown = "AliasesShown";
    public const string AliasAdded = "AliasAdded";
    public const string TriggersShown = "TriggersShown";
    public const string TriggerAdded = "TriggerAdded";
    public const string EventsShown = "EventsShown";
    public const string EventAdded = "EventAdded";

    // ----- Character + game data -----------------------------------------
    public const string MonsterIntelOpened = "MonsterIntelOpened";
    public const string MonsterFiltered = "MonsterFiltered";
    public const string MonsterSelected = "MonsterSelected";
    public const string WorkshopOpened = "WorkshopOpened";
    public const string ItemFinderShown = "ItemFinderShown";
    public const string GearFinderRun = "GearFinderRun";
    public const string EquipmentManagerShown = "EquipmentManagerShown";
    public const string GearSetEdited = "GearSetEdited";
    public const string GameDataBrowserOpened = "GameDataBrowserOpened";
    public const string BrowserSectionPicked = "BrowserSectionPicked";
    public const string BrowserFiltered = "BrowserFiltered";
    public const string PlayersShown = "PlayersShown";
    public const string PlayerPermissionsSaved = "PlayerPermissionsSaved";
    public const string BuffWatchdogOpened = "BuffWatchdogOpened";
    public const string BuffAdded = "BuffAdded";

    // ----- Settings + diagnostics ----------------------------------------
    public const string SettingsOpened = "SettingsOpened";
    public const string SettingsSearched = "SettingsSearched";
    public const string SettingsSectionPicked = "SettingsSectionPicked";
    public const string SettingsApplied = "SettingsApplied";
    public const string CombatSectionShown = "CombatSectionShown";
    public const string BugReportWritten = "BugReportWritten";
}
