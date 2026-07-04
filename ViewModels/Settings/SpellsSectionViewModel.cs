using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Spells" tab — self-cast picks per role. Top section orders the between-round
// casting categories (Minor / Major party heal, Minor / Major self heal,
// Curing, Buffing, Debuffing). Middle sections name the heal / regen / cure
// spells. Bottom section holds the bless slots (10 on a Stock realm, 15 on
// ParaMud, sized live from GameDataCache.ActiveRealm) that cover every class's
// stacked-buff playstyle. Persists as the "Spells" entry in
// CharacterProfile.Settings.
//
// This tab wires DTO storage only — CastingDirector (the between-round cast
// engine) subscribes to ProfileService.ProfileLoaded to re-read the DTO.
// Heal-trigger thresholds (HP / MA percentages) live on HealthSettings — this
// tab only owns the spell names.
public sealed partial class SpellsSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Spells";

    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;
    private readonly Game.Spells.SpellbookState _spellbook;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    // Bless picks whose slot index exceeds the current realm's visible count
    // (e.g. a ParaMud profile's slots 11–15 viewed on a 10-slot Stock realm).
    // Held aside so they persist untouched across the narrower realm and
    // re-surface when a wider one is loaded.
    private readonly Dictionary<int, string> _overflowBlessSlots = new();

    public override string Id => "spells";
    public override string Title => "Spells";
    public override bool IsDirty => _dirty;

    public bool HasProfile => _profile.Current is not null;

    // Known-spell suggestions for every spell-picker typeahead on this tab —
    // the current class's learnable list (level gate ignored), ordered by name +
    // distinct by cast-code, from SpellbookState.AvailablePicks. Each box commits
    // the 4-letter SpellPick.Short cast-code (what the game recognises).
    // Refreshes when the spellbook rebuilds (class swap / reroll).
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

    public override Control View => _view ??= new SpellsSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels =>
        _staticSearchLabels.Concat(
            Enumerable.Range(1, SpellsSettings.ParaMudBlessSlotCount)
                      .Select(i => $"Bless {i}"));

    private static readonly string[] _staticSearchLabels =
    {
        "Spells",
        "Spell type priority", "Priority", "Minor party heal", "Major party heal",
        "Minor self heal", "Major self heal", "Curing", "Buffing", "Debuffing",
        "Healing", "Regeneration", "Minor heal", "Major heal",
        "HP Regen", "Mana Regen", "When HP full", "When Mana full",
        "Mana-regen reroll", "Reroll if roll below", "Reroll threshold",
        "Max rerolls", "Reroll cap", "Nature tap", "Mana flux",
        "Other spells", "Cure Holds", "Cure poison", "Cure disease", "Cure blindness",
        "Room light", "Light", "Bless",
        "Ailment handling", "Coordination",
        "Ignore poison", "Ignore blindness", "Ignore confusion", "Ignore disease",
        "Don't announce poison", "Don't announce blindness",
        "Don't announce confusion", "Don't announce disease",
    };

    // ----- Category priority (1-7) ----------------------------------

    // The seven between-round casting categories in fixed key order; the
    // ranking VM reorders them and reports each one's rank.
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

    // Reorderable between-round casting order. Row position is the rank, so the
    // seven categories always form a clean 1..7 permutation.
    public PriorityRankingViewModel Priority { get; }

    // ----- Healing / regen ------------------------------------------

    [ObservableProperty] private string? _minorHealSpell;
    [ObservableProperty] private string? _majorHealSpell;
    [ObservableProperty] private string? _hpRegenSpell;
    [ObservableProperty] private string? _maRegenSpell;
    [ObservableProperty] private string? _whenHpFullSpell;
    [ObservableProperty] private string? _whenMaFullSpell;

    // ----- Mana-regen reroll (Paradigm roll spells) -----------------
    // Empty threshold = rerolling off (the spell just recasts on expiry).

    [ObservableProperty] private int? _manaRegenRerollThreshold;
    [ObservableProperty] private int _manaRegenRerollCap = 3;

    // Plain-language state of the mana-regen reroll for the current MaRegenSpell
    // pick — the level-scaled roll range when it resolves to a Paradigm roll
    // spell (nature tap / mana flux, ability code 145), otherwise why rerolling
    // doesn't apply. Recomputed when the pick or the spellbook (class / level)
    // changes.
    public string ManaRegenRerollHint => BuildManaRegenRerollHint();

    // ----- Cures + utility ------------------------------------------

    [ObservableProperty] private string? _cureHoldsSpell;
    [ObservableProperty] private string? _curePoisonSpell;
    [ObservableProperty] private string? _cureDiseaseSpell;
    [ObservableProperty] private string? _cureBlindnessSpell;
    [ObservableProperty] private string? _roomLightSpell;

    // ----- Bless slots (realm-sized: Stock 10 / ParaMud 15) ---------

    // Self-bless rows for the active realm, in priority order. Rebuilt from the
    // sparse map on load and whenever the game-data set (hence realm) changes.
    // Bound one-to-one to the tab's ItemsControl.
    public ObservableCollection<SelfBlessSlotViewModel> BlessSlots { get; } = new();

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

    public SpellsSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.GameData) { }

    public SpellsSectionViewModel(ProfileService profile, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameData);
        _profile = profile;
        _gameData = gameData;
        _spellbook = AppServices.Current.Spellbook;
        Priority = new PriorityRankingViewModel(MarkDirty);
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        _gameData.ActiveSetChanged += OnRealmChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            _gameData.ActiveSetChanged -= OnRealmChanged;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnSpellbookChanged()
    {
        OnPropertyChanged(nameof(SpellSuggestions));
        // Class / level swap rescales the roll range shown in the reroll hint.
        OnPropertyChanged(nameof(ManaRegenRerollHint));
    }

    // Resolves the current MaRegenSpell pick to its roll range (nature tap /
    // mana flux, code 145) via the shared classifier, or explains why
    // rerolling doesn't apply. Kept off the render path — only rebuilt when the
    // pick or the spellbook changes.
    private string BuildManaRegenRerollHint()
    {
        if (NullIfBlank(MaRegenSpell) is not { } code)
            return "Pick a mana-regen spell above to configure rerolling.";

        if (_spellbook.FindByCastCode(code) is not { } spell)
            return $"'{code}' isn't in this class's spell list — rerolling needs a known roll spell.";

        if (!Game.Spells.ManaRegenReroller.IsRollSpell(spell.Formula))
            return $"{spell.Name.Trim()} isn't a roll spell — it just recasts on expiry, so rerolling doesn't apply.";

        (long min, long max) = Game.Spells.SpellCalculator.AffectMagnitude(spell.Formula, _spellbook.Level);
        string atLevel = _spellbook.Level > 0 ? $" at level {_spellbook.Level}" : string.Empty;
        return $"{spell.Name.Trim()} rolls {min}…{max} to your mana-regen rate{atLevel}. " +
               "Recast until the roll reaches the threshold (Paradigm only).";
    }

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

            ManaRegenRerollThreshold = ManaRegenRerollThreshold,
            ManaRegenRerollCap       = ManaRegenRerollCap,

            CureHoldsSpell     = NullIfBlank(CureHoldsSpell),
            CurePoisonSpell    = NullIfBlank(CurePoisonSpell),
            CureDiseaseSpell   = NullIfBlank(CureDiseaseSpell),
            CureBlindnessSpell = NullIfBlank(CureBlindnessSpell),
            RoomLightSpell     = NullIfBlank(RoomLightSpell),

            BlessSlots = CollectBlessSlots(),

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

        // Push the Ignore<X> gates at the live ailment engine so a toggle taken
        // mid-affliction re-balances the @wait it already placed — otherwise
        // enabling IgnorePoison while poisoned leaves the party paused.
        AppServices.Current.AilmentSync.ReevaluateWaits();

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

        ManaRegenRerollThreshold = dto.ManaRegenRerollThreshold;
        ManaRegenRerollCap       = dto.ManaRegenRerollCap;

        CureHoldsSpell     = dto.CureHoldsSpell;
        CurePoisonSpell    = dto.CurePoisonSpell;
        CureDiseaseSpell   = dto.CureDiseaseSpell;
        CureBlindnessSpell = dto.CureBlindnessSpell;
        RoomLightSpell     = dto.RoomLightSpell;

        RebuildBlessSlots(dto.BlessSlots ?? new Dictionary<int, string>());

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

    // ----- Bless slots ----------------------------------------------

    // Active game-data set changed → the realm may have flipped
    // (Stock ↔ ParaMud), so re-partition the bless rows for the new slot
    // count. Rebuilds from the current full map so in-progress edits (and the
    // dirty flag) survive; only which slots are visible changes.
    private void OnRealmChanged(string? _)
        => Dispatcher.UIThread.Post(() => RebuildBlessSlots(CollectBlessSlots()));

    // Build the visible slot rows for the active realm (Stock 10 / ParaMud 15)
    // from the full sparse map. Picks beyond the visible count are stashed in
    // _overflowBlessSlots so a wider-realm profile round-trips its extra slots
    // rather than losing them. Row construction is self-suppressed, so this
    // never marks the tab dirty.
    private void RebuildBlessSlots(IReadOnlyDictionary<int, string> full)
    {
        int count = SpellsSettings.BlessSlotCountFor(_gameData.ActiveRealm);

        _overflowBlessSlots.Clear();
        foreach (KeyValuePair<int, string> kv in full)
            if (kv.Key > count) _overflowBlessSlots[kv.Key] = kv.Value;

        BlessSlots.Clear();
        for (int i = 1; i <= count; i++)
            BlessSlots.Add(new SelfBlessSlotViewModel(
                i, full.TryGetValue(i, out string? code) ? code : null, MarkDirty));
    }

    // Merge the visible rows with the preserved out-of-range slots into the
    // full sparse map. Visible blanks drop out; overflow keys (always beyond
    // the visible count, so never colliding) stay untouched.
    private Dictionary<int, string> CollectBlessSlots()
    {
        Dictionary<int, string> map = new(_overflowBlessSlots);
        foreach (SelfBlessSlotViewModel slot in BlessSlots)
            if (NullIfBlank(slot.Spell) is { } code) map[slot.Index] = code;
        return map;
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
    partial void OnWhenHpFullSpellChanged(string? value)     => MarkDirty();
    partial void OnWhenMaFullSpellChanged(string? value)     => MarkDirty();

    partial void OnMaRegenSpellChanged(string? value)
    {
        MarkDirty();
        // The reroll hint keys off the mana-regen pick.
        OnPropertyChanged(nameof(ManaRegenRerollHint));
    }

    partial void OnManaRegenRerollThresholdChanged(int? value) => MarkDirty();
    partial void OnManaRegenRerollCapChanged(int value)        => MarkDirty();

    partial void OnCureHoldsSpellChanged(string? value)      => MarkDirty();
    partial void OnCurePoisonSpellChanged(string? value)     => MarkDirty();
    partial void OnCureDiseaseSpellChanged(string? value)    => MarkDirty();
    partial void OnCureBlindnessSpellChanged(string? value)  => MarkDirty();
    partial void OnRoomLightSpellChanged(string? value)      => MarkDirty();

    partial void OnIgnorePoisonChanged(bool value)           => MarkDirty();
    partial void OnIgnoreBlindnessChanged(bool value)        => MarkDirty();
    partial void OnIgnoreConfusionChanged(bool value)        => MarkDirty();
    partial void OnIgnoreDiseasedChanged(bool value)         => MarkDirty();

    partial void OnDoNotAnnouncePoisonChanged(bool value)    => MarkDirty();
    partial void OnDoNotAnnounceBlindnessChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceConfusionChanged(bool value) => MarkDirty();
    partial void OnDoNotAnnounceDiseasedChanged(bool value)  => MarkDirty();
}
