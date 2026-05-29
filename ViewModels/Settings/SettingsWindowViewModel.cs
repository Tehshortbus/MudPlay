using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Shell view-model for the Settings window. Owns the section catalog,
/// the search-box filter, and the OK / Apply / Cancel commit lifecycle.
/// Every settings tab persists on the loaded character profile — there
/// is no scope picker in this window. The Defaults / Global / BBS / Char
/// hierarchy is reserved for game-data record overrides (Phase 5).
/// </summary>
/// <remarks>
/// Commit model:
/// <list type="bullet">
///   <item><description><b>OK</b> = apply dirty sections, close.</description></item>
///   <item><description><b>Apply</b> = apply dirty sections, stay open.</description></item>
///   <item><description><b>Cancel / title-bar X</b> = drop pending edits, close.</description></item>
///   <item><description><b>Settings hotkey / menu re-press while open</b> = Save path
///       (calls <see cref="ApplyAndClose"/>), per CLAUDE.md's edit-window toggle policy.</description></item>
/// </list>
/// </remarks>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly ProfileService _profile;
    private readonly LogService _log;
    private bool _suppressSelectionSideEffects;

    /// <summary>Raised when the shell wants the host window to close.</summary>
    public event Action? CloseRequested;

    /// <summary>Full section catalog — drives the search filter and the sidebar order.</summary>
    public ObservableCollection<SettingsSectionViewModel> Sections { get; } = new();

    /// <summary>Filtered view the sidebar binds against. Recomputed on search-text change.</summary>
    public ObservableCollection<SettingsSectionViewModel> VisibleSections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SettingsSectionViewModel? _selectedSection;

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Footer status line — shows the active section name.</summary>
    public string StatusText => SelectedSection is null
        ? "Pick a section from the sidebar."
        : SelectedSection.Title;

    public SettingsWindowViewModel(ProfileService profile, LogService log, string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        _profile = profile;
        _log = log;

        SeedSections();
        RebuildVisibleSections();

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();
    }

    /// <summary>
    /// Save path — apply every dirty section, then ask the host window to
    /// close. Called by the OK button AND by the MainWindow toggle-hotkey
    /// re-press path (per CLAUDE.md).
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
        int wrote = 0;
        foreach (SettingsSectionViewModel s in Sections)
        {
            if (!s.IsDirty) continue;
            s.Apply();
            wrote++;
        }
        if (wrote > 0)
        {
            _log.Info("Settings",
                $"Applied {wrote} section(s) to profile '{_profile.CurrentProfileName ?? "(none)"}'.");
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleSections();

    /// <summary>
    /// Clear the search box whenever the user lands on a section so the
    /// filter doesn't persist after the click that resolved it. Setting
    /// <see cref="SearchText"/> here triggers
    /// <see cref="OnSearchTextChanged"/> which rebuilds the sidebar back
    /// to the full list. Suppressed during <see cref="RebuildVisibleSections"/>
    /// because the Clear / Add cycle briefly empties the ListBox, which
    /// nulls the bound SelectedItem and would otherwise wipe the filter
    /// the user just typed.
    /// </summary>
    partial void OnSelectedSectionChanged(SettingsSectionViewModel? value)
    {
        if (_suppressSelectionSideEffects) return;
        if (!string.IsNullOrEmpty(SearchText)) SearchText = string.Empty;
    }

    private void RebuildVisibleSections()
    {
        SettingsSectionViewModel? previouslySelected = SelectedSection;

        // Suppress the OnSelectedSectionChanged side-effect for the duration
        // of the rebuild — the ListBox nulls its bound SelectedItem the
        // moment we Clear() the ItemsSource, which would otherwise wipe
        // SearchText (and the filter the user just typed) before we get
        // a chance to restore the selection at the end.
        _suppressSelectionSideEffects = true;
        try
        {
            VisibleSections.Clear();
            string needle = SearchText.Trim();

            foreach (SettingsSectionViewModel s in Sections)
            {
                // Always include the active section even when the filter
                // would hide it — otherwise the sidebar selection vanishes
                // while the content pane keeps rendering the same tab.
                bool keepBecauseSelected = ReferenceEquals(s, previouslySelected);
                if (!string.IsNullOrEmpty(needle) && !MatchesSearch(s, needle) && !keepBecauseSelected) continue;
                VisibleSections.Add(s);
            }

            if (previouslySelected is not null && VisibleSections.Contains(previouslySelected))
            {
                SelectedSection = previouslySelected;
            }
        }
        finally
        {
            _suppressSelectionSideEffects = false;
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

    /// <summary>
    /// Populate the sidebar with placeholders for every tab. Real section
    /// VMs land in subsequent PRs — for now each placeholder advertises
    /// the phase that will wire it. Order follows the UI design spec.
    /// </summary>
    private void SeedSections()
    {
        Sections.Add(new GeneralSectionViewModel(_profile));
        Add("toolbar",   "Toolbar",   "Phase 4 PR 4.6", "Which toolbar icons are visible.");

        Sections.Add(new BbsSectionViewModel(
            AppServices.Current.Bbs,
            _profile,
            AppServices.Current.Credentials,
            AppServices.Current.Display));

        Add("health",    "Health",    "Phase 4 PR 4.8", "Passive thresholds — rest / hang / run / regen. No spell decisions (see Spells / Party).");
        Add("spells",    "Spells",    "Phase 4 PR 4.8", "Self-cast decisions — self-heal / self-cure / self-buff and which spell for each.");
        Add("combat",    "Combat",    "Phase 4 PR 4.8", "Weapon swap matrix, target order, multi-attack room spells.");
        Add("party",     "Party",     "Phase 4 PR 4.8", "Party-cast decisions, par frequency, request-heal-at, party rank.");
        Add("cash",      "Cash",      "Phase 4 PR 4.8", "Per-coin Discard / Ignore / Collect, encumbrance gates, auto-deposit.");
        Add("statline",  "Statline",  "Phase 4 PR 4.7", "Current server-side statline + wildcard preview. Token editor lands in Phase 12.");
        Add("talk",      "Talk",      "Phase 4 PR 4.8", "Per-channel filter toggles consumed by the Conversation window.");
        Add("auto-lair", "Auto-Lair", "Phase 4 PR 4.8", "Marked-lair list + scheduler heuristic + idle-penalty weight.");
        Add("other",     "Other",     "Phase 4 PR 4.8", "Auto-action toggles, scrollback size, log retention, etc.");

        Add("events",    "Events",    "Phase 4 PR 4.8", "Scheduled / lifecycle events: AtTime, Every, Logon / Logoff / Re-log.");
        Add("sounds",    "Sounds",    "Phase 4 PR 4.8", "Sound cues for triggers, events, party state changes.");

        void Add(string id, string title, string phase, string description)
            => Sections.Add(new PlaceholderSectionViewModel(id, title, phase, description));
    }
}
