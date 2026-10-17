using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Combat" tab — auto-attack engine config. Weapon Combat picks the items for
// each role, Targeting governs target order + attack timing + polite mode,
// Options governs room-skip + BS flow, Spell Combat picks per-role damage /
// debuff spells. Persists as the "Combat" entry in CharacterProfile.Settings.
//
// Wires DTO storage only. There's no ApplyToServices call — CombatManager (the
// auto-attack engine that reads these values) subscribes to
// ProfileService.ProfileLoaded and re-reads the DTO from there.
public sealed partial class CombatSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Combat";

    private readonly ProfileService _profile;
    private readonly Game.Spells.SpellbookState _spellbook;
    private readonly Game.PlayerState? _state;
    private readonly GameDataCache? _gameData;
    private Control? _view;
    private bool _suppressDirty;

    public override string Id => "combat";
    public override string Title => "Combat";

    // Dirtiness is owned by the shared staging session — a combat profile spans the
    // Combat, Health, Spells and Party tabs, and the single Commit writes their
    // Settings sections + the profile blob + weapons as one unit, so any edit on any
    // of those tabs dirties it.
    public override bool IsDirty => _session.IsDirty;

    public bool HasProfile => _profile.Current is not null;

    // Known-spell suggestions for the spell-combat typeahead boxes — the current
    // class's learnable list (level gate ignored), ordered by name + distinct by
    // cast-code, from Game.Spells.SpellbookState.AvailablePicks. Each box commits
    // the 4-letter SpellPick.Short cast-code (what the game recognises — the same
    // value CombatSpellSlot.SpellName stores). Refreshes when the spellbook
    // rebuilds (class swap / reroll).
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

    public override Control View => _view ??= new CombatSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Combat",
        "Combat action order", "Spells first", "Physical first",
        "Custom round cycle", "Physical rounds", "Spell rounds", "Spells till death",
        "Start on spell",
        "Normal weapon attack command", "Alternate weapon attack command",
        "Attack command",
        "Do BS attacks", "Don't BS if multi-attack", "Run if BS fails",
        "Clear hostiles when seen hidden",
        "Target order", "Normal", "Reverse",
        "Attack Order", "Attack timing", "Default", "Attack Last Party", "Attack Last Room", "Attack After", "Attack Not Last",
        "Target Priority", "Attack what party leader attacks", "Attack what player attacks",
        "Follow leader", "Follow player", "Player name",
        "Polite mode", "Skip room", "Attack different",
        "Min monsters", "Max monsters", "Run distance",
        "When running away", "Go backwards if running", "Break combat before running",
        "Failure tracking", "No effect threshold",
        "Multi-attack", "Debuff single target", "Debuff AOE",
        "Normal attack spell", "Alternate attack spell",
        "Drain spell", "Life drain", "Drain HP trigger", "Drains override AOE",
        "Min enemies", "Max casts per room", "Minimum mana per cast",
        "Show combat round totals", "Display",
    };

    // ----- Wire command ---------------------------------------------

    [ObservableProperty] private string _normalAttackCommand = "a";
    [ObservableProperty] private string _alternateAttackCommand = "a";

    // ----- Combat action order --------------------------------------

    // Bound to the Combat action-order ComboBox: which action to prefer as the
    // round's one combat action. Default SpellsFirst.
    [ObservableProperty] private CombatActionOrder _actionOrder = CombatActionOrder.SpellsFirst;

    // Dropdown rows — friendly labels paired with the enum value.
    public IReadOnlyList<ActionOrderOption> ActionOrderOptions { get; } =
        new[]
        {
            new ActionOrderOption(CombatActionOrder.SpellsFirst,   "Spells first"),
            new ActionOrderOption(CombatActionOrder.PhysicalFirst, "Physical first"),
            new ActionOrderOption(CombatActionOrder.AlternateSpellPhysical, "Alternate — spell, then physical"),
            new ActionOrderOption(CombatActionOrder.AlternatePhysicalSpell, "Alternate — physical, then spell"),
            new ActionOrderOption(CombatActionOrder.CustomRoundCycle, "Custom round cycle"),
        };

    // ----- Round-cycle action order (ActionOrder = CustomRoundCycle) -----

    [ObservableProperty] private int _cycleRoundsPhysical = 1;
    [ObservableProperty] private int _cycleRoundsSpell = 1;
    [ObservableProperty] private bool _cycleStartOnSpell;

    // True when the round-cycle fields should be enabled — only meaningful for
    // ActionOrder.CustomRoundCycle. The fields stay visible (not collapsed) so a
    // user switching away and back doesn't lose sight of a value they tuned.
    public bool CycleFieldsEnabled => ActionOrder == CombatActionOrder.CustomRoundCycle;

    // ----- Weapon slots (per-profile) -------------------------------
    // Primary / alternate weapons + off-hands are now PER COMBAT PROFILE. The active
    // profile's weapons live in the Workshop Default gear set — the surface the
    // combat overlay (EquipmentWeaponSync) + the auto-equip coordinator read — so
    // editing here and editing the Workshop's Default-set weapon rows are the same
    // live loadout, kept in sync. Switching a profile writes its stored weapons into
    // the Default set. Backstab gear stays global on the Workshop Backstab set.
    [ObservableProperty] private string? _normalWeapon;
    [ObservableProperty] private string? _normalOffHand;
    [ObservableProperty] private string? _alternateWeapon;
    [ObservableProperty] private string? _alternateOffHand;

    // Typeahead suggestions for the weapon pickers — every weapon-slot / off-hand
    // item the active game-data set ships (unfiltered by level/class/alignment; the
    // Default set the pick lands in still block-flags an unwearable choice). Weapon +
    // Alt-Weapon share WeaponSuggestions; OffHand + Alt-OffHand share OffHandSuggestions.
    [ObservableProperty] private IReadOnlyList<string> _weaponSuggestions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _offHandSuggestions = Array.Empty<string>();

    // ----- Backstab options -----------------------------------------

    [ObservableProperty] private bool _doBackstab;
    [ObservableProperty] private bool _skipBackstabIfMultiAttack = true;
    [ObservableProperty] private bool _runIfBackstabFails;
    // Hit and Run covers everything Run if BS fails does, so that box greys out
    // while it's on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunIfBackstabFailsEditable))]
    private bool _hitAndRunTactics;
    public bool IsRunIfBackstabFailsEditable => !HitAndRunTactics;
    [ObservableProperty] private int _hitAndRunMaxRuns = 3;
    [ObservableProperty] private bool _clearHostilesWhenSeenHidden;
    // When the see-hidden clear applies: alone, in a party, or both.
    [ObservableProperty] private bool _seenHiddenClearWhileSolo = true;
    [ObservableProperty] private bool _seenHiddenClearWhileInParty = true;
    [ObservableProperty] private bool _clearHostilesWhenSneakFails;

    // ----- Targeting ------------------------------------------------

    [ObservableProperty] private bool _targetOrderNormal = true;
    [ObservableProperty] private bool _targetOrderReverse;

    // Bound to the Target Priority ComboBox (the "who"). Default
    // TargetPriority.Default — pick our own target from game data; the follow
    // modes mirror the party leader / a named member's announced monster.
    [ObservableProperty] private TargetPriority _targetPriority = TargetPriority.Default;

    // Party member whose target we mirror when TargetPriority is FollowMember.
    [ObservableProperty] private string _targetPriorityMemberName = string.Empty;

    // True when the Target-Priority member-name field is enabled — only meaningful
    // for TargetPriority.FollowMember.
    public bool TargetPriorityMemberEnabled =>
        TargetPriority == TargetPriority.FollowMember;

    // Target Priority dropdown rows — friendly labels paired with the enum value.
    public IReadOnlyList<TargetPriorityOption> TargetPriorityOptions { get; } =
        new[]
        {
            new TargetPriorityOption(TargetPriority.Default,      "Default"),
            new TargetPriorityOption(TargetPriority.FollowLeader, "Attack what party leader attacks"),
            new TargetPriorityOption(TargetPriority.FollowMember, "Attack what player attacks"),
        };

    // Bound to the Attack Order ComboBox (the "when"). Default AttackTiming.Default
    // — no party / room re-fire.
    [ObservableProperty] private AttackTiming _attackTiming = AttackTiming.Default;

    // Player name required when AttackTiming is AttackAfter.
    [ObservableProperty] private string _attackAfterPlayerName = string.Empty;

    // True when the player-name field is enabled — only meaningful for
    // AttackTiming.AttackAfter.
    public bool AttackAfterEnabled => AttackTiming == AttackTiming.AttackAfter;

    // Available AttackTiming values for the ComboBox ItemsSource.
    public IReadOnlyList<AttackTiming> AttackTimingOptions { get; } =
        new[]
        {
            AttackTiming.Default,
            AttackTiming.AttackLastParty,
            AttackTiming.AttackLastRoom,
            AttackTiming.AttackAfter,
            AttackTiming.AttackNotLast,
        };

    // Bound to the PoliteMode ComboBox SelectedItem. Default PoliteMode.Off.
    [ObservableProperty] private PoliteMode _politeMode = PoliteMode.Off;

    public IReadOnlyList<PoliteMode> PoliteModeOptions { get; } =
        new[]
        {
            PoliteMode.Off,
            PoliteMode.WaitForOthers,
            PoliteMode.SkipRoom,
            PoliteMode.AttackDifferent,
        };

    // ----- Room-skip ------------------------------------------------

    [ObservableProperty] private int _minMonstersInRoom;
    [ObservableProperty] private int _maxMonstersInRoom = 20;
    [ObservableProperty] private bool _killAllEngaged;
    [ObservableProperty] private int _runDistance = 2;

    // ----- Run-away (flee) behaviour --------------------------------
    // Graduated from the Other tab — they coordinate with RunDistance
    // above. HealthManager.TryFlee reads RunDirection / BreakBeforeFleeing
    // from CombatSettings.

    // When checked (default) an auto-flee retraces the rooms just walked through
    // (RunDirection.Backward); when unchecked it pushes forward along the active
    // walker path (RunDirection.Forward).
    [ObservableProperty] private bool _goBackwardsIfRunning = true;

    // When checked (default) `break` is sent before the first flee move so
    // auto-attack disengages and the move lands cleanly.
    [ObservableProperty] private bool _breakBeforeFleeing = true;

    // ----- Spell combat ---------------------------------------------

    [ObservableProperty] private bool _spellManaModePercentage = true;
    [ObservableProperty] private bool _spellManaModeAbsolute;

    // One flat set of fields per spell slot, to keep the AXAML grid binding straightforward.

    // MaxCastsPerRoom is nullable: blank = no limit, 0 = never cast, N = cap.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MultiAttackSpellUnlearned))]
    private string? _multiAttackSpellName;
    [ObservableProperty] private int _multiAttackMinEnemies;
    [ObservableProperty] private int? _multiAttackMaxCastsPerRoom;
    [ObservableProperty] private int _multiAttackMinManaPerCast;

    // Room spell 2 — picks up once slot 1's cap or mana floor stops it. No
    // MinEnemies of its own: the room trigger is slot 1's, shared by both.
    [ObservableProperty] private bool _multiAttack2Enabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MultiAttack2SpellUnlearned))]
    private string? _multiAttack2SpellName;
    [ObservableProperty] private int? _multiAttack2MaxCastsPerRoom;
    [ObservableProperty] private int _multiAttack2MinManaPerCast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreaDebuffSpellUnlearned))]
    [NotifyPropertyChangedFor(nameof(AreaDebuffSpellMisSlotWarning))]
    private string? _areaDebuffSpellName;
    [ObservableProperty] private int _areaDebuffMinEnemies;
    [ObservableProperty] private int? _areaDebuffMaxCastsPerRoom;
    [ObservableProperty] private int _areaDebuffMinManaPerCast;
    // Only attempt the AoE debuff on room entry (before the first combat round);
    // abandon it for the room once the fight's underway. Default off.
    [ObservableProperty] private bool _areaDebuffFirstRoundOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SingleTargetDebuffSpellUnlearned))]
    [NotifyPropertyChangedFor(nameof(SingleTargetDebuffSpellMisSlotWarning))]
    private string? _singleTargetDebuffSpellName;
    [ObservableProperty] private int? _singleTargetDebuffMaxCastsPerRoom;
    [ObservableProperty] private int _singleTargetDebuffMinManaPerCast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalAttackSpellUnlearned))]
    private string? _normalAttackSpellName;
    [ObservableProperty] private int? _normalAttackSpellMaxCastsPerRoom;
    [ObservableProperty] private int _normalAttackSpellMinManaPerCast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlternateAttackSpellUnlearned))]
    private string? _alternateAttackSpellName;
    [ObservableProperty] private int? _alternateAttackSpellMaxCastsPerRoom;
    [ObservableProperty] private int _alternateAttackSpellMinManaPerCast;

    // Drain (life-steal) spell — an emergency heal that also attacks. DrainHpTrigger
    // is the HP% at/under which it overrides the round's action; DrainsOverrideAoe
    // lets it pre-empt the room AoE too (off = the AoE wins when rooming).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrainSpellUnlearned))]
    private string? _drainSpellName;
    [ObservableProperty] private int? _drainSpellMaxCastsPerRoom;
    [ObservableProperty] private int _drainSpellMinManaPerCast;
    [ObservableProperty] private int _drainHpTrigger = 50;

    // Red-outline flags: the slot points at a spell this class can learn but the
    // character hasn't (re-raised on the name change above via NotifyPropertyChangedFor,
    // and on a spellbook change via OnSpellbookChanged).
    public bool MultiAttackSpellUnlearned        => IsSpellUnlearned(SpellSuggestions, MultiAttackSpellName);
    public bool MultiAttack2SpellUnlearned       => IsSpellUnlearned(SpellSuggestions, MultiAttack2SpellName);
    public bool AreaDebuffSpellUnlearned         => IsSpellUnlearned(SpellSuggestions, AreaDebuffSpellName);
    public bool SingleTargetDebuffSpellUnlearned => IsSpellUnlearned(SpellSuggestions, SingleTargetDebuffSpellName);
    public bool NormalAttackSpellUnlearned       => IsSpellUnlearned(SpellSuggestions, NormalAttackSpellName);
    public bool AlternateAttackSpellUnlearned    => IsSpellUnlearned(SpellSuggestions, AlternateAttackSpellName);
    public bool DrainSpellUnlearned              => IsSpellUnlearned(SpellSuggestions, DrainSpellName);
    [ObservableProperty] private bool _drainsOverrideAoe;

    // Amber-warning text when a debuff slot points at a spell that resolves but
    // doesn't fit the slot: a non-0-energy attack spell, or a targeting scope
    // that's AoE in the single-target slot (or single in the AoE slot). Null when
    // OK / blank / unresolved (the red unlearned flag covers unresolved). Mirrors
    // the runtime guard in CombatManager — see DebuffTargeting and GAME_MECHANICS.md
    // "Debuff slot spells". Re-raised on the name change (NotifyPropertyChangedFor)
    // and on a spellbook change (OnSpellbookChanged).
    public string? AreaDebuffSpellMisSlotWarning         => DebuffSlotWarning(AreaDebuffSpellName, isArea: true);
    public string? SingleTargetDebuffSpellMisSlotWarning => DebuffSlotWarning(SingleTargetDebuffSpellName, isArea: false);

    private string? DebuffSlotWarning(string? castCode, bool isArea)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        if (_spellbook.FindByCastCode(castCode) is not { } spell) return null; // unresolved — unlearned flag covers it
        if (!Game.Combat.DebuffTargeting.IsBetweenRound(spell.Formula.EnergyCost))
            return $"⚠ costs {spell.Formula.EnergyCost} energy — a debuff needs a 0-energy spell (this is an attack spell)";
        bool targetingOk = isArea
            ? Game.Combat.DebuffTargeting.IsAreaEnemy(spell.Targets)
            : Game.Combat.DebuffTargeting.IsSingleTargetEnemy(spell.Targets);
        if (!targetingOk)
            return isArea
                ? "⚠ not an area spell — belongs in the single-target slot"
                : "⚠ not a single-target spell — belongs in the AoE slot";
        return null;
    }

    // ----- Min-mana-per-cast conversion (mirrors the Health tab) -----

    // Live MaxMa for the % ↔ value conversion labels; 0 until a prompt gives us one.
    public int LiveMaxMa => _state?.MaxMa ?? 0;

    // NumericUpDown Maximum for the Min-mana fields: 100 in Percentage mode (a % can't
    // exceed 100), the absolute ceiling in Value mode.
    public decimal SpellManaMax => SpellManaModePercentage ? 100 : 100_000;

    public string MultiAttackMinManaPerCastConverted         => FormatMana(MultiAttackMinManaPerCast);
    public string MultiAttack2MinManaPerCastConverted        => FormatMana(MultiAttack2MinManaPerCast);
    public string AreaDebuffMinManaPerCastConverted          => FormatMana(AreaDebuffMinManaPerCast);
    public string SingleTargetDebuffMinManaPerCastConverted  => FormatMana(SingleTargetDebuffMinManaPerCast);
    public string NormalAttackSpellMinManaPerCastConverted   => FormatMana(NormalAttackSpellMinManaPerCast);
    public string AlternateAttackSpellMinManaPerCastConverted => FormatMana(AlternateAttackSpellMinManaPerCast);
    public string DrainSpellMinManaPerCastConverted          => FormatMana(DrainSpellMinManaPerCast);

    // Percentage mode shows the absolute mana equivalent ("54/66"); Value mode shows
    // the percentage ("82%"). Empty until a prompt gives us a live MaxMa.
    private string FormatMana(int value)
    {
        int max = LiveMaxMa;
        if (max <= 0) return string.Empty;
        return SpellManaModePercentage
            ? $"{(int)System.Math.Round(max * value / 100.0)}/{max}"
            : $"{(int)System.Math.Round(value * 100.0 / max)}%";
    }

    private void RefreshAllManaConverted()
    {
        OnPropertyChanged(nameof(MultiAttackMinManaPerCastConverted));
        OnPropertyChanged(nameof(MultiAttack2MinManaPerCastConverted));
        OnPropertyChanged(nameof(AreaDebuffMinManaPerCastConverted));
        OnPropertyChanged(nameof(SingleTargetDebuffMinManaPerCastConverted));
        OnPropertyChanged(nameof(NormalAttackSpellMinManaPerCastConverted));
        OnPropertyChanged(nameof(AlternateAttackSpellMinManaPerCastConverted));
        OnPropertyChanged(nameof(DrainSpellMinManaPerCastConverted));
    }

    // In Percentage mode a Min-mana-per-cast can't exceed 100% — snap any Value-mode
    // leftover above 100 back down when the user flips to Percentage.
    private void ClampManaToPercent()
    {
        if (MultiAttackMinManaPerCast > 100) MultiAttackMinManaPerCast = 100;
        if (MultiAttack2MinManaPerCast > 100) MultiAttack2MinManaPerCast = 100;
        if (AreaDebuffMinManaPerCast > 100) AreaDebuffMinManaPerCast = 100;
        if (SingleTargetDebuffMinManaPerCast > 100) SingleTargetDebuffMinManaPerCast = 100;
        if (NormalAttackSpellMinManaPerCast > 100) NormalAttackSpellMinManaPerCast = 100;
        if (AlternateAttackSpellMinManaPerCast > 100) AlternateAttackSpellMinManaPerCast = 100;
        if (DrainSpellMinManaPerCast > 100) DrainSpellMinManaPerCast = 100;
    }

    private static Game.PlayerState? TryGetPlayerState()
    {
        try { return AppServices.Current.PlayerState; }
        catch { return null; }
    }

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Game.PlayerState.MaxMa))
        {
            OnPropertyChanged(nameof(LiveMaxMa));
            RefreshAllManaConverted();
        }
    }

    // ----- Display --------------------------------------------------

    [ObservableProperty] private bool _showCombatRoundTotals;
    [ObservableProperty] private bool _showCombatRoundTotalsSelf;
    [ObservableProperty] private bool _showCombatRoundTotalsParty;
    [ObservableProperty] private bool _showCombatRoundTotalsPlayers;
    [ObservableProperty] private bool _showCombatRoundTotalsMonsters;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCombatRoundTotalsStacked))]
    private bool _showCombatRoundTotalsEachMonster;
    [ObservableProperty] private bool _capRoundTotalsAtMonsterHp;

    // The other half of the stacked / each-monster radio pair.
    public bool ShowCombatRoundTotalsStacked
    {
        get => !ShowCombatRoundTotalsEachMonster;
        set => ShowCombatRoundTotalsEachMonster = !value;
    }

    // ----- Combat profiles (staged quick-swap chip bar) -------------

    // The SHARED staged working list — one per Settings window, edited by the
    // Combat, Health, Spells and Party tabs through it (a combat profile spans them
    // all). Every chip switch, add / remove, name + box edit happens in memory here;
    // Apply commits, Cancel discards. This tab owns the spell / verb / room / weapon boxes + the
    // name; the Health tab owns the Health section of the same working profiles.
    private readonly CombatProfileStagingSession _session;

    // The profile chips (shared with the Health / Spells / Party tabs). Clicking one
    // STAGES a switch through the session — no persistence.
    public CombatProfileChipBar ChipBar { get; }

    // The active profile's name — the textbox below the chips. Staged like the rest.
    [ObservableProperty] private string _activeProfileName = string.Empty;

    // Header shown on every "this group is per combat profile" border, on both the
    // Combat and Health tabs. Tracks the active profile so a chip switch / rename
    // re-labels the borders live.
    public string ActiveProfileLabel =>
        $"Combat profile: {(string.IsNullOrWhiteSpace(ActiveProfileName) ? $"Profile {_session.ActiveIndex + 1}" : ActiveProfileName.Trim())}";

    // The active profile's distinct accent colour — each profile reads as its own,
    // so the per-profile borders + header recolour on a chip switch to match the
    // active chip. Raised alongside ActiveProfileLabel from OnChipsChanged.
    public Avalonia.Media.IBrush ActiveProfileAccentBrush =>
        CombatProfilePalette.SolidBrush(_session.ActiveIndex + 1);
    public Avalonia.Media.IBrush ActiveProfileAccentSoftBrush =>
        CombatProfilePalette.SoftBrush(_session.ActiveIndex + 1);

    partial void OnActiveProfileNameChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveProfileLabel));
        if (_suppressDirty) return;
        _session.SetActiveName(value);
        MarkDirty();
    }

    private void OnChipsChanged()
    {
        OnPropertyChanged(nameof(ActiveProfileLabel));
        OnPropertyChanged(nameof(ActiveProfileAccentBrush));
        OnPropertyChanged(nameof(ActiveProfileAccentSoftBrush));
    }

    // Fold the Combat tab's boxes (spells + verbs + room + weapons + name) into the
    // active working profile — the session's CaptureRequested handler. Mutates in
    // place so the Health tab's Health fold on the same profile isn't clobbered.
    private void CaptureCombatBoxesToActive()
    {
        CombatSpellProfile p = _session.Active;
        p.CaptureCombatFrom(BuildDto());
        p.Name = ActiveProfileName ?? string.Empty;
        p.NormalWeapon     = NullIfBlank(NormalWeapon);
        p.NormalOffHand    = NullIfBlank(NormalOffHand);
        p.AlternateWeapon  = NullIfBlank(AlternateWeapon);
        p.AlternateOffHand = NullIfBlank(AlternateOffHand);
    }

    // Chip switch: load the active profile's PER-PROFILE combat fields into the boxes
    // (spells + verbs + room thresholds + action order), preserving the shared fields
    // (targeting / backstab / display). BuildDto seeds the scratch from the current boxes;
    // ApplyTo overwrites the per-profile fields, so LoadBoxesFrom updates those boxes
    // (incl. the action-order dropdown) while the shared boxes stay as-is.
    private void OnSessionLoadPerProfile()
    {
        _suppressDirty = true;
        CombatSpellProfile p = _session.Active;
        CombatSettings scratch = BuildDto();
        p.ApplyTo(scratch);
        LoadBoxesFrom(scratch);
        LoadWeaponBoxesFromActive();
        ActiveProfileName = p.Name;
        _suppressDirty = false;
        MarkDirty();
    }

    // Profile swap / discard: reload EVERYTHING for the new active profile — the full
    // Settings["Combat"] (shared + per-profile, kept in sync on the active profile)
    // plus weapons + name.
    private void OnSessionReloadAll()
    {
        _suppressDirty = true;
        LoadFromProfile();
        LoadWeaponBoxesFromActive();
        ActiveProfileName = _session.Active.Name;
        _suppressDirty = false;
    }

    // A gear set changed elsewhere (Workshop Update from live / Clear all / a slot edit,
    // `@equip <set> update`). The live combat profile's weapons live in the Default
    // set, so pull them into the staged profile and the boxes — otherwise the boxes
    // show the old weapon and a Save writes it back over the change (report
    // paradigm-20260927-023326). Only for the profile that's live: a different staged
    // chip's weapons aren't in the set.
    private void OnGearSetsEdited()
    {
        if (_profile.Current is not { } profile || profile.Equipment is not { } eq) return;
        if (profile.CombatProfiles?.ActiveId is { } liveId && _session.Active.Id != liveId) return;
        EquipmentWeaponSync.CaptureDefaultWeapons(eq, _session.Active);
        bool was = _suppressDirty;
        _suppressDirty = true;
        LoadWeaponBoxesFromActive();
        _suppressDirty = was;
    }

    private readonly MudPlay.Game.Inventory.EquipmentManager? _equipment;

    private static MudPlay.Game.Inventory.EquipmentManager? TryGetEquipment()
    {
        try { return AppServices.Current.Equipment; }
        catch (NullReferenceException) { return null; }   // design-time: no services
    }

    private void LoadWeaponBoxesFromActive()
    {
        CombatSpellProfile p = _session.Active;
        NormalWeapon     = p.NormalWeapon;
        NormalOffHand    = p.NormalOffHand;
        AlternateWeapon  = p.AlternateWeapon;
        AlternateOffHand = p.AlternateOffHand;
    }

    private void OnSessionCommitted() => OnPropertyChanged(nameof(IsDirty));

    // Convenience for SettingsWindowViewModel, which builds the shared session and
    // passes the same instance to the Combat, Health, Spells and Party section VMs.
    // "Include in combat profile" checkboxes, one per header on this tab.
    public CombatProfileGroupToggle ActionOrderInProfile { get; }
    public CombatProfileGroupToggle WeaponsInProfile { get; }
    public CombatProfileGroupToggle BackstabInProfile { get; }
    public CombatProfileGroupToggle RoomThresholdsInProfile { get; }
    public CombatProfileGroupToggle SpellCombatInProfile { get; }

    public CombatSectionViewModel(CombatProfileStagingSession session)
        : this(AppServices.Current.Profile, session) { }

    public CombatSectionViewModel(ProfileService profile, CombatProfileStagingSession session)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        _profile = profile;
        _session = session;
        _spellbook = AppServices.Current.Spellbook;
        _state = TryGetPlayerState();
        _gameData = TryGetGameData();
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        if (_state is not null) _state.PropertyChanged += OnPlayerStateChanged;
        if (_gameData is not null) _gameData.ActiveSetChanged += OnActiveSetChangedRefreshWeapons;
        _equipment = TryGetEquipment();
        if (_equipment is not null) _equipment.SetsEdited += OnGearSetsEdited;

        // This tab drives the shared session; the Health tab folds its own section in
        // on the same events.
        _session.BuildFullCombat = BuildDto;
        _session.CaptureRequested += CaptureCombatBoxesToActive;
        _session.LoadRequested += OnSessionLoadPerProfile;
        _session.ReloadAllRequested += OnSessionReloadAll;
        _session.ChipsChanged += OnChipsChanged;
        _session.Committed += OnSessionCommitted;
        ChipBar = new CombatProfileChipBar(_session);
        ActionOrderInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.ActionOrder, MarkDirty);
        WeaponsInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.WeaponsAndCommands, MarkDirty);
        BackstabInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.Backstab, MarkDirty);
        RoomThresholdsInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.RoomThresholds, MarkDirty);
        SpellCombatInProfile = new CombatProfileGroupToggle(_session, Models.Profile.CombatProfileGroup.SpellCombat, MarkDirty);

        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            if (_state is not null) _state.PropertyChanged -= OnPlayerStateChanged;
            if (_gameData is not null) _gameData.ActiveSetChanged -= OnActiveSetChangedRefreshWeapons;
            if (_equipment is not null) _equipment.SetsEdited -= OnGearSetsEdited;
            _session.BuildFullCombat = null;
            _session.CaptureRequested -= CaptureCombatBoxesToActive;
            _session.LoadRequested -= OnSessionLoadPerProfile;
            _session.ReloadAllRequested -= OnSessionReloadAll;
            _session.ChipsChanged -= OnChipsChanged;
            _session.Committed -= OnSessionCommitted;
            ChipBar.Dispose();
            ActionOrderInProfile.Dispose();
            WeaponsInProfile.Dispose();
            BackstabInProfile.Dispose();
            RoomThresholdsInProfile.Dispose();
            SpellCombatInProfile.Dispose();
        });

        RefreshWeaponSuggestions();
        _suppressDirty = true;
        LoadFromProfile();                // shared + active per-profile from Settings["Combat"]
        LoadWeaponBoxesFromActive();      // weapons from the session's active profile
        ActiveProfileName = _session.Active.Name;
        OnChipsChanged();
        _suppressDirty = false;
    }

    private static GameDataCache? TryGetGameData()
    {
        try { return AppServices.Current.GameData; }
        catch { return null; }   // design-time
    }

    // Re-derive the weapon-picker suggestion lists from the active game-data set's
    // Items table. Unfiltered by the live character (the Default set the pick lands
    // in still flags an unwearable choice); a null / empty table yields empty lists.
    private void RefreshWeaponSuggestions()
    {
        if (_gameData is null) return;
        ClassEquipProfile anyClass = ItemEquipFilter.ResolveClassProfile(_gameData, null);
        WeaponSuggestions  = EquipmentSlotMap.GetItemsForSlot(_gameData, EquipmentSlot.Weapon, 0, anyClass, null);
        OffHandSuggestions = EquipmentSlotMap.GetItemsForSlot(_gameData, EquipmentSlot.OffHand, 0, anyClass, null);
    }

    private void OnActiveSetChangedRefreshWeapons(string? _) => RefreshWeaponSuggestions();

    // Build the full CombatSettings DTO from the current boxes (spell + non-spell).
    // Shared by Apply and the staged profile-switch capture.
    private CombatSettings BuildDto() => new()
    {
            NormalAttackCommand        = NormalAttackCommand ?? "a",
            AlternateAttackCommand     = AlternateAttackCommand ?? "a",

            ActionOrder = ActionOrder,
            CycleRoundsPhysical = ClampRounds(CycleRoundsPhysical),
            CycleRoundsSpell    = ClampRounds(CycleRoundsSpell),
            CycleStartOnSpell   = CycleStartOnSpell,

            // Weapon fields (NormalWeapon / AlternateWeapon / BackstabWeapon +
            // off-hands) are owned by the Equipment Manager gear sets and
            // overlaid at combat-read time, so this tab no longer writes them.

            DoBackstab                   = DoBackstab,
            SkipBackstabIfMultiAttack    = SkipBackstabIfMultiAttack,
            RunIfBackstabFails           = RunIfBackstabFails,
            HitAndRunTactics   = HitAndRunTactics,
            HitAndRunMaxRuns             = Math.Clamp(HitAndRunMaxRuns, 1, 20),
            ClearHostilesWhenSeenHidden  = ClearHostilesWhenSeenHidden,
            SeenHiddenClearWhileSolo     = SeenHiddenClearWhileSolo,
            SeenHiddenClearWhileInParty  = SeenHiddenClearWhileInParty,
            ClearHostilesWhenSneakFails  = ClearHostilesWhenSneakFails,

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
            KillAllEngaged = KillAllEngaged,
            MaxMonstersInRoom = Math.Clamp(MaxMonstersInRoom, 1, 20),
            RunDistance       = Math.Clamp(RunDistance, 1, 100),
            RunDirection      = GoBackwardsIfRunning ? RunDirection.Backward : RunDirection.Forward,
            BreakBeforeFleeing = BreakBeforeFleeing,

            SpellManaThresholdMode = SpellManaModeAbsolute
                                     ? ThresholdMode.Absolute
                                     : ThresholdMode.Percentage,

            MultiAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(MultiAttackSpellName),
                MinEnemies      = ClampSpell(MultiAttackMinEnemies),
                MaxCastsPerRoom = ClampCasts(MultiAttackMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(MultiAttackMinManaPerCast),
            },
            MultiAttack2Enabled = MultiAttack2Enabled,
            MultiAttack2Spell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(MultiAttack2SpellName),
                MinEnemies      = 0, // shared with slot 1 — never read for this slot
                MaxCastsPerRoom = ClampCasts(MultiAttack2MaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(MultiAttack2MinManaPerCast),
            },
            AreaDebuffSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(AreaDebuffSpellName),
                MinEnemies      = ClampSpell(AreaDebuffMinEnemies),
                MaxCastsPerRoom = ClampCasts(AreaDebuffMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(AreaDebuffMinManaPerCast),
            },
            AreaDebuffFirstRoundOnly = AreaDebuffFirstRoundOnly,
            SingleTargetDebuffSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(SingleTargetDebuffSpellName),
                MinEnemies      = 0, // ignored for single-target
                MaxCastsPerRoom = ClampCasts(SingleTargetDebuffMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(SingleTargetDebuffMinManaPerCast),
            },
            NormalAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(NormalAttackSpellName),
                MinEnemies      = 0,
                MaxCastsPerRoom = ClampCasts(NormalAttackSpellMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(NormalAttackSpellMinManaPerCast),
            },
            AlternateAttackSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(AlternateAttackSpellName),
                MinEnemies      = 0,
                MaxCastsPerRoom = ClampCasts(AlternateAttackSpellMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(AlternateAttackSpellMinManaPerCast),
            },
            DrainSpell = new CombatSpellSlot
            {
                SpellName       = NullIfBlank(DrainSpellName),
                MinEnemies      = 0,
                MaxCastsPerRoom = ClampCasts(DrainSpellMaxCastsPerRoom),
                MinManaPerCast  = ClampSpell(DrainSpellMinManaPerCast),
            },
            DrainHpTrigger    = Math.Clamp(DrainHpTrigger, 0, 100),
            DrainsOverrideAoe = DrainsOverrideAoe,

            ShowCombatRoundTotals = ShowCombatRoundTotals,
            ShowCombatRoundTotalsSelf     = ShowCombatRoundTotalsSelf,
            ShowCombatRoundTotalsParty    = ShowCombatRoundTotalsParty,
            ShowCombatRoundTotalsPlayers  = ShowCombatRoundTotalsPlayers,
            ShowCombatRoundTotalsMonsters = ShowCombatRoundTotalsMonsters,
            ShowCombatRoundTotalsEachMonster  = ShowCombatRoundTotalsEachMonster,
            CapRoundTotalsAtMonsterHp = CapRoundTotalsAtMonsterHp,
    };

    // Commit the staged edits through the shared session, which folds every tab's
    // boxes into the working profiles and persists Settings["Combat"] +
    // Settings["Spells"] + Settings["Health"] + the CombatProfiles blob + the
    // Default-set weapons in one Save. Guarded on the session's dirty flag, so
    // whichever tab's Apply runs first commits and the others no-op. Until this runs, every chip
    // switch / add / remove / box edit was in-memory only; Cancel throws them away.
    public override void Apply() => _session.CommitIfDirty();

    public override void Discard() => _session.DiscardAndReset();

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Spell-slot integer fields share a 0..100,000 range — covers both percentage
    // and absolute mana thresholds + the cast / enemy counts which top out far below.
    private static int ClampSpell(int value) => Math.Clamp(value, 0, 100_000);

    // Per-room cast cap: null (blank) stays "no limit"; a supplied value clamps to
    // the editor's 0..100 range (0 = never cast).
    private static int? ClampCasts(int? value) => value is { } v ? Math.Clamp(v, 0, 100) : null;

    // Round-cycle phase length: 0 (that phase never ends) up to 999 rounds — well
    // past any fight that hasn't already ended some other way.
    private static int ClampRounds(int value) => Math.Clamp(value, 0, 999);

    // The shared session subscribes to ProfileLoaded first (it is constructed before
    // this VM) and drives the box reload via ReloadAllRequested + ChipsChanged; this
    // handler only refreshes the non-staging HasProfile gate + the weapon suggestions
    // for the new character's data.
    private void OnProfileChanged(CharacterProfile _)
    {
        RefreshWeaponSuggestions();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
    }
    private void OnProfileClosedExternally()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnSpellbookChanged()
    {
        OnPropertyChanged(nameof(SpellSuggestions));
        // The learned set (hence each slot's red-outline flag) just changed.
        OnPropertyChanged(nameof(MultiAttackSpellUnlearned));
        OnPropertyChanged(nameof(MultiAttack2SpellUnlearned));
        OnPropertyChanged(nameof(AreaDebuffSpellUnlearned));
        OnPropertyChanged(nameof(SingleTargetDebuffSpellUnlearned));
        OnPropertyChanged(nameof(NormalAttackSpellUnlearned));
        OnPropertyChanged(nameof(AlternateAttackSpellUnlearned));
        OnPropertyChanged(nameof(DrainSpellUnlearned));
        // The resolved energy/targeting behind each debuff slot may have changed too.
        OnPropertyChanged(nameof(AreaDebuffSpellMisSlotWarning));
        OnPropertyChanged(nameof(SingleTargetDebuffSpellMisSlotWarning));
    }

    private void LoadFromProfile() => LoadBoxesFrom(ReadOrDefault());

    // Set every box from a CombatSettings (spell + non-spell). Used to load the
    // persisted settings and to reload the boxes when a staged profile switch
    // swaps in another profile's spells.
    private void LoadBoxesFrom(CombatSettings dto)
    {
        NormalAttackCommand     = dto.NormalAttackCommand ?? "a";
        AlternateAttackCommand  = dto.AlternateAttackCommand ?? "a";

        ActionOrder = dto.ActionOrder;
        CycleRoundsPhysical = dto.CycleRoundsPhysical;
        CycleRoundsSpell    = dto.CycleRoundsSpell;
        CycleStartOnSpell   = dto.CycleStartOnSpell;

        DoBackstab                  = dto.DoBackstab;
        SkipBackstabIfMultiAttack   = dto.SkipBackstabIfMultiAttack;
        RunIfBackstabFails          = dto.RunIfBackstabFails;
        HitAndRunTactics  = dto.HitAndRunTactics;
        HitAndRunMaxRuns            = dto.HitAndRunMaxRuns;
        ClearHostilesWhenSeenHidden = dto.ClearHostilesWhenSeenHidden;
        SeenHiddenClearWhileSolo = dto.SeenHiddenClearWhileSolo;
        SeenHiddenClearWhileInParty = dto.SeenHiddenClearWhileInParty;
        ClearHostilesWhenSneakFails = dto.ClearHostilesWhenSneakFails;

        TargetOrderNormal  = dto.TargetOrder == TargetOrder.Normal;
        TargetOrderReverse = dto.TargetOrder == TargetOrder.Reverse;

        TargetPriority           = dto.TargetPriority;
        TargetPriorityMemberName = dto.TargetPriorityMemberName ?? string.Empty;

        AttackTiming           = dto.AttackTiming;
        AttackAfterPlayerName  = dto.AttackAfterPlayerName ?? string.Empty;
        PoliteMode             = dto.PoliteMode;

        MinMonstersInRoom = dto.MinMonstersInRoom;
        KillAllEngaged     = dto.KillAllEngaged;
        MaxMonstersInRoom = dto.MaxMonstersInRoom;
        RunDistance       = dto.RunDistance;
        GoBackwardsIfRunning = dto.RunDirection == RunDirection.Backward;
        BreakBeforeFleeing   = dto.BreakBeforeFleeing;

        SpellManaModePercentage = dto.SpellManaThresholdMode == ThresholdMode.Percentage;
        SpellManaModeAbsolute   = dto.SpellManaThresholdMode == ThresholdMode.Absolute;

        MultiAttackSpellName       = dto.MultiAttackSpell.SpellName;
        MultiAttackMinEnemies      = dto.MultiAttackSpell.MinEnemies;
        MultiAttackMaxCastsPerRoom = dto.MultiAttackSpell.MaxCastsPerRoom;
        MultiAttackMinManaPerCast  = dto.MultiAttackSpell.MinManaPerCast;

        MultiAttack2Enabled         = dto.MultiAttack2Enabled;
        MultiAttack2SpellName       = dto.MultiAttack2Spell.SpellName;
        MultiAttack2MaxCastsPerRoom = dto.MultiAttack2Spell.MaxCastsPerRoom;
        MultiAttack2MinManaPerCast  = dto.MultiAttack2Spell.MinManaPerCast;

        AreaDebuffSpellName       = dto.AreaDebuffSpell.SpellName;
        AreaDebuffMinEnemies      = dto.AreaDebuffSpell.MinEnemies;
        AreaDebuffMaxCastsPerRoom = dto.AreaDebuffSpell.MaxCastsPerRoom;
        AreaDebuffMinManaPerCast  = dto.AreaDebuffSpell.MinManaPerCast;
        AreaDebuffFirstRoundOnly  = dto.AreaDebuffFirstRoundOnly;

        SingleTargetDebuffSpellName       = dto.SingleTargetDebuffSpell.SpellName;
        SingleTargetDebuffMaxCastsPerRoom = dto.SingleTargetDebuffSpell.MaxCastsPerRoom;
        SingleTargetDebuffMinManaPerCast  = dto.SingleTargetDebuffSpell.MinManaPerCast;

        NormalAttackSpellName             = dto.NormalAttackSpell.SpellName;
        NormalAttackSpellMaxCastsPerRoom  = dto.NormalAttackSpell.MaxCastsPerRoom;
        NormalAttackSpellMinManaPerCast   = dto.NormalAttackSpell.MinManaPerCast;

        AlternateAttackSpellName            = dto.AlternateAttackSpell.SpellName;
        AlternateAttackSpellMaxCastsPerRoom = dto.AlternateAttackSpell.MaxCastsPerRoom;
        AlternateAttackSpellMinManaPerCast  = dto.AlternateAttackSpell.MinManaPerCast;

        DrainSpellName            = dto.DrainSpell.SpellName;
        DrainSpellMaxCastsPerRoom = dto.DrainSpell.MaxCastsPerRoom;
        DrainSpellMinManaPerCast  = dto.DrainSpell.MinManaPerCast;
        DrainHpTrigger            = dto.DrainHpTrigger;
        DrainsOverrideAoe         = dto.DrainsOverrideAoe;

        ShowCombatRoundTotals = dto.ShowCombatRoundTotals;
        ShowCombatRoundTotalsSelf     = dto.ShowsRoundTotalsRow(dto.ShowCombatRoundTotalsSelf);
        ShowCombatRoundTotalsParty    = dto.ShowsRoundTotalsRow(dto.ShowCombatRoundTotalsParty);
        ShowCombatRoundTotalsPlayers  = dto.ShowsRoundTotalsRow(dto.ShowCombatRoundTotalsPlayers);
        ShowCombatRoundTotalsMonsters = dto.ShowsRoundTotalsRow(dto.ShowCombatRoundTotalsMonsters);
        ShowCombatRoundTotalsEachMonster  = dto.ShowCombatRoundTotalsEachMonster;
        CapRoundTotalsAtMonsterHp = dto.CapRoundTotalsAtMonsterHp;
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

    // Route every box edit into the shared session's single dirty flag (a combat
    // profile spans this tab + the Health tab). The session clears it on Commit /
    // Discard; this VM just re-raises IsDirty for its own Save-button gate.
    private void MarkDirty()
    {
        if (_suppressDirty) return;
        _session.MarkDirty();
        OnPropertyChanged(nameof(IsDirty));
    }

    // Attack commands
    partial void OnNormalAttackCommandChanged(string value)      => MarkDirty();
    partial void OnAlternateAttackCommandChanged(string value)   => MarkDirty();

    // Weapon pickers (per-profile)
    partial void OnNormalWeaponChanged(string? value)            => MarkDirty();
    partial void OnNormalOffHandChanged(string? value)           => MarkDirty();
    partial void OnAlternateWeaponChanged(string? value)         => MarkDirty();
    partial void OnAlternateOffHandChanged(string? value)        => MarkDirty();

    // Combat action order
    partial void OnActionOrderChanged(CombatActionOrder value)
    {
        OnPropertyChanged(nameof(CycleFieldsEnabled));
        MarkDirty();
    }
    partial void OnCycleRoundsPhysicalChanged(int value)          => MarkDirty();
    partial void OnCycleRoundsSpellChanged(int value)             => MarkDirty();
    partial void OnCycleStartOnSpellChanged(bool value)           => MarkDirty();

    // BS options
    partial void OnDoBackstabChanged(bool value)                    => MarkDirty();
    partial void OnSkipBackstabIfMultiAttackChanged(bool value)     => MarkDirty();
    partial void OnRunIfBackstabFailsChanged(bool value)            => MarkDirty();
    partial void OnHitAndRunTacticsChanged(bool value)    => MarkDirty();
    partial void OnHitAndRunMaxRunsChanged(int value)               => MarkDirty();
    partial void OnClearHostilesWhenSeenHiddenChanged(bool value)   => MarkDirty();
    partial void OnSeenHiddenClearWhileSoloChanged(bool value)      => MarkDirty();
    partial void OnSeenHiddenClearWhileInPartyChanged(bool value)   => MarkDirty();
    partial void OnClearHostilesWhenSneakFailsChanged(bool value)   => MarkDirty();

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

    // Room-skip
    partial void OnMinMonstersInRoomChanged(int value)           => MarkDirty();
    partial void OnKillAllEngagedChanged(bool value)             => MarkDirty();
    partial void OnMaxMonstersInRoomChanged(int value)           => MarkDirty();
    partial void OnRunDistanceChanged(int value)                 => MarkDirty();
    partial void OnGoBackwardsIfRunningChanged(bool value)       => MarkDirty();
    partial void OnBreakBeforeFleeingChanged(bool value)         => MarkDirty();

    // Spell mana mode
    partial void OnSpellManaModePercentageChanged(bool value)
    {
        if (value) SpellManaModeAbsolute = false;
        OnPropertyChanged(nameof(SpellManaMax));
        if (value) ClampManaToPercent();      // a % can't exceed 100
        RefreshAllManaConverted();            // the % ↔ value labels flip with the mode
        MarkDirty();
    }
    partial void OnSpellManaModeAbsoluteChanged(bool value)
    {
        if (value) SpellManaModePercentage = false;
        OnPropertyChanged(nameof(SpellManaMax));
        RefreshAllManaConverted();
        MarkDirty();
    }

    // Spell slot — multi-attack
    partial void OnMultiAttackSpellNameChanged(string? value)        => MarkDirty();
    partial void OnMultiAttackMinEnemiesChanged(int value)           => MarkDirty();
    partial void OnMultiAttackMaxCastsPerRoomChanged(int? value)     => MarkDirty();
    partial void OnMultiAttackMinManaPerCastChanged(int value)       { OnPropertyChanged(nameof(MultiAttackMinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — multi-attack 2
    partial void OnMultiAttack2EnabledChanged(bool value)             => MarkDirty();
    partial void OnMultiAttack2SpellNameChanged(string? value)        => MarkDirty();
    partial void OnMultiAttack2MaxCastsPerRoomChanged(int? value)     => MarkDirty();
    partial void OnMultiAttack2MinManaPerCastChanged(int value)       { OnPropertyChanged(nameof(MultiAttack2MinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — AOE debuff
    partial void OnAreaDebuffSpellNameChanged(string? value)         => MarkDirty();
    partial void OnAreaDebuffMinEnemiesChanged(int value)            => MarkDirty();
    partial void OnAreaDebuffMaxCastsPerRoomChanged(int? value)      => MarkDirty();
    partial void OnAreaDebuffMinManaPerCastChanged(int value)        { OnPropertyChanged(nameof(AreaDebuffMinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — single-target debuff
    partial void OnSingleTargetDebuffSpellNameChanged(string? value)        => MarkDirty();
    partial void OnSingleTargetDebuffMaxCastsPerRoomChanged(int? value)     => MarkDirty();
    partial void OnSingleTargetDebuffMinManaPerCastChanged(int value)       { OnPropertyChanged(nameof(SingleTargetDebuffMinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — normal attack spell
    partial void OnNormalAttackSpellNameChanged(string? value)          => MarkDirty();
    partial void OnNormalAttackSpellMaxCastsPerRoomChanged(int? value)  => MarkDirty();
    partial void OnNormalAttackSpellMinManaPerCastChanged(int value)    { OnPropertyChanged(nameof(NormalAttackSpellMinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — alternate attack spell
    partial void OnAlternateAttackSpellNameChanged(string? value)          => MarkDirty();
    partial void OnAlternateAttackSpellMaxCastsPerRoomChanged(int? value)  => MarkDirty();
    partial void OnAlternateAttackSpellMinManaPerCastChanged(int value)    { OnPropertyChanged(nameof(AlternateAttackSpellMinManaPerCastConverted)); MarkDirty(); }

    // Spell slot — drain (life-steal)
    partial void OnDrainSpellNameChanged(string? value)          => MarkDirty();
    partial void OnDrainSpellMaxCastsPerRoomChanged(int? value)  => MarkDirty();
    partial void OnDrainSpellMinManaPerCastChanged(int value)    { OnPropertyChanged(nameof(DrainSpellMinManaPerCastConverted)); MarkDirty(); }
    partial void OnDrainHpTriggerChanged(int value)              => MarkDirty();
    partial void OnDrainsOverrideAoeChanged(bool value)          => MarkDirty();

    // Display
    partial void OnShowCombatRoundTotalsChanged(bool value)      => MarkDirty();
    partial void OnShowCombatRoundTotalsSelfChanged(bool value)     => MarkDirty();
    partial void OnShowCombatRoundTotalsPartyChanged(bool value)    => MarkDirty();
    partial void OnShowCombatRoundTotalsPlayersChanged(bool value)  => MarkDirty();
    partial void OnShowCombatRoundTotalsMonstersChanged(bool value) => MarkDirty();
    partial void OnShowCombatRoundTotalsEachMonsterChanged(bool value)  => MarkDirty();
    partial void OnCapRoundTotalsAtMonsterHpChanged(bool value) => MarkDirty();

    // One Target Priority dropdown row — pairs the enum value with its friendly label.
    public sealed record TargetPriorityOption(TargetPriority Value, string Label);

    // One Combat action-order dropdown row — pairs the enum value with its label.
    public sealed record ActionOrderOption(CombatActionOrder Value, string Label);
}
