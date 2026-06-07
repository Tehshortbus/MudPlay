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
            yield return "Ignore poison";
            yield return "Ignore blindness";
            yield return "Ignore confusion";
            yield return "Ignore diseased";
            yield return "Ailments";
            yield return "Game entry command";
            yield return "Game exit command";
            yield return "Enter realm";
            yield return "Logoff";
            yield return "@hangup";
            yield return "Attempt bash";
            yield return "Pick locks instead of bashing";
            yield return "Attempt pick-lock";
            yield return "Lockpicks";
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

    // ----- Ignored ailments (wired Phase 6+) -----
    // Default UNCHECKED — most parties want to pause on every ailment.
    // Toggle ON when the party agrees to push through a specific
    // ailment (e.g. don't pause for a poison tick during a boss).
    // Drives the future WaitTriggerEngine's per-ailment @wait decision
    // once message-matching lands.

    [ObservableProperty] private bool _ignorePoison;
    [ObservableProperty] private bool _ignoreBlindness;
    [ObservableProperty] private bool _ignoreConfusion;
    [ObservableProperty] private bool _ignoreDiseased;

    // ----- Game-menu commands (wired) -----
    // Entry: main-menu key to enter the realm (default "E").
    // Exit:  main-menu logoff command (default "=x").
    // HangupHandler consumes ExitCommand immediately on a permitted
    // @hangup. The cleanup-warning + first-session-load automation
    // for both commands ships in a follow-up PR once the small
    // scheduler + main-menu pattern exist.

    [ObservableProperty] private string _gameEntryCommand = "E";
    [ObservableProperty] private string _gameExitCommand  = "=x";

    // ----- @trap auto-disarm attempt caps (wired) -----
    // Both push into TrapDisarmManager on Apply via ApplyToServices,
    // and via AppServices.ApplyOtherFromActiveProfile on ProfileLoaded /
    // ProfileMutated. Search row sits above disarm in the rendered
    // panel per user spec.

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

    // ----- Phase 9 verbose diagnostic toggles -----
    // Per docs/10-phase-9-automation-engines.md § Cross-cut 3. Each toggle
    // gates whether its category's Debug-severity log lines reach the
    // LogPane (Info+ severity is always on). Off by default — verbose
    // channels are loud and only useful when troubleshooting a specific
    // subsystem. Round-trace toggle is independent: it writes one row
    // per combat round to Data/Logs/combat-{sessionStart}.log
    // regardless of LogPane state.

    [ObservableProperty] private bool _verboseCombat;
    [ObservableProperty] private bool _verboseRoomClassifier;
    [ObservableProperty] private bool _verboseCasting;
    [ObservableProperty] private bool _verboseCash;
    [ObservableProperty] private bool _verboseStealth;
    [ObservableProperty] private bool _writeCombatRoundTrace;

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
            new StubField("Allow hangup in all-off mode",    StubFieldKind.Check, "Phase 13 — gate hangup when every Auto-* toggle is off."),
            new StubField("Hangup if naked",                 StubFieldKind.Check, "Phase 13 — recovery safety, disconnect if equipment got lost."),
            new StubField("Search rooms if item needed",     StubFieldKind.Check, "Phase 7 — walker auto-searches when item-collect requires it."),
            new StubField("Go backwards if running",         StubFieldKind.Check, "Phase 13 — flee direction prefers retracing rather than pushing forward."),
            new StubField("Break combat before running",     StubFieldKind.Check, "Phase 13 — stop swinging before issuing the flee command."),
            new StubField("Don't move unless sneaking",      StubFieldKind.Check, "Phase 7 — walker pause-gate when stealth drops."),
            // Removed per user direction: "Backwards if warning" (nonsense),
            // "Provide light in dimly lit rooms" (handled elsewhere).
            // Lock / trap preference toggles moved down next to their
            // matching retry-count pickers (see "Locks & traps" group).
        }),
        // Ignored ailments group graduated to a real wired section above
        // (rendered inline in OtherSectionView.axaml). Diseased added per
        // user direction so the four ailment families are symmetric.
        new StubGroup("Auto-engage on connect", new[]
        {
            new StubField("Enable auto-combat on reconnect", StubFieldKind.Check, "Phase 13 PR 13.A — flips CombatManager on at logon."),
            new StubField("Enable auto-rest on reconnect",   StubFieldKind.Check, "Phase 13 PR 13.B — flips HealthManager rest on at logon."),
            new StubField("Enable auto-heal on reconnect",   StubFieldKind.Check, "Phase 13 PR 13.D — flips CastingDirector self-heal on at logon."),
            new StubField("Bless while resting",  StubFieldKind.Check, "Phase 13 PR 13.D — CastingDirector recasts party-buffs during downtime."),
            new StubField("Bless during combat",  StubFieldKind.Check, "Phase 13 PR 13.D — extends bless casting into active rounds."),
        }),
        new StubGroup("Locks & traps", new[]
        {
            // Attempt-bash / Pick-locks-over-bash / Attempt-pick-lock
            // graduated to wired fields by commit 2 (DoorOpenManager).
            // They render in the wired section above; this group keeps
            // the remaining trap-disarm toggles only.
            new StubField("Attempt to disarm traps",       StubFieldKind.Check,   "Phase 7 PR 7.22 — walker pauses at trapped exits and tries disarm."),
            new StubField("Attempt disarm",                StubFieldKind.Numeric, "Phase 7 PR 7.22 — retry cap on trap disarm before falling back.","times"),
        }),
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
            IgnorePoison    = IgnorePoison,
            IgnoreBlindness = IgnoreBlindness,
            IgnoreConfusion = IgnoreConfusion,
            IgnoreDiseased  = IgnoreDiseased,
            GameEntryCommand = (GameEntryCommand ?? string.Empty).Trim(),
            GameExitCommand  = (GameExitCommand  ?? string.Empty).Trim(),
            MaxTrapSearchAttempts = Math.Clamp(MaxTrapSearchAttempts, 1, 100),
            MaxTrapDisarmAttempts = Math.Clamp(MaxTrapDisarmAttempts, 1, 50),
            MaxBashAttempts       = Math.Clamp(MaxBashAttempts,       1, 100),
            MaxPickAttempts       = Math.Clamp(MaxPickAttempts,       1, 100),
            PicklocksOverBash     = PicklocksOverBash,
            LogMovementHopTiming  = LogMovementHopTiming,

            VerboseCombat          = VerboseCombat,
            VerboseRoomClassifier  = VerboseRoomClassifier,
            VerboseCasting         = VerboseCasting,
            VerboseCash            = VerboseCash,
            VerboseStealth         = VerboseStealth,
            WriteCombatRoundTrace  = WriteCombatRoundTrace,
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
        IgnorePoison    = dto.IgnorePoison;
        IgnoreBlindness = dto.IgnoreBlindness;
        IgnoreConfusion = dto.IgnoreConfusion;
        IgnoreDiseased  = dto.IgnoreDiseased;
        GameEntryCommand = dto.GameEntryCommand;
        GameExitCommand  = dto.GameExitCommand;
        MaxTrapSearchAttempts = dto.MaxTrapSearchAttempts;
        MaxTrapDisarmAttempts = dto.MaxTrapDisarmAttempts;
        MaxBashAttempts       = dto.MaxBashAttempts;
        MaxPickAttempts       = dto.MaxPickAttempts;
        PicklocksOverBash     = dto.PicklocksOverBash;
        LogMovementHopTiming  = dto.LogMovementHopTiming;
        VerboseCombat          = dto.VerboseCombat;
        VerboseRoomClassifier  = dto.VerboseRoomClassifier;
        VerboseCasting         = dto.VerboseCasting;
        VerboseCash            = dto.VerboseCash;
        VerboseStealth         = dto.VerboseStealth;
        WriteCombatRoundTrace  = dto.WriteCombatRoundTrace;
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
        svcs.RemoteCommands.MaxSuicideLivesThreshold = Math.Clamp(dto.MaxSuicideLivesThreshold, 0, 20);
        // Live-mirror the entry / exit commands so HangupHandler picks
        // them up without a profile reload. Blank values fall back to
        // the DTO defaults — see AppServices.ApplyOtherFromActiveProfile
        // for the canonical guard.
        svcs.GameCommands.EntryCommand = string.IsNullOrWhiteSpace(dto.GameEntryCommand)
            ? new OtherSettings().GameEntryCommand
            : dto.GameEntryCommand;
        svcs.GameCommands.ExitCommand  = string.IsNullOrWhiteSpace(dto.GameExitCommand)
            ? new OtherSettings().GameExitCommand
            : dto.GameExitCommand;
        // @trap attempt caps — push into the live manager so the next
        // queued @trap honours the edit without a profile reload.
        svcs.TrapDisarm.MaxSearchAttempts = Math.Clamp(dto.MaxTrapSearchAttempts, 1, 100);
        svcs.TrapDisarm.MaxDisarmAttempts = Math.Clamp(dto.MaxTrapDisarmAttempts, 1, 50);
        // Calibrator toggle — live-mirror so the user can flip it from
        // the Settings dialog without an Apply + profile reload cycle.
        svcs.HopCalibrator.Enabled = dto.LogMovementHopTiming;
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnMaxSuicideLivesThresholdChanged(int value) => MarkDirty();
    partial void OnIgnorePoisonChanged(bool value)    => MarkDirty();
    partial void OnIgnoreBlindnessChanged(bool value) => MarkDirty();
    partial void OnIgnoreConfusionChanged(bool value) => MarkDirty();
    partial void OnIgnoreDiseasedChanged(bool value)  => MarkDirty();
    partial void OnGameEntryCommandChanged(string value) => MarkDirty();
    partial void OnGameExitCommandChanged(string value)  => MarkDirty();
    partial void OnPlayerCleanupDaysChanged(int value)   => MarkDirty();
    partial void OnMaxTrapSearchAttemptsChanged(int value) => MarkDirty();
    partial void OnMaxTrapDisarmAttemptsChanged(int value) => MarkDirty();
    partial void OnMaxBashAttemptsChanged(int value)       => MarkDirty();
    partial void OnMaxPickAttemptsChanged(int value)       => MarkDirty();
    partial void OnPicklocksOverBashChanged(bool value)    => MarkDirty();
    partial void OnLogMovementHopTimingChanged(bool value) => MarkDirty();

    partial void OnVerboseCombatChanged(bool value)         => MarkDirty();
    partial void OnVerboseRoomClassifierChanged(bool value) => MarkDirty();
    partial void OnVerboseCastingChanged(bool value)        => MarkDirty();
    partial void OnVerboseCashChanged(bool value)           => MarkDirty();
    partial void OnVerboseStealthChanged(bool value)        => MarkDirty();
    partial void OnWriteCombatRoundTraceChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
