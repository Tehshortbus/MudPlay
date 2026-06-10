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
    private readonly Func<string, Task<bool>>? _sendText;
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

    public SettingsWindowViewModel(
        ProfileService profile,
        LogService log,
        Func<string, Task<bool>>? sendText = null,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        _profile = profile;
        _log = log;
        _sendText = sendText;

        SeedSections();
        RebuildVisibleSections();

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();
    }

    /// <summary>
    /// True once the user has chosen a commit path (OK / Apply-and-close
    /// or Cancel). Lets the host window's Closing handler distinguish
    /// "X / Alt-F4 with no explicit decision" from "we already discarded
    /// / saved" and route the no-decision close through <see cref="DiscardChanges"/>.
    /// </summary>
    public bool IsCommitted { get; private set; }

    /// <summary>
    /// Save path — apply every dirty section, then ask the host window to
    /// close. Called by the OK button AND by the MainWindow toggle-hotkey
    /// re-press path (per CLAUDE.md). When Confirm save settings is on,
    /// "No" returns to the editor with no save and no close.
    /// </summary>
    public async void ApplyAndClose()
    {
        if (!await Services.AppServices.Current.Confirm.ConfirmSaveAsync()) return;
        ApplyAll();
        IsCommitted = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Discard path — drop pending edits without writing, then close.
    /// Called by the Cancel button.
    /// </summary>
    public void DiscardAndClose()
    {
        DiscardChanges();
        IsCommitted = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Drop pending edits but don't close the window. Used by the host
    /// window's Closing handler to route X / Alt-F4 through the same
    /// rollback logic as Cancel without re-entering Close.
    /// </summary>
    public void DiscardChanges()
    {
        foreach (SettingsSectionViewModel s in Sections) s.Discard();
        IsCommitted = true;
    }

    [RelayCommand]
    private void Ok() => ApplyAndClose();

    [RelayCommand]
    private void Cancel() => DiscardAndClose();

    [RelayCommand]
    private async Task ApplyAsync()
    {
        // Apply button — same confirm-save semantics as OK, minus the close.
        if (!await Services.AppServices.Current.Confirm.ConfirmSaveAsync()) return;
        ApplyAll();
    }

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
        Sections.Add(new ToolbarSectionViewModel(
            _profile,
            AppServices.Current.Keybindings,
            AppServices.Current.Macros,
            AppServices.Current.Dialogs));

        Sections.Add(new BbsSectionViewModel(
            AppServices.Current.Bbs,
            _profile,
            AppServices.Current.Passwords,
            AppServices.Current.Display,
            AppServices.Current.Settings));

        // Phase 4 PR 4.8 stub tabs — disabled controls with per-field tooltips
        // showing the owning PR. Real persistence + wiring lands per the
        // tooltip on each row.
        Sections.Add(new HealthSectionViewModel());
        Sections.Add(new SpellsSectionViewModel());
        Sections.Add(new CombatSectionViewModel());
        Sections.Add(new PartySectionViewModel());
        Sections.Add(new CashSectionViewModel());
        Sections.Add(new ItemsSectionViewModel());
        Sections.Add(new StatlineSectionViewModel(_profile, AppServices.Current.PlayerState, _sendText));
        Sections.Add(new TalkSectionViewModel());
        Sections.Add(new AutoLairSectionViewModel());
        Sections.Add(new OtherSectionViewModel());
        Sections.Add(new EventsSectionViewModel());
        Sections.Add(new SoundsSectionViewModel());
    }
}
