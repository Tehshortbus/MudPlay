using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-character store for built-in-action keybindings — toolbar + menu
// shortcuts. Each BuiltInAction maps to one KeyChord; the dictionary is
// seeded from DefaultBindings on profile load and only entries that differ
// from the default are persisted.
//
// Sister service to MacroStore — same Char-tier storage, same conflict-check
// shape. IsConflict + FindAction let callers (the macro edit dialog, the
// built-in keybind edit dialog) detect cross-store collisions so a chord can
// never bind to both a macro and a built-in action. BindingChanged fires
// after any successful rebind so Views.GlobalHotkeys and the menu-item
// input-gesture labels can re-render.
public sealed class KeybindingStore
{
    private readonly ProfileService? _profile;
    private readonly Dictionary<BuiltInAction, KeyChord> _bindings =
        new(EqualityComparer<BuiltInAction>.Default);

    // Fired after a binding changes via Rebind. Payload: the action whose chord moved.
    public event Action<BuiltInAction>? BindingChanged;

    // Fired after a bulk reload — profile load (LoadFrom) or profile close
    // (SeedDefaults). Subscribers re-render every binding rather than
    // reacting to a single action, the way they do for BindingChanged.
    // Without this, Views.GlobalHotkeys wouldn't know to rebuild
    // Window.KeyBindings on profile load, leaving the just-loaded chords
    // inert until the user manually rebinds.
    public event Action? BindingsReloaded;

    // Parameterless ctor for tests / in-memory scenarios. Seeds defaults.
    public KeybindingStore()
    {
        SeedDefaults();
    }

    public KeybindingStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += SeedDefaults;
        profile.ProfileSaving += SnapshotForSave;
        if (profile.Current is { } current) LoadFrom(current);
        else SeedDefaults();
    }

    // Read-only snapshot keyed by action.
    public IReadOnlyDictionary<BuiltInAction, KeyChord> Bindings => _bindings;

    // Chord currently bound to action. KeyChord.Empty when the action has been unbound.
    public KeyChord Get(BuiltInAction action)
        => _bindings.TryGetValue(action, out KeyChord chord) ? chord : KeyChord.Empty;

    // Reassign action to chord. KeyChord.Empty unbinds (the action becomes
    // unreachable by keyboard until rebound). Persists + fires BindingChanged.
    public void Rebind(BuiltInAction action, KeyChord chord)
    {
        _bindings[action] = chord;
        _profile?.Save();
        BindingChanged?.Invoke(action);
    }

    // True when chord is already bound to a built-in action. excluding lets
    // the rebind UI skip the action being edited so it doesn't flag its own
    // current chord. Out-param returns the colliding action for error
    // messaging.
    public bool IsConflict(KeyChord chord, BuiltInAction? excluding, out BuiltInAction? conflictingAction)
    {
        if (chord.IsEmpty) { conflictingAction = null; return false; }
        foreach ((BuiltInAction action, KeyChord c) in _bindings)
        {
            if (excluding is BuiltInAction e && action == e) continue;
            if (c.Equals(chord)) { conflictingAction = action; return true; }
        }
        conflictingAction = null;
        return false;
    }

    // Reverse lookup: which built-in action is bound to this chord, if any.
    public BuiltInAction? FindAction(KeyChord chord)
    {
        if (chord.IsEmpty) return null;
        foreach ((BuiltInAction action, KeyChord c) in _bindings)
            if (c.Equals(chord)) return action;
        return null;
    }

    // Friendly label for the action — used in conflict messages and the
    // Settings → Toolbar + Shortcuts panel. Toolbar/Action-menu actions pull
    // their label from ToolbarItemCatalogue (single source of truth, no
    // drift); File-menu actions, which have no catalogue entry, fall back to
    // the explicit map below.
    public static string ActionLabel(BuiltInAction action)
        => ToolbarItemCatalogue.Find(action.ToString())?.Label
           ?? action switch
           {
               BuiltInAction.NewProfile    => "New profile",
               BuiltInAction.OpenProfile   => "Open profile",
               BuiltInAction.SaveProfile   => "Save profile",
               BuiltInAction.SaveProfileAs => "Save profile as",
               BuiltInAction.Quit          => "Quit",
               _                           => action.ToString(),
           };

    // Hardcoded seed chords — mirror the chords that used to live as literals
    // in Views.GlobalHotkeys and MainWindow.axaml. Any new BuiltInAction
    // entry without a default here is unbound by default.
    public static readonly IReadOnlyDictionary<BuiltInAction, KeyChord> DefaultBindings =
        new Dictionary<BuiltInAction, KeyChord>
        {
            [BuiltInAction.OpenConversation]    = new(Key.F2),
            [BuiltInAction.OpenParty]           = new(Key.F3),
            [BuiltInAction.OpenWorkshop]        = new(Key.F4),
            [BuiltInAction.OpenNavigation]      = new(Key.F5),
            [BuiltInAction.OpenSpellBook]       = new(Key.F7),
            [BuiltInAction.OpenLogPane]         = new(Key.F9),
            [BuiltInAction.OpenBackscroll]      = new(Key.F10),
            [BuiltInAction.OpenSessionStats]    = new(Key.F11),
            [BuiltInAction.OpenSettings]        = new(Key.OemComma, Ctrl: true),
            [BuiltInAction.OpenGameDataBrowser] = new(Key.G,        Ctrl: true),
            [BuiltInAction.OpenWireInspector]   = KeyChord.Empty, // toolbar-only
            [BuiltInAction.ToggleConnection]    = new(Key.K,        Ctrl: true),
            [BuiltInAction.ToggleCapture]       = new(Key.F6),
            [BuiltInAction.NewProfile]          = new(Key.N,        Ctrl: true),
            [BuiltInAction.OpenProfile]         = new(Key.O,        Ctrl: true),
            [BuiltInAction.SaveProfile]         = new(Key.S,        Ctrl: true),
            [BuiltInAction.SaveProfileAs]       = new(Key.S,        Ctrl: true, Shift: true),
            [BuiltInAction.Quit]                = new(Key.Q,        Ctrl: true),
        };

    // Reset every binding back to its DefaultBindings chord (actions with no
    // default become unbound). Persists once and fires BindingsReloaded once
    // — cheaper than rebinding each action individually. Drives the "Reset
    // all shortcuts" button on the Toolbar + Shortcuts settings tab.
    public void ResetAllToDefaults()
    {
        SeedDefaults();
        _profile?.Save();
    }

    // ----- Profile sync --------------------------------------------------

    private void SeedDefaults()
    {
        _bindings.Clear();
        foreach ((BuiltInAction action, KeyChord chord) in DefaultBindings)
            _bindings[action] = chord;
        BindingsReloaded?.Invoke();
    }

    private void LoadFrom(CharacterProfile profile)
    {
        _bindings.Clear();
        foreach ((BuiltInAction action, KeyChord chord) in DefaultBindings)
            _bindings[action] = chord;
        if (profile.BuiltInKeybindings is { Count: > 0 } overrides)
        {
            foreach ((BuiltInAction action, KeyChord chord) in overrides)
                _bindings[action] = chord;
        }
        BindingsReloaded?.Invoke();
    }

    // Snapshot the dict onto the profile right before save. Only entries that
    // deviate from the seed default get written — a pristine profile leaves
    // CharacterProfile.BuiltInKeybindings null.
    private void SnapshotForSave(CharacterProfile profile)
    {
        Dictionary<BuiltInAction, KeyChord> deltas = new();
        foreach ((BuiltInAction action, KeyChord chord) in _bindings)
        {
            if (!DefaultBindings.TryGetValue(action, out KeyChord def) || !def.Equals(chord))
                deltas[action] = chord;
        }
        profile.BuiltInKeybindings = deltas.Count == 0 ? null : deltas;
    }
}
