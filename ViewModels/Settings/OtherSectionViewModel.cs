using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Other" tab — the misc bucket. Graduated from Phase-4 stub to wired
/// section as soon as its first feature (suicide-lives threshold) was
/// ready to plumb to the live engine. Future PRs add fields to
/// <see cref="OtherSettings"/> and wire them through
/// <see cref="ApplyToServices"/> the same way; the inline stub catalog
/// (<see cref="StubGroups"/>) shrinks as each lands.
/// </summary>
public sealed partial class OtherSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Other";

    private readonly ProfileService _profile;
    private readonly SettingsService _globalSettings;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "other";
    public override string Title => "Other";
    public override bool IsDirty => _dirty;

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    public string PhaseTag => "Phase 6 (suicide threshold) + Phases 7 / 11 / 13 (per-row tooltips)";

    public string Description =>
        "Catch-all for safety thresholds, walker behaviour flags, log retention, and ignore-this-ailment knobs " +
        "that don't fit the other tabs. The suicide-threshold row at the top is wired into the Phase 6 remote " +
        "engine; everything below is still skeleton — each toggle's tooltip names the owning phase.";

    public override Control View => _view ??= new OtherSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels
    {
        get
        {
            yield return Title;
            yield return "Suicide threshold";
            yield return "Block @do suicide";
            yield return "Lives";
            yield return "Attempt bash";
            yield return "Pick locks instead of bashing";
            yield return "Attempt pick-lock";
            yield return "Lockpicks";
            yield return "Max comeback backtrack rooms";
            yield return "@comeback";
            yield return "Utilize self or party members to disarm traps";
            yield return "Disarm traps";
            yield return "Traps";
            yield return "@trap max searches";
            yield return "@trap max disarms";
            foreach (StubGroup g in StubGroups)
            foreach (StubField f in g.Fields)
                yield return f.Label;
        }
    }

    // ----- Wired (PR 6.x) -----

    /// <summary>
    /// Block <c>@do suicide</c> / <c>@party suicide</c> when remaining
    /// lives are ≤ this value. Range 0..20. Default 3 per the Phase 6
    /// spec; pushed into the live engine on Apply + on profile load.
    /// </summary>
    [ObservableProperty] private int _maxSuicideLivesThreshold = 5;

    // Note: the ailment Ignore* (@wait gates) and DoNotAnnounce* (say-
    // suppression gates) toggles graduated to the Spells tab — they sit
    // next to the cure-spell picks they coordinate with. AilmentSyncEngine
    // reads them from SpellsSettings now.

    // ----- @trap auto-disarm attempt caps (wired) -----
    // Both push into TrapDisarmManager on Apply via ApplyToServices,
    // and via AppServices.ApplyOtherFromActiveProfile on ProfileLoaded /
    // ProfileMutated. Search row sits above disarm in the rendered
    // panel per user spec.

    // Master gate for walker trap-disarming. Read live by the walker's
    // trap-disarm gate (AppServices.SetTrapDisarmGate) which also AND-s
    // in the local Traps-skill capability check.
    [ObservableProperty] private bool _utilizeDisarmTrapsIfAble = true;

    [ObservableProperty] private int _maxTrapSearchAttempts = 20;
    [ObservableProperty] private int _maxTrapDisarmAttempts = 5;

    // ----- Door open/bash/pick caps (wired) -----
    // Graduated from Phase-4 stubs by commit 2. Read live by
    // DoorOpenManager on each enqueue via providers in AppServices
    // (no push needed — the manager reads through the resolver on
    // every request).

    /// <summary>
    /// Walker max <c>bash &lt;dir&gt;</c> retries before falling back
    /// to pick / failing. Default 10 per user direction.
    /// </summary>
    [ObservableProperty] private int _maxBashAttempts = 10;

    /// <summary>
    /// Walker max <c>pick &lt;dir&gt;</c> retries before falling back
    /// to bash / failing. Default 10 per user direction.
    /// </summary>
    [ObservableProperty] private int _maxPickAttempts = 10;

    /// <summary>
    /// When checked, the walker prefers <c>pick &lt;dir&gt;</c> over
    /// <c>bash &lt;dir&gt;</c> on doors where both verbs are viable.
    /// Thieves typically flip this on.
    /// </summary>
    [ObservableProperty] private bool _picklocksOverBash;

    /// <summary>
    /// Off by default. When on, every observed Confirmed→Pending→Confirmed
    /// transition logs one Info line with the measured wall-clock time +
    /// the current encumbrance level. Use it for a data-collection session
    /// when tuning the Auto-Lair travel-cost table; turn it off again for
    /// normal play.
    /// </summary>
    [ObservableProperty] private bool _logMovementHopTiming;

    /// <summary>
    /// Leader-side <c>@comeback</c> backtrack budget — how many rooms the
    /// leader walks backwards along the path just taken when a stranded
    /// follower sends a bare <c>@comeback</c> (no target room) before
    /// giving up and going idle. Range 1..50, default 10. Ignored when
    /// the follower supplies an explicit room. Pushed into the live
    /// <see cref="Game.Remote.PartyComebackManager"/> on Apply +
    /// profile load.
    /// </summary>
    [ObservableProperty] private int _maxComebackBacktrackRooms = 10;

    /// <summary>
    /// Follower-side auto-<c>@comeback</c>. When on (default), being left
    /// behind by the party leader (a movement-failure line just before
    /// "You are no longer following X.") telepaths <c>@comeback</c> to the
    /// leader automatically. When off, the strand is detected but no
    /// request is sent. Pushed into the live
    /// <see cref="Game.Remote.ComebackRequester"/> on Apply + profile load.
    /// </summary>
    [ObservableProperty] private bool _autoRequestComebackWhenLeftBehind = true;

    // Phase 9 per-category Verbose toggles + WriteCombatRoundTrace
    // moved out of per-character settings into a session-only umbrella
    // switch in the Log pane menu — see Services/LogDiagnosticState
    // + LogPaneViewModel.CombatFilter. Verbose tracing isn't a
    // per-character preference; it's "I'm debugging right now",
    // and a session toggle in the LogPane is the right home.

    // Note: the party-bless gates (Bless while resting / Bless during
    // combat) graduated to the Party tab — they sit next to the bless
    // slots they gate. CastingDirector reads them from PartySettings now.

    // Note: the run-away (flee) knobs (Go-backwards-if-running /
    // Break-combat-before-running) graduated to the Combat tab — they sit
    // next to the room thresholds + RunDistance they coordinate with.
    // HealthManager reads RunDirection / BreakBeforeFleeing from CombatSettings.

    /// <summary>
    /// Inactive-player auto-cleanup window in days. Moved here from the
    /// General tab per user direction. Lives at the Global tier (one
    /// threshold for the whole install) so Apply writes through to
    /// <see cref="SettingsService"/>, not the per-character profile.
    /// 0 disables auto-cleanup entirely; per-player Don't-auto-delete
    /// opts records out individually.
    /// </summary>
    [ObservableProperty] private int _playerCleanupDays = 90;

    // ----- Inline stub catalog (un-wired Phase 7 / 11 / 13 fields) -----

    /// <summary>
    /// The remaining un-wired Other-tab fields, rendered inline below
    /// the wired group as disabled placeholders. Each entry's tooltip
    /// names the owning phase; entries are removed from this list as
    /// their consumer engines wire through <see cref="OtherSettings"/>.
    /// </summary>
    public IReadOnlyList<StubGroup> StubGroups { get; } = new[]
    {
        new StubGroup("Walker behaviour", new[]
        {
            new StubField("Auto-train",                      StubFieldKind.Check, "Phase 13 — auto-spend CP at a trainer when allocations are pending."),
            new StubField("Auto-train stats",                StubFieldKind.Check, "Phase 13 — auto-spend stat points at a trainer when allocations are pending. Paired with Auto-train above."),
            new StubField("Teleport to avoid combat instead of hanging", StubFieldKind.Check,
                          "Phase 7 — when fleeing, use sys-goto (stock) or a town token (paradigm) instead of dropping the line."),
            // "Allow hangup in all-off mode" graduated to a wired checkbox on
            // the General tab (HealthManager runs only the emergency-hangup
            // branch when every Auto-* engine is off and
            // GeneralSettings.AllowHangupInAllOffMode set).
            // Removed per user direction: "Hangup if naked".
            new StubField("Search rooms if item needed",     StubFieldKind.Check, "Phase 7 — walker auto-searches when item-collect requires it."),
            // Removed per user direction: "Backwards if warning" (nonsense),
            // "Provide light in dimly lit rooms" (handled elsewhere),
            // "Don't move unless sneaking" (our movement engine always sneaks
            // before moving — the toggle was a MegaMUD workaround for a
            // message-parsing combat bug we don't share).
            // "Go backwards if running" / "Break combat before running"
            // graduated to wired checkboxes on the Combat tab (HealthManager
            // reads CombatSettings.RunDirection / BreakBeforeFleeing).
            // Lock / trap preference toggles moved down next to their
            // matching retry-count pickers (see "Locks & traps" group).
        }),
        // Ignored ailments group graduated to a real wired section above
        // (rendered inline in OtherSectionView.axaml). Diseased added per
        // user direction so the four ailment families are symmetric.
        // "Auto-engage on connect" group graduated to a real wired section
        // — the three mismatched stub fields became a 1-to-1 set of
        // "re-enable on reconnect" checkboxes (one per AutoMode auto-action)
        // rendered inline in OtherSectionView.axaml.
        // "Locks & traps" group removed: Attempt-bash / Pick-locks-over-bash /
        // Attempt-pick-lock graduated to wired fields by commit 2
        // (DoorOpenManager); "Attempt to disarm traps" graduated to the wired
        // "Utilize disarm traps if able" checkbox; "Attempt disarm" (retry
        // cap) is the already-wired "@trap max disarms" picker above — no
        // duplicate needed.
        // Removed per user direction:
        // - "Command splitter character" (^M and ; are hardwired)
        // - "Backscroll buffer size" (lives on BBS + Display)
        // - "Inactive player cleanup window" (graduated to wired field above)
        // - "Debug log retention" (per-instance, doesn't persist)
        // - "Game entry/exit command" (graduated to wired group above)
    };

    public OtherSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.Settings) { }

    public OtherSectionViewModel(ProfileService profile, SettingsService globalSettings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(globalSettings);
        _profile = profile;
        _globalSettings = globalSettings;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        OtherSettings dto = new()
        {
            MaxSuicideLivesThreshold = Math.Clamp(MaxSuicideLivesThreshold, 0, 9),
            UtilizeDisarmTrapsIfAble = UtilizeDisarmTrapsIfAble,
            MaxTrapSearchAttempts = Math.Clamp(MaxTrapSearchAttempts, 1, 100),
            MaxTrapDisarmAttempts = Math.Clamp(MaxTrapDisarmAttempts, 1, 50),
            MaxBashAttempts       = Math.Clamp(MaxBashAttempts,       1, 100),
            MaxPickAttempts       = Math.Clamp(MaxPickAttempts,       1, 100),
            PicklocksOverBash     = PicklocksOverBash,
            LogMovementHopTiming  = LogMovementHopTiming,
            MaxComebackBacktrackRooms = Math.Clamp(MaxComebackBacktrackRooms, 1, 50),
            AutoRequestComebackWhenLeftBehind = AutoRequestComebackWhenLeftBehind,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // PlayerCleanupDays lives at Global tier (one threshold per
        // install). Persist alongside the char-tier write so the
        // user's single Apply commits both.
        int sanitized = Math.Clamp(PlayerCleanupDays, 0, 3650);
        if (_globalSettings.Current.PlayerCleanupDays != sanitized)
        {
            _globalSettings.Current.PlayerCleanupDays = sanitized;
            _globalSettings.Save();
        }

        ApplyToServices(dto);
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        OtherSettings dto = ReadOrDefault();
        MaxSuicideLivesThreshold = dto.MaxSuicideLivesThreshold;
        UtilizeDisarmTrapsIfAble = dto.UtilizeDisarmTrapsIfAble;
        MaxTrapSearchAttempts = dto.MaxTrapSearchAttempts;
        MaxTrapDisarmAttempts = dto.MaxTrapDisarmAttempts;
        MaxBashAttempts       = dto.MaxBashAttempts;
        MaxPickAttempts       = dto.MaxPickAttempts;
        PicklocksOverBash     = dto.PicklocksOverBash;
        LogMovementHopTiming  = dto.LogMovementHopTiming;
        MaxComebackBacktrackRooms = dto.MaxComebackBacktrackRooms;
        AutoRequestComebackWhenLeftBehind = dto.AutoRequestComebackWhenLeftBehind;
        PlayerCleanupDays = _globalSettings?.Current.PlayerCleanupDays ?? 90;
        ApplyToServices(dto);
    }

    private OtherSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new OtherSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new OtherSettings();
        try
        {
            return JsonSerializer.Deserialize<OtherSettings>(json) ?? new OtherSettings();
        }
        catch
        {
            return new OtherSettings();
        }
    }

    private static void ApplyToServices(OtherSettings dto)
    {
        AppServices svcs = AppServices.Current;
        // MajorMUD caps lives at 9 (0 lives deletes the character), so the
        // threshold clamps 0..9 — matching the Apply() DTO clamp + the UI.
        svcs.RemoteCommands.MaxSuicideLivesThreshold = Math.Clamp(dto.MaxSuicideLivesThreshold, 0, 9);
        // @trap attempt caps — push into the live manager so the next
        // queued @trap honours the edit without a profile reload.
        svcs.TrapDisarm.MaxSearchAttempts = Math.Clamp(dto.MaxTrapSearchAttempts, 1, 100);
        svcs.TrapDisarm.MaxDisarmAttempts = Math.Clamp(dto.MaxTrapDisarmAttempts, 1, 50);
        // Calibrator toggle — live-mirror so the user can flip it from
        // the Settings dialog without an Apply + profile reload cycle.
        svcs.HopCalibrator.Enabled = dto.LogMovementHopTiming;
        // @comeback backtrack budget — live-mirror so the next stranded-
        // follower pickup honours the edit without a profile reload.
        svcs.PartyComeback.MaxBacktrackRooms = Math.Clamp(dto.MaxComebackBacktrackRooms, 1, 50);
        // Follower-side auto-@comeback toggle — live-mirror so the next
        // left-behind honours the edit without a profile reload.
        svcs.ComebackRequest.Enabled = dto.AutoRequestComebackWhenLeftBehind;
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnMaxSuicideLivesThresholdChanged(int value) => MarkDirty();
    partial void OnPlayerCleanupDaysChanged(int value)   => MarkDirty();
    partial void OnUtilizeDisarmTrapsIfAbleChanged(bool value) => MarkDirty();
    partial void OnMaxTrapSearchAttemptsChanged(int value) => MarkDirty();
    partial void OnMaxTrapDisarmAttemptsChanged(int value) => MarkDirty();
    partial void OnMaxBashAttemptsChanged(int value)       => MarkDirty();
    partial void OnMaxPickAttemptsChanged(int value)       => MarkDirty();
    partial void OnPicklocksOverBashChanged(bool value)    => MarkDirty();
    partial void OnLogMovementHopTimingChanged(bool value) => MarkDirty();
    partial void OnMaxComebackBacktrackRoomsChanged(int value) => MarkDirty();
    partial void OnAutoRequestComebackWhenLeftBehindChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
