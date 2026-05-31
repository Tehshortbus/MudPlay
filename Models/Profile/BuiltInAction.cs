namespace FujinTerm.Models.Profile;

/// <summary>
/// Every keybindable built-in app action. One entry per command the
/// menu or toolbar can invoke; <see cref="Services.KeybindingStore"/>
/// maps each to a <see cref="KeyChord"/>. New built-in actions get
/// added here once + seeded with a default chord in
/// <see cref="Services.KeybindingStore.DefaultBindings"/>.
/// </summary>
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

    // ---- File menu ----
    NewProfile,
    OpenProfile,
    SaveProfile,
    SaveProfileAs,
    Quit,
}
