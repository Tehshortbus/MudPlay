using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Settings;

// Shell view-model for the Settings window. Owns the section catalog, the
// search box, and the OK / Apply / Cancel commit lifecycle. Each section
// persists to its own store (character profile, BBS record or Global tier);
// there is no scope picker in this window. The Defaults / Global / BBS / Char
// hierarchy picker is reserved for game-data record overrides.
//
// Commit model:
//   OK     = apply dirty sections, close.
//   Apply  = apply dirty sections, stay open.
//   Cancel / title-bar X = drop pending edits, close.
//   Settings hotkey / menu re-press while open = raise it when it's buried; when
//     it's already in front, the same as OK (ApplyAndClose) — CLAUDE.md's
//     window re-press policy.
public sealed partial class SettingsWindowViewModel : ObservableObject, IDisposable
{
    private readonly ProfileService _profile;
    private readonly LogService _log;
    private readonly Func<string, Task<bool>>? _sendText;

    // Reads the text a section's settings show (labels, tooltips) off its View; set
    // by the host window, since walking controls is view work. Null until then,
    // when the search falls back to SearchableLabels alone.
    private Func<SettingsSectionViewModel, IEnumerable<string>>? _shownText;

    // Shared staged combat-profile list edited by the Combat, Health, Spells and Party
    // section VMs (a combat profile spans those tabs). Lives for this window's lifetime.
    private CombatProfileStagingSession? _profileSession;

    // Raised when the shell wants the host window to close.
    public event Action? CloseRequested;

    // Full section catalog, in sidebar order. The search box highlights sections
    // (SettingsSectionViewModel.IsMatch) and never hides one — the Help window's
    // search, applied to the sidebar.
    public ObservableCollection<SettingsSectionViewModel> Sections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SettingsSectionViewModel? _selectedSection;

    // Live search — highlights the matching sections, and the matching settings in
    // the open one. It stays put while the user moves between sections.
    [ObservableProperty] private string _searchText = string.Empty;

    // Footer status line — shows the active section name.
    public string StatusText => SelectedSection is null
        ? "Pick a section from the sidebar."
        : SelectedSection.Title;

    // Under the sidebar, worded as the Help window's topic count.
    public string SearchStatusText => SearchMatch.IsBlank(SearchText)
        ? $"{Sections.Count:N0} sections"
        : MatchSummary(Sections.Count(s => s.IsMatch));

    internal static string MatchSummary(int n) =>
        n == 0 ? "No sections match" : $"{n:N0} matching section{(n == 1 ? "" : "s")}";

    public SettingsWindowViewModel(
        ProfileService profile,
        LogService log,
        Func<string, Task<bool>>? sendText = null,
        string? initialSectionId = null,
        string? initialBbsName = null,
        string? initialRealmName = null)
        : this(profile, log, sendText)
    {
        SeedSections();

        SelectedSection = initialSectionId is not null
            ? Sections.FirstOrDefault(s => string.Equals(s.Id, initialSectionId, StringComparison.OrdinalIgnoreCase))
              ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault();

        if (initialBbsName is not null) SelectBbs(initialBbsName, initialRealmName);
    }

    // Testable ctor — takes the sections instead of building the real catalog.
    internal SettingsWindowViewModel(ProfileService profile, LogService log, IEnumerable<SettingsSectionViewModel> sections)
        : this(profile, log, sendText: null)
    {
        foreach (SettingsSectionViewModel s in sections) Sections.Add(s);
        SelectedSection = Sections.FirstOrDefault();
    }

    private SettingsWindowViewModel(ProfileService profile, LogService log, Func<string, Task<bool>>? sendText)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        _profile = profile;
        _log = log;
        _sendText = sendText;
    }

    // The host window hands over how to read a section's on-screen text; a search
    // already typed is re-run against it.
    public void UseShownText(Func<SettingsSectionViewModel, IEnumerable<string>> shownText)
    {
        _shownText = shownText;
        ApplySearch();
    }

    // Deep-link target for "edit THIS BBS" (and optionally one of its realms) —
    // Profile Management's settings buttons route here so the window opens on the
    // BBS tab with that record already selected, instead of whichever BBS the tab would have auto-picked. Pending
    // per-BBS field edits survive the switch (BbsSectionViewModel pushes them to
    // its cache on every keystroke), so re-pointing an already-open window is safe.
    public void SelectBbs(string bbsName, string? realmName = null)
    {
        if (Sections.OfType<BbsSectionViewModel>().FirstOrDefault() is not { } bbs) return;
        SelectedSection = bbs;
        if (bbs.AvailableBbsNames.Contains(bbsName)) bbs.SelectedBbsName = bbsName;
        if (realmName is not null && bbs.RealmNames.Contains(realmName)) bbs.SelectedRealmName = realmName;
    }

    // Tears down every section so the singletons they hooked (ProfileService,
    // GameDataCache, PlayerState, spellbook, keybindings) release their
    // references. Called by the host window's Closed handler; without it each
    // reopen of Settings leaks its entire VM graph for the app's life.
    public void Dispose()
    {
        foreach (SettingsSectionViewModel section in Sections)
            section.Dispose();
        _profileSession?.Dispose();
    }

    // True once the user has chosen a commit path (OK / Apply-and-close or
    // Cancel). Lets the host window's Closing handler distinguish "X / Alt-F4
    // with no explicit decision" from "we already discarded / saved" and route
    // the no-decision close through DiscardChanges.
    public bool IsCommitted { get; private set; }

    // Save path — apply every dirty section, then ask the host window to close.
    // Called by the OK button AND by a menu / hotkey re-press while the window is in
    // front (MainWindowViewModel.RaiseOrClose). When Confirm save settings is on, "No" returns to the
    // editor with no save and no close.
    public async void ApplyAndClose()
    {
        // Only prompt to save when something actually changed — clicking OK on an
        // untouched Settings window shouldn't ask "Save your changes?" (report
        // paradigm-20260922-113233). A clean window just closes.
        if (AnyDirty)
        {
            if (!await ConfirmSaveWarningsAsync()) return;
            if (!await Services.AppServices.Current.Confirm.ConfirmSaveAsync()) return;
            ApplyAll();
        }
        IsCommitted = true;
        CloseRequested?.Invoke();
    }

    // True when any section has pending edits. Gates the save-confirm prompt.
    private bool AnyDirty => Sections.Any(s => s.IsDirty);

    // Discard path — drop pending edits without writing, then close. Called by
    // the Cancel button.
    public void DiscardAndClose()
    {
        DiscardChanges();
        IsCommitted = true;
        CloseRequested?.Invoke();
    }

    // Drop pending edits but don't close the window, so the title-bar X / Alt-F4
    // can roll back like Cancel without re-entering Close.
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
        // Apply button — same confirm-save semantics as OK, minus the close. Nothing
        // dirty ⇒ nothing to save, so don't prompt.
        if (!AnyDirty) return;
        if (!await ConfirmSaveWarningsAsync()) return;
        if (!await Services.AppServices.Current.Confirm.ConfirmSaveAsync()) return;
        ApplyAll();
    }

    // A dirty section that flags a problem (SaveWarning) is asked about before
    // anything is written. Going back lands on that section, nothing saved.
    private async Task<bool> ConfirmSaveWarningsAsync()
    {
        List<SettingsSectionViewModel> flagged = Sections.Where(s => s.IsDirty && s.SaveWarning is not null).ToList();
        if (flagged.Count == 0) return true;
        string body = string.Join("\n\n", flagged.Select(s => $"{s.Title}\n{s.SaveWarning}")) + "\n\nSave anyway?";
        if (await Services.AppServices.Current.Confirm.ConfirmAsync("Before saving", body, "Save anyway")) return true;
        SelectedSection = flagged[0];
        return false;
    }

    private void ApplyAll()
    {
        (List<string> applied, _) = ApplyDirty(Sections, holdFlagged: false);
        LogApplied(applied);
        GuideActions.Report(GuideActions.SettingsApplied);
    }

    private void LogApplied(List<string> applied)
    {
        if (applied.Count == 0) return;
        _log.Info("Settings",
            $"User saved {applied.Count} section(s) to profile '{_profile.CurrentProfileName ?? "(none)"}': {string.Join(", ", applied)}.");
    }

    // Apply every dirty section, returning the titles applied. With holdFlagged a
    // dirty section that has a SaveWarning is left as it is and returned as held:
    // its "Save anyway?" needs an answer, and there is nobody to give one.
    internal static (List<string> Applied, List<string> Held) ApplyDirty(
        IEnumerable<SettingsSectionViewModel> sections, bool holdFlagged)
    {
        List<string> applied = new(), held = new();
        foreach (SettingsSectionViewModel s in sections)
        {
            if (!s.IsDirty) continue;
            if (holdFlagged && s.SaveWarning is not null)
            {
                held.Add(s.Title);
                continue;
            }
            s.Apply();
            applied.Add(s.Title);
        }
        return (applied, held);
    }

    // The client is going down (an exit, or the updater's restart) with this window
    // still open. The user never pressed Cancel, so what they changed is saved as OK
    // would save it. Nothing can be asked on the way out: the save-confirm prompt is
    // skipped, and a section whose warning needs answering stays unsaved and is
    // named in the log.
    public void ApplyPendingForExit()
    {
        if (IsCommitted || !AnyDirty) return;
        _log.Info("Settings", "Closing with unsaved Settings changes — saving them.");
        (List<string> applied, List<string> held) = ApplyDirty(Sections, holdFlagged: true);
        LogApplied(applied);
        if (held.Count > 0)
            _log.Warn("Settings",
                $"Not saved on the way out, because it needed a \"Save anyway?\" answer: {string.Join(", ", held)}.");
        IsCommitted = true;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch();
        if (!string.IsNullOrWhiteSpace(value)) GuideActions.Report(GuideActions.SettingsSearched);
    }

    // A section picked by the user, not the one the window opened on, ticks the
    // Settings guide's "pick a section" line; the Events / Combat pages tick theirs
    // however they were reached.
    partial void OnSelectedSectionChanged(SettingsSectionViewModel? oldValue, SettingsSectionViewModel? newValue)
    {
        if (oldValue is not null && newValue is not null) GuideActions.Report(GuideActions.SettingsSectionPicked);
        if (newValue?.Id == "events") GuideActions.Report(GuideActions.EventsShown);
        else if (newValue?.Id == "combat") GuideActions.Report(GuideActions.CombatSectionShown);
    }

    private void ApplySearch()
    {
        foreach (SettingsSectionViewModel s in Sections)
            s.ApplySearch(SearchText, _shownText);
        OnPropertyChanged(nameof(SearchStatusText));
    }

    // Populate the sidebar with a section VM per tab, in UI-design-spec order.
    private void SeedSections()
    {
        Sections.Add(new GeneralSectionViewModel(_profile));
        Sections.Add(new ToolbarSectionViewModel(
            _profile,
            AppServices.Current.Keybindings,
            AppServices.Current.Macros,
            AppServices.Current.Dialogs,
            AppServices.Current.Settings,
            AppServices.Current.Bbs));

        var bbsSection = new BbsSectionViewModel(
            AppServices.Current.Bbs,
            _profile,
            AppServices.Current.Passwords,
            AppServices.Current.Display,
            AppServices.Current.Settings);
        Sections.Add(bbsSection);

        // One shared combat-profile staging session, edited by the Combat, Health,
        // Spells and Party tabs (a profile spans them all). Created before those
        // sections so its ProfileLoaded handler runs first (it drives the box reload
        // on a mid-window character swap). Constructed FIRST here so it subscribes
        // before the VMs.
        _profileSession = new CombatProfileStagingSession(
            AppServices.Current.CombatProfiles,
            _profile,
            () => _profile.Current?.Equipment);

        var healthSection = new HealthSectionViewModel(_profileSession);
        Sections.Add(healthSection);
        // The Health tab's "Sys goto wimpy" gate + picker track the BBS tab's LIVE
        // (unsaved) sysop-goto state, so ticking Sysop goto there enables the wimpy
        // control immediately — no Save round-trip. Both sections live for the same
        // window lifetime, so these subscriptions don't outlive them.
        healthSection.BindLiveSysGoto(
            () => bbsSection.SysopGoto,
            () => bbsSection.SysopGotos
                .Select(r => r.Name ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList());
        bbsSection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BbsSectionViewModel.SysopGoto))
                healthSection.NotifyLiveSysGotoChanged();
        };
        bbsSection.SysopGotos.CollectionChanged += (_, _) => healthSection.NotifyLiveSysGotoChanged();

        Sections.Add(new SpellsSectionViewModel(_profileSession));
        Sections.Add(new CombatSectionViewModel(_profileSession));
        Sections.Add(new PartySectionViewModel(_profileSession));
        Sections.Add(new PvpSectionViewModel());
        Sections.Add(new CashSectionViewModel());
        Sections.Add(new StatlineSectionViewModel(_profile, AppServices.Current.PlayerState, _sendText));
        Sections.Add(new TalkSectionViewModel());
        Sections.Add(new AutoLightSectionViewModel());
        Sections.Add(new AutoLairSectionViewModel());
        Sections.Add(new AutoTrainerSectionViewModel());
        Sections.Add(new TeleportsSectionViewModel());
        Sections.Add(new PeriodicDamageRoomSpellsSectionViewModel());
        Sections.Add(new OtherSectionViewModel());
        Sections.Add(new EventsSectionViewModel());
        Sections.Add(new SoundsSectionViewModel());
    }
}
