using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Spells;

namespace FujinTerm.ViewModels.Settings;

// Base for one section in the SettingsWindowViewModel's sidebar. Every settings
// tab — including app-wide ones like Display and Toolbar — lives on the loaded
// character profile, so sections don't take a runtime scope; each one knows
// where its data lives.
//
// Game-data record overrides use the four-tier (Defaults / Global / BBS / Char)
// hierarchy via SettingsResolver, over in the Game Data Browser. Settings-tab
// data has no tier picker.
public abstract partial class SettingsSectionViewModel : ObservableObject, IDisposable
{
    // Cleanup callbacks (typically event `-=`) registered by sections that hook
    // publishers outliving the Settings window — ProfileService, GameDataCache,
    // PlayerState, the spellbook, keybindings. Run on Dispose when the window
    // closes; without them the singletons keep every section VM (and the whole
    // window graph) alive for the app's lifetime, leaking a fresh copy per reopen.
    private readonly List<Action> _teardown = new();
    private bool _disposed;

    // Stable identifier — sidebar selection persists across reopens against this.
    public abstract string Id { get; }

    // Title shown in the sidebar.
    public abstract string Title { get; }

    // True when this section has unapplied edits.
    public virtual bool IsDirty => false;

    // Substring tokens fed to the shell's search box. Default: title only. Tabs
    // that wrap real fields override to include label text so the search box
    // jumps from "rest" → Health tab.
    public virtual IEnumerable<string> SearchableLabels => new[] { Title };

    // The editor UserControl rendered in the shell's content pane. Lazy —
    // constructed on first access so an unselected section pays no UI cost.
    public abstract Control View { get; }

    // Persist this section's pending edits. Default no-op for placeholders.
    public virtual void Apply() { }

    // Drop pending edits and re-read from the underlying store.
    public virtual void Discard() { }

    // Shared typeahead filter for the spell-picker boxes: matches the typed
    // text against either the 4-letter cast-code or the full spell name, so a
    // slot can be found by code or by name even though the box commits the code.
    // Bound as ItemFilter on each AutoCompleteBox.
    public AutoCompleteFilterPredicate<object?> SpellSuggestionFilter { get; } =
        static (search, item) =>
            item is SpellPick pick &&
            (string.IsNullOrEmpty(search)
             || pick.Short.Contains(search, StringComparison.OrdinalIgnoreCase)
             || pick.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    // Register a cleanup callback (typically an event -=) to run when the
    // Settings window closes. Sections that subscribe to a singleton outliving
    // the window MUST unhook here, or the publisher pins the whole VM graph
    // forever.
    protected void OnDispose(Action teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        _teardown.Add(teardown);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Action t in _teardown) t();
        _teardown.Clear();
    }
}
