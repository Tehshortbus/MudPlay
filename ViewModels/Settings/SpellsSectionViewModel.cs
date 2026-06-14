using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Spells" tab — self-cast picks per role. Top section orders the
/// between-round casting categories (Minor / Major party heal, Minor /
/// Major self heal, Curing, Buffing, Debuffing). Middle sections name the
/// heal / regen / cure spells. Bottom section holds 10 bless slots that
/// cover every class's stacked-buff playstyle. Persists as the
/// <c>"Spells"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Wires DTO storage only — <c>CastingDirector</c> (the between-round
/// cast engine) lands in a later phase and subscribes to
/// <see cref="ProfileService.ProfileLoaded"/> to re-read the DTO.
/// Heal-trigger thresholds (HP / MA percentages) live on
/// <see cref="HealthSettings"/> — this tab only owns the spell names.
/// </remarks>
public sealed partial class SpellsSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Spells";

    private readonly ProfileService _profile;
    private readonly Game.Spells.SpellbookState _spellbook;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "spells";
    public override string Title => "Spells";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    /// <summary>Known-spell suggestions for every spell-picker typeahead on
    /// this tab — the current class's learnable list (level gate ignored),
    /// ordered by name + distinct by cast-code, from
    /// <see cref="Game.Spells.SpellbookState.AvailablePicks"/>. Each box commits
    /// the 4-letter <see cref="Game.Spells.SpellPick.Short"/> cast-code (what
    /// the game recognises). Refreshes when the spellbook rebuilds
    /// (class swap / reroll).</summary>
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

    public override Control View => _view ??= new SpellsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Spells",
        "Spell type priority", "Priority", "Minor party heal", "Major party heal",
        "Minor self heal", "Major self heal", "Curing", "Buffing", "Debuffing",
        "Healing", "Regeneration", "Minor heal", "Major heal",
        "HP Regen", "Mana Regen", "When HP full", "When Mana full",
        "Other spells", "Cure Holds", "Cure poison", "Cure disease", "Cure blindness",
        "Room light", "Light",
        "Bless", "Bless 1", "Bless 2", "Bless 3", "Bless 4", "Bless 5",
        "Bless 6", "Bless 7", "Bless 8", "Bless 9", "Bless 10",
        "Ailment handling", "Coordination",
        "Ignore poison", "Ignore blindness", "Ignore confusion", "Ignore disease",
        "Don't announce poison", "Don't announce blindness",
        "Don't announce confusion", "Don't announce disease",
    };

    // ----- Category priority (1-7) ----------------------------------

    /// <summary>The seven between-round casting categories in fixed key
    /// order; the ranking VM reorders them and reports each one's rank.</summary>
    private static readonly (string Key, string Label, string? Tip)[] _priorityDefs =
    {
        ("MinorPartyHeal", "Minor party heal (single + party)",
            "Priority slot shared by the Party tab's Minor single-target heal and Minor AOE party heal."),
        ("MajorPartyHeal", "Major party heal (single + party)",
            "Priority slot shared by the Party tab's Major single-target heal and Major AOE party heal."),
        ("MinorSelfHeal", "Minor self heal",
            "Priority slot for this tab's Minor heal pick."),
        ("MajorSelfHeal", "Major self heal",
            "Priority slot for this tab's Major heal pick."),
        ("Curing", "Curing",
            "Priority slot for cure spells."),
        ("Buffing", "Buffing",
            "Priority slot for buff / bless casts."),
        ("Debuffing", "Debuffing",
            "Priority slot for between-round debuffs (CombatSettings' debuff slots)."),
    };

    /// <summary>Reorderable between-round casting order. Row position is the
    /// rank, so the seven categories always form a clean 1..7 permutation.</summary>
    public PriorityRankingViewModel Priority { get; }

    // ----- Healing / regen ------------------------------------------

    [ObservableProperty] private string? _minorHealSpell;
    [ObservableProperty] private string? _majorHealSpell;
    [ObservableProperty] private string? _hpRegenSpell;
    [ObservableProperty] private string? _maRegenSpell;
    [ObservableProperty] private string? _whenHpFullSpell;
    [ObservableProperty] private string? _whenMaFullSpell;

    // ----- Cures + utility ------------------------------------------

    [ObservableProperty] private string? _cureHoldsSpell;
    [ObservableProperty] private string? _curePoisonSpell;
    [ObservableProperty] private string? _cureDiseaseSpell;
    [ObservableProperty] private string? _cureBlindnessSpell;
    [ObservableProperty] private string? _roomLightSpell;

    // ----- Bless (10 slots) -----------------------------------------

    [ObservableProperty] private string? _bless1Spell;
    [ObservableProperty] private string? _bless2Spell;
    [ObservableProperty] private string? _bless3Spell;
    [ObservableProperty] private string? _bless4Spell;
    [ObservableProperty] private string? _bless5Spell;
    [ObservableProperty] private string? _bless6Spell;
    [ObservableProperty] private string? _bless7Spell;
    [ObservableProperty] private string? _bless8Spell;
    [ObservableProperty] private string? _bless9Spell;
    [ObservableProperty] private string? _bless10Spell;

    // ----- Ailment handling / coordination --------------------------
    // The four "Ignore X" gates suppress the @wait sent to the party
    // leader; the four "do not announce" gates suppress the say-channel
    // broadcast. Both default off. Consumed by AilmentSyncEngine.

    [ObservableProperty] private bool _ignorePoison;
    [ObservableProperty] private bool _ignoreBlindness;
    [ObservableProperty] private bool _ignoreConfusion;
    [ObservableProperty] private bool _ignoreDiseased;

    [ObservableProperty] private bool _doNotAnnouncePoison;
    [ObservableProperty] private bool _doNotAnnounceBlindness;
    [ObservableProperty] private bool _doNotAnnounceConfusion;
    [ObservableProperty] private bool _doNotAnnounceDiseased;

    public SpellsSectionViewModel() : this(AppServices.Current.Profile) { }

    public SpellsSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _spellbook = AppServices.Current.Spellbook;
        Priority = new PriorityRankingViewModel(MarkDirty);
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnSpellbookChanged() => OnPropertyChanged(nameof(SpellSuggestions));

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        SpellsSettings dto = new()
        {
            PriorityMinorPartyHeal = Priority.RankOf("MinorPartyHeal"),
            PriorityMajorPartyHeal = Priority.RankOf("MajorPartyHeal"),
            PriorityMinorSelfHeal  = Priority.RankOf("MinorSelfHeal"),
            PriorityMajorSelfHeal  = Priority.RankOf("MajorSelfHeal"),
            PriorityCuring         = Priority.RankOf("Curing"),
            PriorityBuffing        = Priority.RankOf("Buffing"),
            PriorityDebuffing      = Priority.RankOf("Debuffing"),

            MinorHealSpell    = NullIfBlank(MinorHealSpell),
            MajorHealSpell    = NullIfBlank(MajorHealSpell),
            HpRegenSpell      = NullIfBlank(HpRegenSpell),
            MaRegenSpell      = NullIfBlank(MaRegenSpell),
            WhenHpFullSpell   = NullIfBlank(WhenHpFullSpell),
            WhenMaFullSpell   = NullIfBlank(WhenMaFullSpell),

            CureHoldsSpell     = NullIfBlank(CureHoldsSpell),
            CurePoisonSpell    = NullIfBlank(CurePoisonSpell),
            CureDiseaseSpell   = NullIfBlank(CureDiseaseSpell),
            CureBlindnessSpell = NullIfBlank(CureBlindnessSpell),
            RoomLightSpell     = NullIfBlank(RoomLightSpell),

            Bless1Spell  = NullIfBlank(Bless1Spell),
            Bless2Spell  = NullIfBlank(Bless2Spell),
            Bless3Spell  = NullIfBlank(Bless3Spell),
            Bless4Spell  = NullIfBlank(Bless4Spell),
            Bless5Spell  = NullIfBlank(Bless5Spell),
            Bless6Spell  = NullIfBlank(Bless6Spell),
            Bless7Spell  = NullIfBlank(Bless7Spell),
            Bless8Spell  = NullIfBlank(Bless8Spell),
            Bless9Spell  = NullIfBlank(Bless9Spell),
            Bless10Spell = NullIfBlank(Bless10Spell),

            IgnorePoison    = IgnorePoison,
            IgnoreBlindness = IgnoreBlindness,
            IgnoreConfusion = IgnoreConfusion,
            IgnoreDiseased  = IgnoreDiseased,

            DoNotAnnouncePoison    = DoNotAnnouncePoison,
            DoNotAnnounceBlindness = DoNotAnnounceBlindness,
            DoNotAnnounceConfusion = DoNotAnnounceConfusion,
            DoNotAnnounceDiseased  = DoNotAnnounceDiseased,
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
        SpellsSettings dto = ReadOrDefault();

        Priority.Load(_priorityDefs, key => key switch
        {
            "MinorPartyHeal" => dto.PriorityMinorPartyHeal,
            "MajorPartyHeal" => dto.PriorityMajorPartyHeal,
            "MinorSelfHeal"  => dto.PriorityMinorSelfHeal,
            "MajorSelfHeal"  => dto.PriorityMajorSelfHeal,
            "Curing"         => dto.PriorityCuring,
            "Buffing"        => dto.PriorityBuffing,
            "Debuffing"      => dto.PriorityDebuffing,
            _                => 99,
        });

        MinorHealSpell  = dto.MinorHealSpell;
        MajorHealSpell  = dto.MajorHealSpell;
        HpRegenSpell    = dto.HpRegenSpell;
        MaRegenSpell    = dto.MaRegenSpell;
        WhenHpFullSpell = dto.WhenHpFullSpell;
        WhenMaFullSpell = dto.WhenMaFullSpell;

        CureHoldsSpell     = dto.CureHoldsSpell;
        CurePoisonSpell    = dto.CurePoisonSpell;
        CureDiseaseSpell   = dto.CureDiseaseSpell;
        CureBlindnessSpell = dto.CureBlindnessSpell;
        RoomLightSpell     = dto.RoomLightSpell;

        Bless1Spell  = dto.Bless1Spell;
        Bless2Spell  = dto.Bless2Spell;
        Bless3Spell  = dto.Bless3Spell;
        Bless4Spell  = dto.Bless4Spell;
        Bless5Spell  = dto.Bless5Spell;
        Bless6Spell  = dto.Bless6Spell;
        Bless7Spell  = dto.Bless7Spell;
        Bless8Spell  = dto.Bless8Spell;
        Bless9Spell  = dto.Bless9Spell;
        Bless10Spell = dto.Bless10Spell;

        IgnorePoison    = dto.IgnorePoison;
        IgnoreBlindness = dto.IgnoreBlindness;
        IgnoreConfusion = dto.IgnoreConfusion;
        IgnoreDiseased  = dto.IgnoreDiseased;

        DoNotAnnouncePoison    = dto.DoNotAnnouncePoison;
        DoNotAnnounceBlindness = dto.DoNotAnnounceBlindness;
        DoNotAnnounceConfusion = dto.DoNotAnnounceConfusion;
        DoNotAnnounceDiseased  = dto.DoNotAnnounceDiseased;
    }

    private SpellsSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new SpellsSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new SpellsSettings();
        try
        {
            return JsonSerializer.Deserialize<SpellsSettings>(json) ?? new SpellsSettings();
        }
        catch
        {
            return new SpellsSettings();
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

    partial void OnMinorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnMajorHealSpellChanged(string? value)      => MarkDirty();
    partial void OnHpRegenSpellChanged(string? value)        => MarkDirty();
    partial void OnMaRegenSpellChanged(string? value)        => MarkDirty();
    partial void OnWhenHpFullSpellChanged(string? value)     => MarkDirty();
    partial void OnWhenMaFullSpellChanged(string? value)     => MarkDirty();

    partial void OnCureHoldsSpellChanged(string? value)      => MarkDirty();
    partial void OnCurePoisonSpellChanged(string? value)     => MarkDirty();
    partial void OnCureDiseaseSpellChanged(string? value)    => MarkDirty();
    partial void OnCureBlindnessSpellChanged(string? value)  => MarkDirty();
    partial void OnRoomLightSpellChanged(string? value)      => MarkDirty();

    partial void OnBless1SpellChanged(string? value)         => MarkDirty();
    partial void OnBless2SpellChanged(string? value)         => MarkDirty();
    partial void OnBless3SpellChanged(string? value)         => MarkDirty();
    partial void OnBless4SpellChanged(string? value)         => MarkDirty();
    partial void OnBless5SpellChanged(string? value)         => MarkDirty();
    partial void OnBless6SpellChanged(string? value)         => MarkDirty();
    partial void OnBless7SpellChanged(string? value)         => MarkDirty();
    partial void OnBless8SpellChanged(string? value)         => MarkDirty();
    partial void OnBless9SpellChanged(string? value)         => MarkDirty();
    partial void OnBless10SpellChanged(string? value)        => MarkDirty();

    partial void OnIgnorePoisonChanged(bool value)           => MarkDirty();
    partial void OnIgnoreBlindnessChanged(bool value)        => MarkDirty();
    partial void OnIgnoreConfusionChanged(bool value)        => MarkDirty();
    partial void OnIgnoreDiseasedChanged(bool value)         => MarkDirty();

    partial void OnDoNotAnnouncePoisonChanged(bool value)    => MarkDirty();
    partial void OnDoNotAnnounceBlindnessChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceConfusionChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceDiseasedChanged(bool value)  => MarkDirty();
}
