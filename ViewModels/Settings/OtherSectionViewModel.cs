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
    [ObservableProperty] private int _maxSuicideLivesThreshold = 3;

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

    // ----- Inline stub catalog (un-wired Phase 7 / 11 / 13 fields) -----

    /// <summary>
    /// The remaining un-wired Other-tab fields, rendered inline below
    /// the wired group as disabled placeholders. Each entry's tooltip
    /// names the owning phase; entries are removed from this list as
    /// their consumer engines wire through <see cref="OtherSettings"/>.
    /// </summary>
    public IReadOnlyList<StubGroup> StubGroups { get; } = new[]
    {
        new StubGroup("Locks, traps, walker behaviour", new[]
        {
            new StubField("Pick locks instead of bashing",   StubFieldKind.Check, "Phase 13 — walker prefers lockpicking when the skill is trained."),
            new StubField("Attempt to disarm traps",         StubFieldKind.Check, "Phase 7 PR 7.22 — walker pauses at trapped exits and tries disarm."),
            new StubField("Auto-train",                      StubFieldKind.Check, "Phase 13 — auto-spend CP at a trainer when allocations are pending."),
            new StubField("Teleport to avoid combat instead of hanging", StubFieldKind.Check,
                          "Phase 7 — when fleeing, use sys-goto (stock) or a town token (paradigm) instead of dropping the line."),
            new StubField("Allow hangup when not AFK",       StubFieldKind.Check, "Phase 13 — gate hangup unless AFK Mode is on."),
            new StubField("Allow hangup in all-off mode",    StubFieldKind.Check, "Phase 13 — gate hangup when every Auto-* toggle is off."),
            new StubField("Hangup if naked",                 StubFieldKind.Check, "Phase 13 — recovery safety, disconnect if equipment got lost."),
            new StubField("Search rooms if item needed",     StubFieldKind.Check, "Phase 7 — walker auto-searches when item-collect requires it."),
            new StubField("Go backwards if running",         StubFieldKind.Check, "Phase 13 — flee direction prefers retracing rather than pushing forward."),
            new StubField("Backwards if warning",            StubFieldKind.Check, "Phase 13 — same direction logic but triggered by warning-state instead of HP."),
            new StubField("Break combat before running",     StubFieldKind.Check, "Phase 13 — stop swinging before issuing the flee command."),
            new StubField("Don't move unless sneaking",      StubFieldKind.Check, "Phase 7 — walker pause-gate when stealth drops."),
            new StubField("Provide light in dimly lit rooms", StubFieldKind.Check, "Phase 7 — pairs with Spells → Room light."),
        }),
        // Ignored ailments group graduated to a real wired section above
        // (rendered inline in OtherSectionView.axaml). Diseased added per
        // user direction so the four ailment families are symmetric.
        new StubGroup("Auto-engage on connect", new[]
        {
            new StubField("Auto-Combat on",       StubFieldKind.Check, "Phase 13 PR 13.A — flips CombatManager on at logon."),
            new StubField("Auto-Rest on",         StubFieldKind.Check, "Phase 13 PR 13.B — flips HealthManager rest on at logon."),
            new StubField("Auto-Heal on",         StubFieldKind.Check, "Phase 13 PR 13.D — flips CastingDirector self-heal on at logon."),
            new StubField("Bless while resting",  StubFieldKind.Check, "Phase 13 PR 13.D — CastingDirector recasts party-buffs during downtime."),
            new StubField("Bless during combat",  StubFieldKind.Check, "Phase 13 PR 13.D — extends bless casting into active rounds."),
        }),
        new StubGroup("Retry counts", new[]
        {
            new StubField("Attempt bash N times",      StubFieldKind.Numeric, "Phase 7 — retry cap on door / chest bash."),
            new StubField("Attempt pick-lock N times", StubFieldKind.Numeric, "Phase 7 — retry cap on lockpicking."),
            new StubField("Attempt disarm N times",    StubFieldKind.Numeric, "Phase 7 PR 7.22 — retry cap on trap disarm before falling back."),
        }),
        new StubGroup("Commands + retention", new[]
        {
            new StubField("Command splitter character",     StubFieldKind.Text,    "Splits multi-command input (default `;`)."),
            new StubField("Game entry command",             StubFieldKind.Text,    "One-shot sent on first prompt after logon (e.g. `set wimpy 30`)."),
            new StubField("Game exit command",              StubFieldKind.Text,    "Sent before disconnect (e.g. `bye`)."),
            new StubField("Backscroll buffer size",         StubFieldKind.Numeric, "Phase 1 — lines retained in the in-memory ring.", "lines"),
            new StubField("Inactive player cleanup window", StubFieldKind.Numeric, "Phase 5 PR 5.19 — drop Players-tab records last seen this many days ago.", "days"),
            new StubField("Debug log retention",            StubFieldKind.Numeric, "Phase 0 — prune Data/Logs/ entries older than this on app launch.", "days"),
        }),
    };

    public OtherSectionViewModel() : this(AppServices.Current.Profile) { }

    public OtherSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
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
            MaxSuicideLivesThreshold = Math.Clamp(MaxSuicideLivesThreshold, 0, 20),
            IgnorePoison    = IgnorePoison,
            IgnoreBlindness = IgnoreBlindness,
            IgnoreConfusion = IgnoreConfusion,
            IgnoreDiseased  = IgnoreDiseased,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

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
        AppServices.Current.RemoteCommands.MaxSuicideLivesThreshold = Math.Clamp(dto.MaxSuicideLivesThreshold, 0, 20);
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

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
