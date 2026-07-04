namespace FujinTerm.Models.Profile;

// Every keybindable built-in app action. One entry per command the menu or
// toolbar can invoke; KeybindingStore maps each to a KeyChord. New built-in
// actions get added here once + seeded with a default chord in
// KeybindingStore.DefaultBindings.
public enum BuiltInAction
{
    // ---- Window toggles (View menu + toolbar) ----
    OpenConversation,
    OpenParty,
    OpenWorkshop,
    OpenNavigation,
    OpenSpellBook,
    OpenLogPane,
    OpenBackscroll,
    OpenSessionStats,
    OpenSettings,
    OpenGameDataBrowser,
    OpenWireInspector,

    // ---- Connection ----
    ToggleConnection,
    ToggleCapture,
    ToggleDisableHangups,

    // ---- File menu ----
    NewProfile,
    OpenProfile,
    SaveProfile,
    SaveProfileAs,
    Quit,

    // ---- Movement engine (toolbar) ----
    MovementStart,
    MovementPause,
    MovementStop,

    // ---- Bulk one-shot actions (toolbar / Action menu) ----
    ActionGetAll,
    ActionDropAll,
    ActionEquipAll,
    ActionDepositAll,

    // ---- Auto-response toggles (toolbar / Action menu) ----
    ToggleAllAutoOff,
    ToggleAutoCombat,
    ToggleAutoNuke,
    ToggleAutoHealRest,
    ToggleAutoBless,
    ToggleAutoLight,
    ToggleAutoGetItems,
    ToggleAutoGetCash,
    ToggleAutoSneak,
    ToggleAutoHide,
    ToggleAutoSearch,
}
