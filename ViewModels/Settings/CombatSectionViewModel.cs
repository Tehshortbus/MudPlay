using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Combat" tab — auto-attack engine config. Weapon Combat picks the items
/// for each role, Targeting governs target order + attack timing + polite
/// mode, Options governs room-skip + BS flow, Spell Combat picks per-role
/// damage / debuff spells. Persists as the <c>"Combat"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Wires DTO storage now (PR 9.0a sub-C); the engine that reads these
/// values arrives in PR 9.A (CombatManager). No <c>ApplyToServices</c>
/// call yet — CombatManager will subscribe to
/// <see cref="ProfileService.ProfileLoaded"/> when it lands and re-read
/// the DTO from there.
/// </remarks>
public sealed partial class CombatSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Combat";

    private readonly ProfileService _profile;
    private readonly ItemNameStore _items;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "combat";
    public override string Title => "Combat";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new CombatSectionView { DataContext = this };

    /// <summary>Weapon-name suggestions (<c>ItemType == 1</c>) for the
    /// weapon typeahead boxes — sourced from the active game-data set's
    /// <c>Items.json</c>. Refreshes when the active set changes.</summary>
    public IReadOnlyList<string> WeaponSuggestions => _items.WeaponNames;

    /// <summary>Off-hand-name suggestions (<c>Worn == 12</c>) for the
    /// off-hand typeahead boxes — shields, tomes, instruments. Refreshes
    /// when the active set changes.</summary>
    public IReadOnlyList<string> OffHandSuggestions => _items.OffHandNames;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Combat", "Weapon", "Normal weapon", "Alternate weapon",
        "BS weapon", "BS weapon off-hand", "Off-hand",
        "Combat priority", "Priority order", "Backstab priority",
        "Debuffing priority", "Spells priority", "Physical priority",
        "Normal weapon attack command", "Alternate weapon attack command",
        "Attack command",
        "Do BS attacks", "Don't BS if multi-attack", "Run if BS fails",
        "Clear hostiles when seen hidden",
        "Target order", "Normal", "Reverse",
        "Attack Order", "Attack timing", "Default", "Attack Last Party", "Attack Last Room", "Attack After",
        "Target Priority", "Attack what party leader attacks", "Attack what player attacks",
        "Follow leader", "Follow player", "Player name",
        "Polite mode", "Skip room", "Attack different",
        "Min monsters", "Max monsters", "Run distance",
        "When running away", "Go backwards if running", "Break combat before running",
        "Failure tracking", "No effect threshold",
        "Multi-attack", "Debuff single target", "Debuff AOE",
        "Normal attack spell", "Alternate attack spell",
        "Min enemies", "Max casts per room", "Minimum mana per cast",
        "Show combat round totals", "Display",
    };

    // ----- Wire command ---------------------------------------------

    [ObservableProperty] private string _normalAttackCommand = "a";
    [ObservableProperty] private string _alternateAttackCommand = "a";

    // ----- Combat priority order ------------------------------------

    /// <summary>The four combat categories in their fixed key order; the
    /// ranking VM reorders them and reports each category's 1-based rank.</summary>
    private static readonly (string Key, string Label, string? Tip)[] _priorityDefs =
    {
        ("Backstab", "Backstab",
            "Priority of the backstab opener. Backstab only fires when ranked 1 (and Do BS attacks is on, we're sneaking, and the room has no SeeHidden monster); at any other rank it is ignored."),
        ("Debuffing", "Debuffing",
            "Priority of the debuffing category (area debuff, else single-target debuff)."),
        ("Spells", "Spells",
            "Priority of the attack-spell category (multi → normal → alternate attack spell cascade)."),
        ("Physical", "Physical",
            "Priority of the physical weapon swing — the terminal fallback that always applies. Placing it above another category suppresses that category whenever a swing is possible."),
    };

    /// <summary>Reorderable per-round action order. Row position is the rank,
    /// so the four categories always form a clean 1..4 permutation.</summary>
    public PriorityRankingViewModel Priority { get; }

    // ----- Weapon slots --------------------------------------------

    [ObservableProperty] private string? _normalWeapon;
    [ObservableProperty] private string? _normalOffHand;
    [ObservableProperty] private string? _alternateWeapon;
    [ObservableProperty] private string? _alternateOffHand;
    [ObservableProperty] private string? _backstabWeapon;
    [ObservableProperty] private string? _backstabOffHand;

    // ----- Backstab options -----------------------------------------

    [ObservableProperty] private bool _doBackstab;
    [ObservableProperty] private bool _skipBackstabIfMultiAttack = true;
    [ObservableProperty] private bool _runIfBackstabFails;
    [ObservableProperty] private bool _clearHostilesWhenSeenHidden;

    // ----- Targeting ------------------------------------------------

    [ObservableProperty] private bool _targetOrderNormal = true;
    [ObservableProperty] private bool _targetOrderReverse;

    /// <summary>
    /// Bound to the Target Priority ComboBox (the "who"). Default
    /// <see cref="Models.Profile.TargetPriority.Default"/> — pick our own
    /// target from game data; the follow modes mirror the party leader /
    /// a named member's announced monster.
    /// </summary>
    [ObservableProperty] private TargetPriority _targetPriority = TargetPriority.Default;

    /// <summary>Party member whose target we mirror when
    /// <see cref="TargetPriority"/> is
    /// <see cref="Models.Profile.TargetPriority.FollowMember"/>.</summary>
    [ObservableProperty] private string _targetPriorityMemberName = string.Empty;

    /// <summary>True when the Target-Priority member-name field is enabled —
    /// only meaningful for
    /// <see cref="Models.Profile.TargetPriority.FollowMember"/>.</summary>
    public bool TargetPriorityMemberEnabled =>
        TargetPriority == TargetPriority.FollowMember;

    /// <summary>Target Priority dropdown rows — friendly labels paired with
    /// the enum value.</summary>
    public IReadOnlyList<TargetPriorityOption> TargetPriorityOptions { get; } =
        new[]
        {
            new TargetPriorityOption(TargetPriority.Default,      "Default"),
            new TargetPriorityOption(TargetPriority.FollowLeader, "Attack what party leader attacks"),
            new TargetPriorityOption(TargetPriority.FollowMember, "Attack what player attacks"),
        };

    /// <summary>
    /// Bound to the Attack Order ComboBox (the "when"). Default
    /// <see cref="AttackTiming.Default"/> — no party / room re-fire.
    /// </summary>
    [ObservableProperty] private AttackTiming _attackTiming = AttackTiming.Default;

    /// <summary>Player name required when
    /// <see cref="AttackTiming"/> is <see cref="AttackTiming.AttackAfter"/>.</summary>
    [ObservableProperty] private string _attackAfterPlayerName = string.Empty;

    /// <summary>True when the player-name field is enabled — only
    /// meaningful for <see cref="AttackTiming.AttackAfter"/>.</summary>
    public bool AttackAfterEnabled => AttackTiming == AttackTiming.AttackAfter;

    /// <summary>Available <see cref="AttackTiming"/> values for the
    /// ComboBox ItemsSource.</summary>
    public IReadOnlyList<AttackTiming> AttackTimingOptions { get; } =
        new[]
        {
            AttackTiming.Default,
            AttackTiming.AttackLastParty,
            AttackTiming.AttackLastRoom,
            AttackTiming.AttackAfter,
        };

    /// <summary>Bound to the PoliteMode ComboBox <c>SelectedItem</c>.
    /// Default <see cref="PoliteMode.Off"/>.</summary>
    [ObservableProperty] private PoliteMode _politeMode = PoliteMode.Off;

    public IReadOnlyList<PoliteMode> PoliteModeOptions { get; } =
        new[]
        {
            PoliteMode.Off,
            PoliteMode.WaitForOthers,
            PoliteMode.SkipRoom,
            PoliteMode.AttackDifferent,
        };

    // ----- Room-skip + failure --------------------------------------

    [ObservableProperty] private int _minMonstersInRoom;
    [ObservableProperty] private int _maxMonstersInRoom = 20;
    [ObservableProperty] private int _runDistance = 3;
    [ObservableProperty] private int _noEffectFailureThreshold = 1;

    // ----- Run-away (flee) behaviour --------------------------------
    // Graduated from the Other tab — they coordinate with RunDistance
    // above. HealthManager.TryFlee reads RunDirection / BreakBeforeFleeing
    // from CombatSettings.

    /// <summary>When checked (default) an auto-flee retraces the rooms just
    /// walked through (<see cref="RunDirection.Backward"/>); when unchecked it
    /// pushes forward along the active walker path
    /// (<see cref="RunDirection.Forward"/>).</summary>
    [ObservableProperty] private bool _goBackwardsIfRunning = true;

    /// <summary>When checked (default) <c>break</c> is sent before the first
    /// flee move so auto-attack disengages and the move lands cleanly.</summary>
    [ObservableProperty] private bool _breakBeforeFleeing = true;

    // ----- Spell combat ---------------------------------------------

    [ObservableProperty] private bool _spellManaModePercentage = true;
    [ObservableProperty] private bool _spellManaModeAbsolute;

    // Five slots × 4 fields each — flat to keep the AXAML grid binding straightforward.

    [ObservableProperty] private string? _multiAttackSpellName;
    [ObservableProperty] private int _multiAttackMinEnemies;
    [ObservableProperty] private int _multiAttackMaxCastsPerRoom;
    [ObservableProperty] private int _multiAttackMinManaPerCast;

    [ObservableProperty] private string? _areaDebuffSpellName;
    [ObservableProperty] private int _areaDebuffMinEnemies;
    [ObservableProperty] private int _areaDebuffMaxCastsPerRoom;
    [ObservableProperty] private int _areaDebuffMinManaPerCast;

    [ObservableProperty] private string? _singleTargetDebuffSpellName;
    [ObservableProperty] private int _singleTargetDebuffMaxCastsPerRoom;
    [ObservableProperty] private int _singleTargetDebuffMinManaPerCast;

    [ObservableProperty] private string? _normalAttackSpellName;
    [ObservableProperty] private int _normalAttackSpellMaxCastsPerRoom;
    [ObservableProperty] private int _normalAttackSpellMinManaPerCast;

    [ObservableProperty] private string? _alternateAttackSpellName;
    [ObservableProperty] private int _alternateAttackSpellMaxCastsPerRoom;
    [ObservableProperty] private int _alternateAttackSpellMinManaPerCast;

    // ----- Display --------------------------------------------------

    [ObservableProperty] private bool _showCombatRoundTotals;

    public CombatSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.ItemNames) { }

    public CombatSectionViewModel(ProfileService profile, ItemNameStore items)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(items);
        _profile = profile;
        _items = items;
        Priority = new PriorityRankingViewModel(MarkDirty);
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _items.StoreReloaded += OnItemStoreReloaded;
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnItemStoreReloaded()
    {
        OnPropertyChanged(nameof(WeaponSuggestions));
        OnPropertyChanged(nameof(OffHandSuggestions));
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        CombatSettings dto = new()
        {
            NormalAttackCommand        = NormalAttackCommand ?? "a",
            AlternateAttackCommand     = AlternateAttackCommand ?? "a",

            PriorityBackstab  = Priority.RankOf("Backstab"),
            PriorityDebuffing = Priority.RankOf("Debuffing"),
            PrioritySpells    = Priority.RankOf("Spells"),
            PriorityPhysical  = Priority.RankOf("Physical"),

            NormalWeapon       = NullIfBlank(NormalWeapon),
            NormalOffHand      = NullIfBlank(NormalOffHand),
            AlternateWeapon    = NullIfBlank(AlternateWeapon),
            AlternateOffHand   = NullIfBlank(AlternateOffHand),
            BackstabWeapon     = NullIfBlank(BackstabWeapon),
            BackstabOffHand    = NullIfBlank(BackstabOffHand),

            DoBackstab                   = DoBackstab,
            SkipBackstabIfMultiAttack    = SkipBackstabIfMultiAttack,
            RunIfBackstabFails           = RunIfBackstabFails,
            ClearHostilesWhenSeenHidden  = ClearHostilesWhenSeenHidden,

            TargetOrder              = TargetOrderReverse ? TargetOrder.Reverse : TargetOrder.Normal,
            TargetPriority           = TargetPriority,
            TargetPriorityMemberName = TargetPriority == TargetPriority.FollowMember
                                       ? NullIfBlank(TargetPriorityMemberName)
                                       : null,
            AttackTiming             = AttackTiming,
            AttackAfterPlayerName    = AttackTiming == AttackTiming.AttackAfter
                                       ? NullIfBlank(AttackAfterPlayerName)
                                       : null,
            PoliteMode               = PoliteMode,

            MinMonstersInRoom = Math.Clamp(MinMonstersInRoom, 0, 20),
            MaxMonstersInRoom = Math.Clamp(MaxMonstersInRoom, 1, 20),
            RunDistance       = Math.Clamp(RunDistance, 1, 100),
            RunDirection      = GoBackwardsIfRunning ? RunDirection.Backward : RunDirection.Forward,
            BreakBeforeFleeing = BreakBeforeFleeing,

            NoEffectFailureThreshold = Math.Clamp(NoEffectFailureThreshold, 1, 20),

            SpellManaThresholdMode = SpellManaModeAbsolute
                                     ? ThresholdMode.Absolute
                                     : ThresholdMode.Percentage,

            MultiAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(MultiAttackSpellName),
                MinEnemies      = ClampSpell(MultiAttackMinEnemies),
                MaxCastsPerRoom = ClampSpell(MultiAttackMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(MultiAttackMinManaPerCast),
            },
            AreaDebuffSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(AreaDebuffSpellName),
                MinEnemies      = ClampSpell(AreaDebuffMinEnemies),
                MaxCastsPerRoom = ClampSpell(AreaDebuffMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(AreaDebuffMinManaPerCast),
            },
            SingleTargetDebuffSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(SingleTargetDebuffSpellName),
                MinEnemies      = 0, // ignored for single-target
                MaxCastsPerRoom = ClampSpell(SingleTargetDebuffMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(SingleTargetDebuffMinManaPerCast),
            },
            NormalAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(NormalAttackSpellName),
                MinEnemies      = 0,
                MaxCastsPerRoom = ClampSpell(NormalAttackSpellMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(NormalAttackSpellMinManaPerCast),
            },
            AlternateAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(AlternateAttackSpellName),
                MinEnemies      = 0,
                MaxCastsPerRoom = ClampSpell(AlternateAttackSpellMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(AlternateAttackSpellMinManaPerCast),
            },

            ShowCombatRoundTotals = ShowCombatRoundTotals,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Spell-slot integer fields share a 0..100,000 range — covers
    /// both percentage and absolute mana thresholds + the cast / enemy
    /// counts which top out far below.</summary>
    private static int ClampSpell(int value) => Math.Clamp(value, 0, 100_000);

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
        CombatSettings dto = ReadOrDefault();

        NormalAttackCommand     = dto.NormalAttackCommand ?? "a";
        AlternateAttackCommand  = dto.AlternateAttackCommand ?? "a";

        Priority.Load(_priorityDefs, key => key switch
        {
            "Backstab"  => dto.PriorityBackstab,
            "Debuffing" => dto.PriorityDebuffing,
            "Spells"    => dto.PrioritySpells,
            "Physical"  => dto.PriorityPhysical,
            _           => 99,
        });

        NormalWeapon       = dto.NormalWeapon;
        NormalOffHand      = dto.NormalOffHand;
        AlternateWeapon    = dto.AlternateWeapon;
        AlternateOffHand   = dto.AlternateOffHand;
        BackstabWeapon     = dto.BackstabWeapon;
        BackstabOffHand    = dto.BackstabOffHand;

        DoBackstab                  = dto.DoBackstab;
        SkipBackstabIfMultiAttack   = dto.SkipBackstabIfMultiAttack;
        RunIfBackstabFails          = dto.RunIfBackstabFails;
        ClearHostilesWhenSeenHidden = dto.ClearHostilesWhenSeenHidden;

        TargetOrderNormal  = dto.TargetOrder == TargetOrder.Normal;
        TargetOrderReverse = dto.TargetOrder == TargetOrder.Reverse;

        TargetPriority           = dto.TargetPriority;
        TargetPriorityMemberName = dto.TargetPriorityMemberName ?? string.Empty;

        AttackTiming           = dto.AttackTiming;
        AttackAfterPlayerName  = dto.AttackAfterPlayerName ?? string.Empty;
        PoliteMode             = dto.PoliteMode;

        MinMonstersInRoom = dto.MinMonstersInRoom;
        MaxMonstersInRoom = dto.MaxMonstersInRoom;
        RunDistance       = dto.RunDistance;
        GoBackwardsIfRunning = dto.RunDirection == RunDirection.Backward;
        BreakBeforeFleeing   = dto.BreakBeforeFleeing;

        NoEffectFailureThreshold = dto.NoEffectFailureThreshold;

        SpellManaModePercentage = dto.SpellManaThresholdMode == ThresholdMode.Percentage;
        SpellManaModeAbsolute   = dto.SpellManaThresholdMode == ThresholdMode.Absolute;

        MultiAttackSpellName       = dto.MultiAttackSpell.SpellName;
        MultiAttackMinEnemies      = dto.MultiAttackSpell.MinEnemies;
        MultiAttackMaxCastsPerRoom = dto.MultiAttackSpell.MaxCastsPerRoom;
        MultiAttackMinManaPerCast  = dto.MultiAttackSpell.MinManaPerCast;

        AreaDebuffSpellName       = dto.AreaDebuffSpell.SpellName;
        AreaDebuffMinEnemies      = dto.AreaDebuffSpell.MinEnemies;
        AreaDebuffMaxCastsPerRoom = dto.AreaDebuffSpell.MaxCastsPerRoom;
        AreaDebuffMinManaPerCast  = dto.AreaDebuffSpell.MinManaPerCast;

        SingleTargetDebuffSpellName       = dto.SingleTargetDebuffSpell.SpellName;
        SingleTargetDebuffMaxCastsPerRoom = dto.SingleTargetDebuffSpell.MaxCastsPerRoom;
        SingleTargetDebuffMinManaPerCast  = dto.SingleTargetDebuffSpell.MinManaPerCast;

        NormalAttackSpellName             = dto.NormalAttackSpell.SpellName;
        NormalAttackSpellMaxCastsPerRoom  = dto.NormalAttackSpell.MaxCastsPerRoom;
        NormalAttackSpellMinManaPerCast   = dto.NormalAttackSpell.MinManaPerCast;

        AlternateAttackSpellName            = dto.AlternateAttackSpell.SpellName;
        AlternateAttackSpellMaxCastsPerRoom = dto.AlternateAttackSpell.MaxCastsPerRoom;
        AlternateAttackSpellMinManaPerCast  = dto.AlternateAttackSpell.MinManaPerCast;

        ShowCombatRoundTotals = dto.ShowCombatRoundTotals;
    }

    private CombatSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new CombatSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new CombatSettings();
        try
        {
            return JsonSerializer.Deserialize<CombatSettings>(json) ?? new CombatSettings();
        }
        catch
        {
            return new CombatSettings();
        }
    }

    // ----- IsDirty plumbing -----------------------------------------

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    // Master + attack command
    partial void OnNormalAttackCommandChanged(string value)      => MarkDirty();
    partial void OnAlternateAttackCommandChanged(string value)   => MarkDirty();

    // Weapon slots
    partial void OnNormalWeaponChanged(string? value)            => MarkDirty();
    partial void OnNormalOffHandChanged(string? value)           => MarkDirty();
    partial void OnAlternateWeaponChanged(string? value)         => MarkDirty();
    partial void OnAlternateOffHandChanged(string? value)        => MarkDirty();
    partial void OnBackstabWeaponChanged(string? value)          => MarkDirty();
    partial void OnBackstabOffHandChanged(string? value)         => MarkDirty();

    // BS options
    partial void OnDoBackstabChanged(bool value)                    => MarkDirty();
    partial void OnSkipBackstabIfMultiAttackChanged(bool value)     => MarkDirty();
    partial void OnRunIfBackstabFailsChanged(bool value)            => MarkDirty();
    partial void OnClearHostilesWhenSeenHiddenChanged(bool value)   => MarkDirty();

    // Targeting
    partial void OnTargetOrderNormalChanged(bool value)
    {
        if (value) TargetOrderReverse = false;
        MarkDirty();
    }
    partial void OnTargetOrderReverseChanged(bool value)
    {
        if (value) TargetOrderNormal = false;
        MarkDirty();
    }
    partial void OnTargetPriorityChanged(TargetPriority value)
    {
        // Refresh the FollowMember member-name field's enabled state.
        OnPropertyChanged(nameof(TargetPriorityMemberEnabled));
        MarkDirty();
    }
    partial void OnTargetPriorityMemberNameChanged(string value) => MarkDirty();
    partial void OnAttackTimingChanged(AttackTiming value)
    {
        // Refresh the AttackAfter player-name field's enabled state.
        OnPropertyChanged(nameof(AttackAfterEnabled));
        MarkDirty();
    }
    partial void OnAttackAfterPlayerNameChanged(string value)    => MarkDirty();
    partial void OnPoliteModeChanged(PoliteMode value)           => MarkDirty();

    // Room-skip + failure
    partial void OnMinMonstersInRoomChanged(int value)           => MarkDirty();
    partial void OnMaxMonstersInRoomChanged(int value)           => MarkDirty();
    partial void OnRunDistanceChanged(int value)                 => MarkDirty();
    partial void OnGoBackwardsIfRunningChanged(bool value)       => MarkDirty();
    partial void OnBreakBeforeFleeingChanged(bool value)         => MarkDirty();
    partial void OnNoEffectFailureThresholdChanged(int value)    => MarkDirty();

    // Spell mana mode
    partial void OnSpellManaModePercentageChanged(bool value)
    {
        if (value) SpellManaModeAbsolute = false;
        MarkDirty();
    }
    partial void OnSpellManaModeAbsoluteChanged(bool value)
    {
        if (value) SpellManaModePercentage = false;
        MarkDirty();
    }

    // Spell slot — multi-attack
    partial void OnMultiAttackSpellNameChanged(string? value)        => MarkDirty();
    partial void OnMultiAttackMinEnemiesChanged(int value)           => MarkDirty();
    partial void OnMultiAttackMaxCastsPerRoomChanged(int value)      => MarkDirty();
    partial void OnMultiAttackMinManaPerCastChanged(int value)       => MarkDirty();

    // Spell slot — AOE debuff
    partial void OnAreaDebuffSpellNameChanged(string? value)         => MarkDirty();
    partial void OnAreaDebuffMinEnemiesChanged(int value)            => MarkDirty();
    partial void OnAreaDebuffMaxCastsPerRoomChanged(int value)       => MarkDirty();
    partial void OnAreaDebuffMinManaPerCastChanged(int value)        => MarkDirty();

    // Spell slot — single-target debuff
    partial void OnSingleTargetDebuffSpellNameChanged(string? value)        => MarkDirty();
    partial void OnSingleTargetDebuffMaxCastsPerRoomChanged(int value)      => MarkDirty();
    partial void OnSingleTargetDebuffMinManaPerCastChanged(int value)       => MarkDirty();

    // Spell slot — normal attack spell
    partial void OnNormalAttackSpellNameChanged(string? value)          => MarkDirty();
    partial void OnNormalAttackSpellMaxCastsPerRoomChanged(int value)   => MarkDirty();
    partial void OnNormalAttackSpellMinManaPerCastChanged(int value)    => MarkDirty();

    // Spell slot — alternate attack spell
    partial void OnAlternateAttackSpellNameChanged(string? value)          => MarkDirty();
    partial void OnAlternateAttackSpellMaxCastsPerRoomChanged(int value)   => MarkDirty();
    partial void OnAlternateAttackSpellMinManaPerCastChanged(int value)    => MarkDirty();

    // Display
    partial void OnShowCombatRoundTotalsChanged(bool value)      => MarkDirty();

    /// <summary>One Target Priority dropdown row — pairs the enum value with
    /// its friendly label.</summary>
    public sealed record TargetPriorityOption(TargetPriority Value, string Label);
}
