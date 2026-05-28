using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Shell view-model for the Settings window. Owns the section catalog,
/// the scope selector (Char / BBS / Global / Defaults), the search-box
/// filter, and the OK / Apply / Cancel commit lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Commit model:
/// </para>
/// <list type="bullet">
///   <item><description><b>OK</b> = apply dirty sections at the current scope, then close.</description></item>
///   <item><description><b>Apply</b> = apply dirty sections at the current scope, stay open.</description></item>
///   <item><description><b>Cancel / title-bar X</b> = drop pending edits without writing, close.</description></item>
///   <item><description><b>Settings hotkey / menu re-press while open</b> = Save path
///       (calls <see cref="ApplyAndClose"/>), per CLAUDE.md's edit-window toggle policy.</description></item>
/// </list>
/// <para>
/// Pending edits live inside the section view-models; the shell only sees
/// <see cref="SettingsSectionViewModel.IsDirty"/> and dispatches to
/// <see cref="SettingsSectionViewModel.Apply"/> /
/// <see cref="SettingsSectionViewModel.Discard"/>. The
/// <see cref="SettingsResolver"/> handles tier-aware persistence.
/// </para>
/// </remarks>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly SettingsResolver _resolver;
    private readonly ProfileService _profile;
    private readonly LogService _log;

    /// <summary>Raised when the shell wants the host window to close.</summary>
    public event Action? CloseRequested;

    /// <summary>Full section catalog — drives the search filter and the sidebar order.</summary>
    public ObservableCollection<SettingsSectionViewModel> Sections { get; } = new();

    /// <summary>Filtered view the sidebar binds against. Recomputed on search-text change.</summary>
    public ObservableCollection<SettingsSectionViewModel> VisibleSections { get; } = new();

    /// <summary>Scope picker options. Defaults entry is always present (read-only).</summary>
    public ObservableCollection<ScopeOption> ScopeOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScope))]
    [NotifyPropertyChangedFor(nameof(IsScopeReadOnly))]
    private ScopeOption _scope = null!;

    /// <summary>Convenience alias exposing just the <see cref="SettingsTier"/> of the selected scope.</summary>
    public SettingsTier SelectedScope => Scope.Tier;

    /// <summary>True when the selected scope is Defaults — UI disables Apply / OK.</summary>
    public bool IsScopeReadOnly => Scope.Tier == SettingsTier.Defaults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SettingsSectionViewModel? _selectedSection;

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Footer status line — section name on the left, dirty hint on the right.</summary>
    public string StatusText => SelectedSection is null
        ? "Pick a section from the sidebar."
        : SelectedSection.Title;

    public SettingsWindowViewModel(SettingsResolver resolver, ProfileService profile, LogService log)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        _resolver = resolver;
        _profile = profile;
        _log = log;

        SeedScopeOptions();
        SeedSections();
        RebuildVisibleSections();

        SelectedSection = Sections.FirstOrDefault();
    }

    /// <summary>
    /// Save path — apply every dirty section at the current scope, then ask
    /// the host window to close. Called by the OK button AND by the
    /// MainWindow toggle-hotkey re-press path (per CLAUDE.md).
    /// </summary>
    public void ApplyAndClose()
    {
        ApplyAll();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Discard path — drop pending edits without writing, then close.
    /// Called by the Cancel button. (The title-bar X also drops without
    /// writing since pending edits live in unflushed VM state.)
    /// </summary>
    public void DiscardAndClose()
    {
        foreach (SettingsSectionViewModel s in Sections) s.Discard();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Ok() => ApplyAndClose();

    [RelayCommand]
    private void Cancel() => DiscardAndClose();

    [RelayCommand]
    private void Apply() => ApplyAll();

    private void ApplyAll()
    {
        if (IsScopeReadOnly) return;
        int wrote = 0;
        foreach (SettingsSectionViewModel s in Sections)
        {
            if (!s.IsDirty) continue;
            s.Apply(SelectedScope, _resolver);
            wrote++;
        }
        if (wrote > 0) _log.Info("Settings", $"Applied {wrote} section(s) at {Scope.Label}.");
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleSections();

    private void RebuildVisibleSections()
    {
        VisibleSections.Clear();
        string needle = SearchText.Trim();

        foreach (SettingsSectionViewModel s in Sections)
        {
            if (!string.IsNullOrEmpty(needle) && !MatchesSearch(s, needle)) continue;
            VisibleSections.Add(s);
        }
    }

    private static bool MatchesSearch(SettingsSectionViewModel section, string needle)
    {
        foreach (string label in section.SearchableLabels)
        {
            if (label.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void SeedScopeOptions()
    {
        ScopeOptions.Add(new ScopeOption(SettingsTier.Character,
            _profile.CurrentProfileName is { } c ? $"Char: {c}" : "Char: (no profile)"));
        ScopeOptions.Add(new ScopeOption(SettingsTier.Bbs,
            _profile.Current?.BbsName is { } b ? $"BBS: {b}" : "BBS: (no BBS)"));
        ScopeOptions.Add(new ScopeOption(SettingsTier.Global, "Global"));
        ScopeOptions.Add(new ScopeOption(SettingsTier.Defaults, "Defaults (read-only)"));

        // Default scope = Char if a profile is loaded, else Global. Defaults
        // tier is never the initial pick — read-only confuses first-launch.
        Scope = _profile.Current is not null
            ? ScopeOptions[0]
            : ScopeOptions[2];
    }

    /// <summary>
    /// Populate the sidebar with placeholders for every tab. Real section VMs
    /// land in subsequent PRs — for now each placeholder advertises the phase
    /// that will wire it. Order follows the UI design spec.
    /// </summary>
    private void SeedSections()
    {
        Add("general",   "General",   "Phase 4 PR 4.2", "Data folder, auto-connect, manual / auto-mode defaults.");
        Add("display",   "Display",   "Phase 4 PR 4.3", "Rows / columns, palette, scrollback size, confirmation prompts.");
        Add("toolbar",   "Toolbar",   "Phase 4 PR 4.6", "Which toolbar icons are visible.");
        Add("comms",     "Comms",     "Phase 4 PR 4.4", "NAWS, terminal type, line-end handling.");

        Add("bbs",       "BBS",       "Phase 4 PR 4.5", "Host / port / account, reconnect rules, login automation sequence.");

        Add("health",    "Health",    "Phase 4 PR 4.8", "Passive thresholds — rest / hang / run / regen. No spell decisions (see Spells / Party).");
        Add("spells",    "Spells",    "Phase 4 PR 4.8", "Self-cast decisions — self-heal / self-cure / self-buff and which spell for each.");
        Add("combat",    "Combat",    "Phase 4 PR 4.8", "Weapon swap matrix, target order, multi-attack room spells.");
        Add("party",     "Party",     "Phase 4 PR 4.8", "Party-cast decisions, par frequency, request-heal-at, party rank.");
        Add("cash",      "Cash",      "Phase 4 PR 4.8", "Per-coin Discard / Ignore / Collect, encumbrance gates, auto-deposit.");
        Add("statline",  "Statline",  "Phase 4 PR 4.7", "Current server-side statline + wildcard preview. Token editor lands in Phase 12.");
        Add("talk",      "Talk",      "Phase 4 PR 4.8", "Per-channel filter toggles consumed by the Conversation window.");
        Add("auto-lair", "Auto-Lair", "Phase 4 PR 4.8", "Marked-lair list + scheduler heuristic + idle-penalty weight.");
        Add("pvp",       "PvP",       "Phase 4 PR 4.8", "Flee / hangup / attack / chase rules and reconnect timer.");
        Add("other",     "Other",     "Phase 4 PR 4.8", "Auto-action toggles, scrollback size, log retention, etc.");

        Add("events",    "Events",    "Phase 4 PR 4.8", "Scheduled / lifecycle events: AtTime, Every, Logon / Logoff / Re-log.");
        Add("sounds",    "Sounds",    "Phase 4 PR 4.8", "Sound cues for triggers, events, party state changes.");

        void Add(string id, string title, string phase, string description)
            => Sections.Add(new PlaceholderSectionViewModel(id, title, phase, description));
    }
}

/// <summary>One entry in the scope-selector dropdown.</summary>
public sealed record ScopeOption(SettingsTier Tier, string Label)
{
    public override string ToString() => Label;
}
