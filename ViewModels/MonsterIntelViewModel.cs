using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels;

// Modeless "Monster Intel" window — a FAST pre-fight check, not a monster
// database browser (that's the Game Data Browser's Monsters tab, which still
// owns the full record: loot, locations, elemental matrix, automation
// overlay editing). A persistent character bar (level, live HP/mana, weapon
// HitMagic, known attack-spell count) sits above a searchable master list
// over MonsterCatalog narrowed to what THIS character can safely fight right
// now: a Hits-You-% band filter (its own attack's chance to land on ME, given
// my live AC/Dodge/wards), the rounds-to-kill cap, and the regen / zero-exp
// hides. A monster with no computable Hits You % (no catalogued physical
// attack — an NPC/caster-only record) is dropped from the list entirely once
// a character is loaded, since it isn't a meaningful "can this thing hurt me"
// entry. The per-monster detail panel keeps only what that decision needs
// beyond the list: Attacks (how dangerous is its swing), the monster's own
// abilities and resistances, Your Matchup (weapon eligibility + ranked spell
// effectiveness + incoming elemental threat), and Your Observations (what's
// actually happened in past fights against it).
public sealed partial class MonsterIntelViewModel : ObservableObject, IDisposable
{
    private readonly GameDataCache _gameData;
    private readonly MonsterCatalog _catalog;
    private readonly SettingsResolver _resolver;
    private readonly PlayerStats? _stats;
    private readonly PlayerState? _playerState;
    private readonly InventoryManager? _inventory;
    private readonly SpellbookState? _spellbook;
    // Read of the character's completed-quest permanent bonuses (via the profile's
    // quest log). Monster Intel is a standalone window, so it can't share the
    // Character Workshop's live QuestBonusState — it re-resolves off the profile,
    // cached so a loot-churn re-capture doesn't re-crawl the quest tree.
    private readonly ProfileService? _profile;
    private IReadOnlyList<QuestBonus> _questBonusCache = System.Array.Empty<QuestBonus>();
    private string _questBonusSig = " ";   // impossible initial sig → first read resolves

    // Live read of the character's configured buff plan (Profile.Current.PartyBuffs)
    // so the projected AC / DR assumes those buffs are up.
    private readonly System.Func<Models.Profile.BuffSettings?>? _buffProvider;
    private readonly ItemMagicIndex? _itemMagic;
    private readonly MonsterObservationTracker? _observations;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly IReadOnlyDictionary<int, int> _spellAttType;
    private readonly bool _hasCharacterContext;
    private readonly List<PlayerAttackSpell> _ownedAttackSpells = new();
    private int _weaponHitMagic;
    // Live player combat totals behind the Hits-You-% band filter /
    // master-list "Hits You %" column — recomputed alongside weapon/spell
    // capabilities in RebuildCharacterCapabilities whenever gear changes.
    // AC is the live un-floored value to the tenth (item AC is stored ×10) — it seeds
    // the editable SimAc, which the Hits-You-% figures read (floored for the to-hit
    // formula, shown fractional). See GAME_MECHANICS "Armour Class".
    private double _playerAcExact;
    private int _playerDodge;
    private int _playerProtEvil;
    private int _playerProtGood;
    // The class's ArmourType (Classes table) — only affects the ParaMUD hit-chance
    // floor: light-armour classes (1..6) floor at 1%, everything else at 2%
    // (CombatCalculator.GetHitMin). Drives both the Hits-You-% figures and whether
    // the filter dropdown gets a ≤1% band.
    private int _playerArmourType;
    // Whether the current Hits-You-% band set includes the ≤1% band — rebuilt only
    // when this flips (a realm or qualifying-class change), so a gear/loot churn
    // doesn't wipe the user's selected bands.
    private bool _hitsBandsInclude1Pct;
    // Shadow (Abil 9) is a flat +10 AC that stacks only once no matter how
    // many worn sources carry it — a boolean gate, not the raw accumulated
    // PlusShadowResist total (see GAME_MECHANICS.md's Armour Class section).
    private bool _playerHasShadow;
    private ElementalResists _playerResists;
    private bool _disposed;
    // Guards the initial RoundsToKillCap load (from the resolver) from
    // immediately writing the same value straight back out via
    // OnRoundsToKillCapChanged.
    private bool _suppressCapPersist;
    // Defense-simulator seeding: the last worn-set signature we seeded from, so a
    // gear swap re-seeds the what-if inputs but backpack/loot churn doesn't; and a
    // latch so seeding the sim fields doesn't fire a recompute per field.
    private string _lastWornSignature = "";
    private bool _suppressSimRecompute;

    // "Edit Attacks" picker state. AttackOptions is the picker's rows (a leading
    // "fastest of all" basis row, then usable melee attacks + obtained attack
    // spells); _hiddenAttackKeys are the attacks hidden from Your Matchup;
    // _roundsAttackKey is what drives the master list's Est. Rounds to Kill — one
    // attack, or BestKey for whichever of your attacks kills each monster fastest.
    // Persisted per character in OtherSettings; _buildingOptions suppresses row-event
    // write-back while the list is (re)built.
    private readonly List<MudAttackType> _usableMelee = new();
    private readonly HashSet<string> _hiddenAttackKeys = new(System.StringComparer.Ordinal);
    // A caster's spells out-damage its melee by orders of magnitude, so defaulting
    // the basis to the Normal swing made the rounds cap hide every monster the
    // character actually kills in a couple of casts.
    private const string BestKey = "best";
    private string _roundsAttackKey = BestKey;
    private bool _buildingOptions;
    public ObservableCollection<AttackPickRow> AttackOptions { get; } = new();

    // "Apply Debuffs" picker state. DebuffOptions is the picker's rows (the
    // character's known stat-affecting enemy debuffs); _ownedDebuffSpells backs
    // them; _appliedDebuffKeys are the cast codes folded onto every monster (the
    // list's columns and the detail alike); _monsterDebuff is their summed effect.
    // Persisted per character.
    private readonly List<KnownSpell> _ownedDebuffSpells = new();
    private readonly HashSet<string> _appliedDebuffKeys = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _buildingDebuffOptions;
    private Game.Spells.MonsterDebuffEffect _monsterDebuff;
    public ObservableCollection<SpellPickRow> DebuffOptions { get; } = new();
    [ObservableProperty] private bool _hasAppliedDebuffs;
    [ObservableProperty] private string _debuffSummary = "";
    // The Apply Debuffs button's text, counting what's applied: the debuffs are saved
    // with the character and move every row, so the list says so without a selection.
    [ObservableProperty] private string _applyDebuffsLabel = "Apply Debuffs…";
    public bool HasKnownDebuffs => DebuffOptions.Count > 0;

    // "Apply Buffs" picker state — the offense mirror of Apply Debuffs: the
    // character's own buffs that raise Stealth / accuracy / backstab / damage
    // (e.g. shadowform), folded into every attack as if up. _isBuffUp reports a
    // buff that's on right now, whose Stealth the live stats already carry.
    private readonly List<KnownSpell> _ownedOffenseBuffs = new();
    private readonly HashSet<string> _appliedBuffKeys = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly System.Func<string, bool>? _isBuffUp;
    private bool _buildingBuffOptions;
    public ObservableCollection<SpellPickRow> BuffOptions { get; } = new();
    [ObservableProperty] private bool _hasAppliedBuffs;
    [ObservableProperty] private string _buffSummary = "";
    public bool HasKnownBuffs => BuffOptions.Count > 0;

    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string? _nameFilter;
    [ObservableProperty] private MonsterIntelEntry? _selectedEntry;

    public string CountText => $"{RowsView.Count} monster{(RowsView.Count == 1 ? "" : "s")}";

    // Monsters that pass every other filter and are dropped ONLY by the rounds-to-kill
    // cap. The cap is otherwise invisible — a low saved value silently shrinks the list
    // to a handful with nothing saying why — so the count is surfaced beside the cap.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapHidden))]
    [NotifyPropertyChangedFor(nameof(CapHiddenText))]
    private int _hiddenByCap;
    public bool HasCapHidden => HiddenByCap > 0;
    public string CapHiddenText => $"{HiddenByCap:N0} more hidden by this cap";

    // Re-derive every label that reflects what the filters currently let through.
    // Call after any change that can re-shape the list.
    private void RaiseCountChanged()
    {
        int hidden = 0;
        if (_hasCharacterContext)
            foreach (MonsterIntelEntry e in _all)
                if (OverRoundsCap(e) && PassesFilter(e, applyRoundsCap: false)) hidden++;
        HiddenByCap = hidden;
        OnPropertyChanged(nameof(CountText));
    }

    // The single-monster detail panel shows once something's selected.
    public bool ShowSingleDetail => HasSelection;

    // The "select a monster" placeholder shows while nothing is.
    public bool ShowPlaceholder => !HasSelection;

    public MonsterIntelViewModel(
        GameDataCache gameData, MonsterCatalog catalog, SettingsResolver resolver,
        PlayerStats? stats = null, InventoryManager? inventory = null,
        SpellbookState? spellbook = null, ItemMagicIndex? itemMagic = null,
        MonsterObservationTracker? observations = null, PlayerState? playerState = null,
        System.Func<Models.Profile.BuffSettings?>? buffProvider = null,
        ProfileService? profile = null, System.Func<string, bool>? isBuffUp = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        _gameData = gameData;
        _catalog = catalog;
        _resolver = resolver;
        _stats = stats;
        _playerState = playerState;
        _inventory = inventory;
        _spellbook = spellbook;
        _itemMagic = itemMagic;
        _observations = observations;
        _buffProvider = buffProvider;
        _profile = profile;
        _isBuffUp = isBuffUp;
        _hasCharacterContext = _stats is not null && _inventory is not null
            && _spellbook is not null && _itemMagic is not null;

        _all = MonsterIntelEntry.BuildCatalog(catalog);
        // Reuse the catalog's spell → AttType map (built off its one-time Spells
        // read) instead of re-reading the table the catalog has already evicted.
        _spellAttType = catalog.SpellAttType;
        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };
        RebuildHitsFilterBuckets();   // realm-dependent Hits-You-% filter bands

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NameFilter) or nameof(HideRegenMonsters) or nameof(HideZeroExpMonsters))
            {
                RowsView.Refresh();
                RaiseCountChanged();
                GuideActions.Report(GuideActions.MonsterFiltered);
            }
            else if (e.PropertyName == nameof(SelectedEntry))
            {
                RebuildDetail();
                UpdateAcVsTarget();
                if (SelectedEntry is not null) GuideActions.Report(GuideActions.MonsterSelected);
            }
        };

        if (_observations is not null) _observations.Changed += OnObservationsChanged;

        if (_hasCharacterContext)
        {
            OtherSettings saved = _resolver.Resolve<OtherSettings>("Other");
            _suppressCapPersist = true;
            RoundsToKillCap = saved.RoundsToKillCap;
            _suppressCapPersist = false;
            foreach (string k in saved.MonsterIntelHiddenAttacks) _hiddenAttackKeys.Add(k);
            if (!string.IsNullOrEmpty(saved.MonsterIntelRoundsAttack))
                _roundsAttackKey = saved.MonsterIntelRoundsAttack!;
            foreach (string k in saved.MonsterIntelAppliedDebuffs) _appliedDebuffKeys.Add(k);
            foreach (string k in saved.MonsterIntelAppliedBuffs) _appliedBuffKeys.Add(k);

            // RowsView was just constructed with Filter = PassesFilter, which
            // reads IncomingHitPercent — still every entry's default -1 until
            // this rebuild runs. Without a Refresh here the view's initial
            // snapshot filters the whole catalog out (the -1 sentinel reads
            // as "no computable Hits You %") and never re-evaluates until the
            // next gear/spell change, so the master list opens empty.
            RebuildCharacterCapabilities();
            RowsView.Refresh();
            RaiseCountChanged();
            _inventory!.Changed += OnCharacterCapabilitiesChanged;
            _spellbook!.Changed += OnCharacterCapabilitiesChanged;
        }

        if (_playerState is not null)
        {
            _playerState.PropertyChanged += OnPlayerStateChanged;
            UpdateManaLabel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_observations is not null) _observations.Changed -= OnObservationsChanged;
        if (_hasCharacterContext)
        {
            _inventory!.Changed -= OnCharacterCapabilitiesChanged;
            _spellbook!.Changed -= OnCharacterCapabilitiesChanged;
        }
        if (_playerState is not null) _playerState.PropertyChanged -= OnPlayerStateChanged;
    }

    // Live-refresh the observation lines while a monster's detail panel is
    // open and a swing/no-effect line arrives for it — so a fight you're
    // watching updates without reselecting the row.
    private void OnObservationsChanged()
    {
        if (SelectedEntry is { } entry) RebuildObservations(entry.Number);
    }

    // ----- character bar (who am I, right now) -----
    // PlayerStats (Name/Level/Class) is stat-command-refreshed identity data;
    // PlayerState (Live) carries the per-prompt live Hp/MaxHp/Ma/MaxMa — bound
    // directly in XAML since both are ObservableObject, so the bar tracks HP
    // ticks and level-ups without any VM-side polling.
    public PlayerStats? Stats => _stats;
    public PlayerState? Live => _playerState;
    public bool HasCharacterContext => _hasCharacterContext;
    [ObservableProperty] private string _manaLabel = "Mana";
    [ObservableProperty] private string? _weaponSummaryText;
    [ObservableProperty] private int _knownAttackSpellCount;

    // ----- Defense simulator (top bar) -----
    // The AC / Prot Evil / Vile Ward / Shadow up top are EDITABLE what-if inputs,
    // not read-only labels: they seed to the character's live worn+buff loadout on
    // open (and whenever the worn set changes), and every edit re-runs each
    // monster's Hits-You-%. AC (worn gear + buffs) and Shadow (+10) apply against
    // every attacker; Prot Evil and Vile Ward are evil-only (they raise defense
    // only versus an evil monster). The Vile-Ward alignment picker is the
    // character's OWN evil tier — it scales how much raw Vile Ward converts to AC
    // (not evil 0% / outlaw-criminal 50% / villain-fiend 100%), matching
    // CombatCalculator's AdjustVileWard.
    // The what-if AC is fractional to the tenth (the game shows AC that way — item AC
    // is stored ×10). Seeded to the live un-floored AC; the spinner steps by 0.1. The
    // hit-% formula floors it (the whole number the game's to-hit consumes), so it's
    // cast to int at each matchup call. See GAME_MECHANICS "Armour Class".
    [ObservableProperty] private double _simAc;
    [ObservableProperty] private int _simProtEvil;
    // Protection from Good (ability 25) — Stock-only. Paradigm dropped it for VileWard
    // (its server ignores ability 25 and its gear carries none), so the ProtGood field
    // is shown only on Stock and VileWard only on Paradigm. See GAME_MECHANICS.
    [ObservableProperty] private int _simProtGood;
    [ObservableProperty] private int _simVileWard;
    [ObservableProperty] private bool _simShadow;
    // Default Villain/Fiend: worn Vile Ward implies an evil character, and with
    // zero Vile Ward the tier is inert anyway (AdjustVileWard returns 0), so a
    // full-benefit default never overstates a non-evil character's defense.
    [ObservableProperty] private int _simVileWardAlignIndex = 2;

    // What-if resists against a monster's spell damage, seeded with the rest of the
    // row from the worn gear + buffs. They feed each spell row's "you take … dmg"
    // line, so a resist set can be tried without wearing it. Signed: a negative
    // resist is a vulnerability (Stock adds damage for it).
    [ObservableProperty] private int _simResistCold;
    [ObservableProperty] private int _simResistFire;
    [ObservableProperty] private int _simResistStone;
    [ObservableProperty] private int _simResistLightning;
    [ObservableProperty] private int _simResistWater;
    // Stock cuts a poison spell's damage by ImmuPoison; Paradigm has no poison cut,
    // so the picker shows only on Stock.
    [ObservableProperty] private int _simResistPoison;

    private ElementalResists SimResists => new(
        SimResistCold, SimResistFire, SimResistStone, SimResistLightning, SimResistWater, SimResistPoison);

    public IReadOnlyList<string> VileWardAlignOptions { get; } =
        new[] { "Not evil (0%)", "Outlaw / Criminal (50%)", "Villain / Fiend (100%)" };

    private EvilLevel SimEvilLevel => SimVileWardAlignIndex switch
    {
        1 => EvilLevel.Criminal,
        2 => EvilLevel.Fiend,
        _ => EvilLevel.Saint,
    };

    // Realm-exclusive defense-sim fields: Paradigm shows Vile Ward (+ the evil-tier
    // picker), Stock shows Prot Good. Only depends on the active realm, which doesn't
    // change while the window is open, so no change-notification is needed.
    public bool ShowVileWard => _gameData.ActiveRealm == RealmType.ParaMud;
    public bool ShowProtGood => !ShowVileWard;
    public bool ShowPoisonResist => !ShowVileWard;

    // The effective AC the selected monster's attack actually rolls against —
    // base AC (worn + buffs) + Shadow (vs all) + the wards that apply to THAT
    // monster's alignment (Prot Evil + converted Vile Ward vs evil, Prot Good vs
    // good). "—" until a monster is selected. This is the same `defense` figure
    // that feeds Hits You %, surfaced as a plain number for the picked target.
    [ObservableProperty] private string _acVsTargetText = "—";

    // Hits-You-% filter: a multi-select dropdown of contiguous %-bands. Selecting
    // none shows every monster (still subject to the "no computable value" drop);
    // selecting any keeps a monster whose Hits You % falls in ANY selected band.
    // The band set is REALM-DEPENDENT — the least a monster's attack gets through
    // armour is 9% on Stock and 2% on ParaMUD (CombatCalculator.GetHitMin), so Stock
    // folds the 2/5 bands into one at its floor (dodge can take a figure lower). Session-only (not
    // persisted), built once from the active realm at construction.
    public ObservableCollection<HitsFilterBucket> HitsFilterBuckets { get; } = new();

    private static readonly int[] ParadigmHitBands =
        { 2, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 100 };
    // Light-armour ParaMUD classes (ArmourType 1..6) floor at 1%, not 2%, so their
    // dropdown gains a ≤1% band ahead of the standard set.
    private static readonly int[] ParadigmHitBands1Pct =
        { 1, 2, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 100 };
    private static readonly int[] StockHitBands =
        { CombatCalculator.STOCK_HIT_MIN, 10, 15, 20, 25, 30, 35, 40, 45, 50, 100 };

    // The ≤1% band exists only on ParaMUD for a light-armour class (ArmourType
    // 1..6) — the exact GetHitMin condition that drops the floor to 1%.
    private bool HitsBandsShouldInclude1Pct() =>
        _gameData.ActiveRealm == RealmType.ParaMud
        && _playerArmourType is > 0 and <= 6;

    // The dropdown button's label — "all" when no band is selected, else the count.
    public string HitsFilterLabel
    {
        get
        {
            int n = 0;
            foreach (HitsFilterBucket b in HitsFilterBuckets) if (b.Selected) n++;
            return n == 0 ? "Hits You %: all" : $"Hits You %: {n} band{(n == 1 ? "" : "s")}";
        }
    }

    // Drop monsters that respawn on their own timer (a non-zero RegenTime — bosses,
    // lair leaders, other timed spawns) so the list shows only freely-farmable
    // monsters. Session-only, like the Hits-You-% boxes it sits beside.
    [ObservableProperty] private bool _hideRegenMonsters;

    // Drop monsters worth no experience (shopkeepers, trainers, quest NPCs and
    // other records with 0 exp) so the list holds only what's worth hunting.
    // Session-only, like the checkbox it sits beside.
    [ObservableProperty] private bool _hideZeroExpMonsters;

    // Ceiling for the master list's "Est. Rounds to Kill" column — edited
    // right here instead of Settings → Other so changing it doesn't mean
    // leaving the window. Loaded once from OtherSettings (Character tier)
    // in the constructor; every edit persists straight back via WriteAt and
    // re-stamps every entry's cap without a full RebuildCharacterCapabilities
    // (only the display ceiling changed, not the underlying projections).
    [ObservableProperty] private int _roundsToKillCap = NoRoundsCap;

    // Completed-quest permanent bonuses for the current character, cached so a
    // loot-churn re-capture doesn't re-walk the quest tree — re-resolved only when the
    // class or the completed-quest set actually changes.
    private IReadOnlyList<QuestBonus> QuestBonusesForCharacter()
    {
        if (_profile?.Current is not { } prof || _stats is null) return _questBonusCache;
        int? classId = CompletedQuestBonuses.ResolveClassId(_gameData, _stats.Class);
        string sig = classId + "|" + string.Join(",",
            (prof.QuestLog ?? Enumerable.Empty<QuestProgress>())
                .Where(p => p.Complete).Select(p => $"{p.Flag}:{p.Step}"));
        if (sig != _questBonusSig)
        {
            _questBonusSig = sig;
            _questBonusCache = CompletedQuestBonuses.Resolve(_gameData, classId, prof.QuestLog);
        }
        return _questBonusCache;
    }

    // Recomputes weapon HitMagic, the owned-attack-spell set, and the live
    // AC/Dodge/ward totals behind the Hits-You-% band filter, the master list's
    // "Hits You %" / "Est. Rounds to Kill" columns, and Your Matchup. Called once
    // at startup and again whenever gear or the spellbook changes, so a
    // mid-session weapon swap or a newly-obtained spell updates the character
    // bar and re-filters the list without reselecting a monster.
    private void RebuildCharacterCapabilities()
    {
        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        string? weaponName = worn.FirstOrDefault(w => w.Slot == "Weapon Hand").Name;
        _weaponHitMagic = string.IsNullOrEmpty(weaponName) ? 0 : _itemMagic!.HitMagic(weaponName);
        WeaponSummaryText = string.IsNullOrEmpty(weaponName)
            ? "No weapon equipped"
            : $"{weaponName} (HitMagic {_weaponHitMagic})";

        _ownedAttackSpells.Clear();
        int spellBonus = CharacterCalculator.SpellDamageBonus(_stats!, worn, _gameData!);
        foreach (KnownSpell known in _spellbook!.Available)
        {
            if (!_spellbook.IsObtained(known.Number)) continue;
            long maxDmg = SpellCalculator.MaxDamage(known.Formula, _stats!.Level,
                spellDamageBonus: spellBonus, realm: _gameData!.ActiveRealm);
            if (maxDmg <= 0) continue;   // not an attack spell
            int attType = _spellAttType.TryGetValue(known.Number, out int at) ? at : -1;
            // Abil 23 / 108 (AbilityNames.cs) — a caster-side target-type gate
            // independent of SpellImmu/resist: an undead-only spell (e.g. a
            // turn-undead-style attack) does nothing to a living monster, and
            // a living-only spell does nothing to an undead one.
            bool undeadOnly = known.Formula.Abilities.Any(a => a.Code == 23);
            bool livingOnly = known.Formula.Abilities.Any(a => a.Code == 108);
            long minDmg = SpellCalculator.MinDamage(known.Formula, _stats!.Level,
                spellDamageBonus: spellBonus, realm: _gameData!.ActiveRealm);
            _ownedAttackSpells.Add(new PlayerAttackSpell(
                known.Name, known.Short, known.ReqLevel, attType,
                maxDmg, SpellCalculator.ManaCost(known.Formula),
                undeadOnly, livingOnly, known.Targets,
                ManaCostPerCast: known.Formula.ManaCost,
                MinDamagePerRound: minDmg,
                UsesMagicResist: SpellDamageCalculator.UsesMagicResist(known.Formula),
                TypeOfResists: known.Formula.TypeOfResists));
        }
        KnownAttackSpellCount = _ownedAttackSpells.Count;

        // Stat-affecting enemy debuffs the character knows — a 0-energy
        // between-round spell aimed at an enemy (single or area) that carries an
        // AC / DR / Dodge / accuracy / slowness ability the matchup sim can fold
        // onto the monsters. Feeds the "Apply Debuffs" picker.
        _ownedDebuffSpells.Clear();
        foreach (KnownSpell known in _spellbook.Available)
        {
            if (!_spellbook.IsObtained(known.Number)) continue;
            if (!Game.Combat.DebuffTargeting.IsBetweenRound(known.Formula.EnergyCost)) continue;
            if (!Game.Combat.DebuffTargeting.IsSingleTargetEnemy(known.Targets)
                && !Game.Combat.DebuffTargeting.IsAreaEnemy(known.Targets)) continue;
            if (!Game.Spells.MonsterDebuffCalculator.AffectsMonsterStats(known)) continue;
            _ownedDebuffSpells.Add(known);
        }
        RebuildDebuffOptions();
        RecomputeDebuff();

        // The character's own offense buffs — feeds the "Apply Buffs" picker.
        _ownedOffenseBuffs.Clear();
        foreach (KnownSpell known in _spellbook.Available)
            if (_spellbook.IsObtained(known.Number) && BuffOffenseCalculator.AffectsOffense(known))
                _ownedOffenseBuffs.Add(known);
        RebuildBuffOptions();
        RecomputeBuffSummary();

        // The player-side defence the Hits-You-% column reads against — worn-gear
        // aggregate + permanent race/class innate + completed-quest folds + the
        // character's configured (assumed-up) AC buffs — assembled by the shared
        // IncomingHitEstimator so the route Details window colours monsters from the
        // SAME AC/Dodge/wards this window shows. (AC rides worn+base+buffs, not the
        // live `stat` ArmourClass, which already folds active buffs — see the helper.)
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        PlayerDefenseProfile def = IncomingHitEstimator.BuildLiveDefense(
            _stats!, worn, encum, _gameData, _buffProvider?.Invoke(),
            _spellbook!.Available, QuestBonusesForCharacter());
        _playerAcExact = def.AcExact;
        _playerDodge = def.Dodge;
        _playerProtEvil = def.ProtEvil;
        _playerProtGood = def.ProtGood;
        _playerHasShadow = def.Shadow;
        _playerResists = def.Resists;
        _playerArmourType = def.ArmourType;
        // A qualifying light-armour ParaMUD class floors incoming hits at 1% (not
        // 2%), so its filter dropdown gains a ≤1% band. Rebuild only when that
        // eligibility flips (class change) — never on a gear/loot recompute, which
        // would clear the user's band selection.
        if (HitsBandsShouldInclude1Pct() != _hitsBandsInclude1Pct)
            RebuildHitsFilterBuckets();

        // Seed the editable defense simulator to the live loadout — but only when
        // the WORN set actually changed (first open + a real gear swap). Backpack
        // and loot churn also fires _inventory.Changed, and re-seeding on that
        // would wipe a what-if the user is mid-edit on. The alignment tier is the
        // player's own and gear-independent, so it stays at its field default.
        string wornSig = string.Join("|", worn.Select(w => $"{w.Slot}={w.Name}"));
        if (wornSig != _lastWornSignature)
        {
            _lastWornSignature = wornSig;
            _suppressSimRecompute = true;
            SimAc = _playerAcExact;              // fractional (worn + buffs); Shadow is its own toggle
            SimProtEvil = _playerProtEvil;
            SimProtGood = _playerProtGood;
            SimVileWard = def.VileWard;
            SimShadow = _playerHasShadow;
            SimResistCold = _playerResists.Cold;
            SimResistFire = _playerResists.Fire;
            SimResistStone = _playerResists.Stone;
            SimResistLightning = _playerResists.Lightning;
            SimResistWater = _playerResists.Water;
            SimResistPoison = _playerResists.Poison;
            _suppressSimRecompute = false;
        }

        // The monster → player direction — the master list's "Hits You %" column
        // and band filter — computed from the (possibly tweaked) simulator.
        RecomputeIncomingHits();
        UpdateAcVsTarget();

        // The character's usable attacks feed the Edit Attacks picker — every
        // melee type they can throw (CharacterCalculator gates by class/race) plus
        // each obtained attack spell built above. Rebuilt on gear/spell change,
        // preserving the user's show/hide + rounds-attack picks.
        _usableMelee.Clear();
        _usableMelee.AddRange(CharacterCalculator.UsableMeleeAttacks(_stats!, _gameData));
        RebuildAttackOptions();

        // Estimated Rounds to Kill — the player-offense direction against each
        // monster's HP/AC/DR, using whichever attack the picker selected (default:
        // whichever of the character's attacks kills fastest). The rounds-to-kill
        // cap then filters the list (see PassesFilter), so a superboss projecting
        // into the millions of rounds simply drops out rather than showing a
        // noise number.
        ComputeRoundsToKill(worn, encum);
    }

    // Fill every entry's Est. Rounds to Kill from the currently-selected basis: a
    // melee pick runs the full Compute() matchup; a spell pick divides the
    // monster's HP by that spell's resist-adjusted per-round damage against it; the
    // "fastest" basis takes whichever of those is quickest for each monster.
    private void ComputeRoundsToKill(IReadOnlyList<EquippedItem> worn, EncumbranceReading encum)
    {
        MudAttackType? roundsMelee = MeleeTypeForKey(_roundsAttackKey);
        PlayerAttackSpell? roundsSpell = roundsMelee is null ? SpellForKey(_roundsAttackKey) : null;
        // No single pick (the default, or a saved pick that's gone stale) → fastest of all.
        bool fastest = roundsMelee is null && roundsSpell is null;

        BuffOffense buff = CurrentSelfBuff();

        // Backstab opens the fight: round 1 is the stab, and the rounds after it are
        // normal attacks ("a") on what it leaves (report paradigm-20260929-183044). A
        // sure one-stab kill is 1; otherwise the stab counts at its average after DR.
        // A monster that sees hidden gets no stab, so it's the normal attack alone.
        if (roundsMelee == MudAttackType.Backstab)
        {
            PlayerMatchupProfile bs = BackstabProfile(worn, encum, buff);
            PlayerMatchupProfile follow = CharacterCalculator.BuildMeleeAttackProfile(
                MudAttackType.Normal, _stats!, worn, encum, _gameData, buff);
            foreach (MonsterIntelEntry entry in _all)
            {
                if (entry.Hp <= 0) { entry.EstimatedRoundsToKill = -1; continue; }
                MonsterMatchupProfile monster = MonsterProfileFor(entry.Source, _monsterDebuff);
                BackstabMatchup stab = EvaluateBackstab(bs, entry.Source, monster);
                if (stab.IsOneStabKill) { entry.EstimatedRoundsToKill = 1; continue; }
                bool stabbed = stab.Verdict != BackstabVerdict.SeesHidden;
                int left = stabbed ? Math.Max(1, monster.Hp - (stab.MinDamage + stab.MaxDamage) / 2) : monster.Hp;
                int after = MonsterMatchupCalculator.Compute(follow, monster with { Hp = left }).RoundsToKill;
                entry.EstimatedRoundsToKill = after > 0 ? after + (stabbed ? 1 : 0) : 0;
            }
            return;
        }

        List<PlayerMatchupProfile> meleeProfiles = new();
        if (fastest)
        {
            // The opener never counts as "how fast can I kill this" (see above).
            foreach (MudAttackType t in _usableMelee)
                if (t != MudAttackType.Backstab)
                    meleeProfiles.Add(CharacterCalculator.BuildMeleeAttackProfile(t, _stats!, worn, encum, _gameData, buff));
        }
        else if (roundsMelee is { } mt)
        {
            meleeProfiles.Add(CharacterCalculator.BuildMeleeAttackProfile(mt, _stats!, worn, encum, _gameData, buff));
        }

        foreach (MonsterIntelEntry entry in _all)
        {
            if (entry.Hp <= 0) { entry.EstimatedRoundsToKill = -1; continue; }
            MonsterCatalogEntry m = entry.Source;
            MonsterMatchupProfile monster = MonsterProfileFor(m, _monsterDebuff);
            int best = 0;   // 0 = none of the candidate attacks can kill it
            foreach (PlayerMatchupProfile mp in meleeProfiles)
                best = FasterRounds(best, MonsterMatchupCalculator.Compute(mp, monster).RoundsToKill);
            if (fastest)
                foreach (PlayerAttackSpell s in _ownedAttackSpells)
                    best = FasterRounds(best, SpellRoundsToKill(s, m));
            else if (roundsSpell is { } picked)
                best = SpellRoundsToKill(picked, m);
            entry.EstimatedRoundsToKill = best;
        }
    }

    // The smaller positive rounds figure; 0 means "can't kill", so it never wins.
    private static int FasterRounds(int current, int candidate)
        => candidate > 0 && (current == 0 || candidate < current) ? candidate : current;

    // The defence every Hits-You-% figure in this window reads: the defense-simulator
    // inputs (AC floored for the to-hit formula, Prot Evil / Prot Good / Vile Ward +
    // its alignment scale / Shadow), with Dodge and the class ArmourType (the ParaMUD
    // light-armour 1% floor) at their live values, since the simulator doesn't expose
    // them. One source for the list, the Incoming threat lines and the Attacks rows,
    // so none of them can leave an input out.
    private PlayerDefenseProfile SimDefense() => new(
        (int)SimAc, _playerDodge, SimProtEvil, SimProtGood, SimShadow, SimVileWard, SimEvilLevel,
        _playerArmourType);

    // Refill every entry's Hits-You-% — weighted across every physical attack (each
    // hit chance blended by its use-chance), not just the majority slot, with the
    // applied debuffs' accuracy cut folded in like the detail panel's. The evil-only
    // wards are applied per row against each monster's own alignment.
    private void RecomputeIncomingHits()
    {
        if (!_hasCharacterContext) return;
        PlayerDefenseProfile def = SimDefense();
        RealmType realm = _gameData.ActiveRealm;
        foreach (MonsterIntelEntry entry in _all)
            entry.IncomingHitChance = IncomingHitEstimator.WeightedHitChance(
                entry.Source, def, realm, _monsterDebuff.AccDelta) ?? -1;
    }

    // A defense-simulator input changed — recompute Hits-You-%, re-filter, and
    // refresh the selected-target AC readout.
    private void OnSimInputChanged()
    {
        if (_suppressSimRecompute || !_hasCharacterContext) return;
        RecomputeIncomingHits();
        RowsView.Refresh();
        RaiseCountChanged();
        UpdateAcVsTarget();
    }

    // Effective AC vs the currently-selected monster: base AC + Shadow (always) +
    // the alignment-applicable wards (Prot Evil + converted Vile Ward vs an evil
    // target, Prot Good vs a good one). Mirrors the `defense` term in
    // CombatCalculator.CalculateHitChance so it matches that monster's Hits You %.
    private void UpdateAcVsTarget()
    {
        if (!_hasCharacterContext || SelectedEntry is not { } sel)
        {
            AcVsTargetText = "—";
            return;
        }
        int align = sel.Source.Align;
        bool isEvil = align is 1 or 2 or 5 or 6;
        bool isGood = align is 0 or 4;
        // SimAc is fractional to the tenth; the wards are whole. Shown to the tenth
        // (the game displays AC that way); the hit-% math floors SimAc separately.
        double ac = SimAc + (SimShadow ? 10 : 0);
        if (isEvil)
        {
            ac += SimProtEvil;
            // Vile Ward only counts on Paradigm — CalculateHitChance gates it there.
            if (_gameData.ActiveRealm == RealmType.ParaMud)
                ac += CombatCalculator.AdjustVileWard(SimVileWard, SimEvilLevel);
        }
        // ProtGood counts only on Stock (Paradigm dropped it for VileWard).
        if (isGood && ShowProtGood) ac += SimProtGood;
        AcVsTargetText = ac.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnSimAcChanged(double value) => OnSimInputChanged();
    partial void OnSimProtEvilChanged(int value) => OnSimInputChanged();
    partial void OnSimProtGoodChanged(int value) => OnSimInputChanged();
    partial void OnSimVileWardChanged(int value) => OnSimInputChanged();
    partial void OnSimShadowChanged(bool value) => OnSimInputChanged();
    partial void OnSimVileWardAlignIndexChanged(int value) => OnSimInputChanged();

    partial void OnSimResistColdChanged(int value) => OnSimResistChanged();
    partial void OnSimResistFireChanged(int value) => OnSimResistChanged();
    partial void OnSimResistStoneChanged(int value) => OnSimResistChanged();
    partial void OnSimResistLightningChanged(int value) => OnSimResistChanged();
    partial void OnSimResistWaterChanged(int value) => OnSimResistChanged();
    partial void OnSimResistPoisonChanged(int value) => OnSimResistChanged();

    // A resist only moves the selected monster's spell rows — no hit chance reads it.
    private void OnSimResistChanged()
    {
        if (_suppressSimRecompute || !_hasCharacterContext) return;
        if (SelectedEntry is { } entry) RebuildAttackRows(entry.Source);
    }

    // The monster's matchup inputs, optionally with an applied debuff folded in.
    // Debuff deltas SUBTRACT from the monster's defense (AC / DR / Dodge) and its
    // to-hit (Accuracy) and are deliberately NOT clamped — a monster pushed below
    // zero accuracy can't land a hit, below-zero AC is trivially hit. Slowness
    // raises the primary attack's energy ×1.5, thinning the
    // swing count MonsterMatchupCalculator derives from energy/attEnergy. With the
    // default (no-op) effect this reproduces the plain monster profile.
    private static MonsterMatchupProfile MonsterProfileFor(
        MonsterCatalogEntry m, Game.Spells.MonsterDebuffEffect debuff = default)
    {
        int attEnergy = m.PrimaryPhysicalEnergy;
        if (debuff.Slowed && attEnergy > 0) attEnergy = (attEnergy * 3) / 2;
        return new(
            ArmourClass: m.ArmourClass - debuff.AcDelta,
            DamageResist: m.DamageResist - (int)System.Math.Round(debuff.DrDelta),
            Hp: m.Hp,
            Dodge: m.Dodge - debuff.DodgeDelta,
            HasPhysicalAttack: m.PhysicalAccuracy is not null,
            AvgAttackDamage: m.PrimaryPhysicalAvgDamage,
            EnergyPerRound: m.Energy,
            PrimaryAttackEnergy: attEnergy);
    }

    // Rounds for one attack spell to drop a monster: HP / its resist-adjusted
    // per-round damage (the same RankAttackSpells the Your Matchup panel uses).
    // 0 when the spell can't land (SpellImmu too high, fully resisted, wrong
    // target type) — the column renders that as "—", like an unkillable weapon.
    private int SpellRoundsToKill(PlayerAttackSpell spell, MonsterCatalogEntry m)
    {
        SpellEffectivenessResult r = MonsterMatchupCalculatorSpells.RankAttackSpells(
            new[] { spell }, m.SpellImmunity, m.ElementalResists, m.Undead,
            monsterMagicResist: m.SpellMagicResist, monsterAntiMagic: m.AntiMagic,
            realm: _gameData!.ActiveRealm)[0];
        if (!r.Eligible || r.EffectiveDamage <= 0) return 0;
        double rounds = System.Math.Ceiling(m.Hp / (double)r.EffectiveDamage);
        return rounds >= int.MaxValue ? int.MaxValue : (int)rounds;
    }

    // ----- Edit Attacks picker -----

    // Stable persistence keys: a melee attack is its enum name, a spell its cast
    // code, each namespaced so the two can never collide.
    private static string MeleeKey(MudAttackType t) => "melee:" + t;
    private static string SpellKey(string shortCode) => "spell:" + shortCode;

    private static string MeleeLabel(MudAttackType t) => t == MudAttackType.Jumpkick ? "Jump Kick" : t.ToString();

    // The selected key back to a usable melee type / owned spell, or null when it
    // names the other kind (so ComputeRoundsToKill can branch) or is stale.
    private MudAttackType? MeleeTypeForKey(string key)
    {
        const string prefix = "melee:";
        if (!key.StartsWith(prefix, System.StringComparison.Ordinal)) return null;
        return System.Enum.TryParse(key[prefix.Length..], out MudAttackType t) && _usableMelee.Contains(t)
            ? t : null;
    }

    private PlayerAttackSpell? SpellForKey(string key)
    {
        const string prefix = "spell:";
        if (!key.StartsWith(prefix, System.StringComparison.Ordinal)) return null;
        string shortCode = key[prefix.Length..];
        foreach (PlayerAttackSpell s in _ownedAttackSpells)
            if (string.Equals(s.Short, shortCode, System.StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    // Rebuild the picker rows from the current usable-melee + owned-spell sets,
    // carrying the user's show/hide + rounds-attack picks across the rebuild. If
    // the saved rounds pick is no longer available (spell unlearned / class swap),
    // fall back to the fastest-of-all basis so the column always has one.
    private void RebuildAttackOptions()
    {
        _buildingOptions = true;
        foreach (AttackPickRow r in AttackOptions) r.PropertyChanged -= OnAttackOptionChanged;
        AttackOptions.Clear();

        AttackOptions.Add(NewOption(BestKey, "Fastest of all my attacks", basisOnly: true));
        foreach (MudAttackType t in _usableMelee)
            AttackOptions.Add(NewOption(MeleeKey(t), MeleeLabel(t)));
        foreach (PlayerAttackSpell s in _ownedAttackSpells)
            AttackOptions.Add(NewOption(SpellKey(s.Short), s.Name));

        if (!AttackOptions.Any(o => o.IsRoundsAttack))
        {
            AttackOptions[0].IsRoundsAttack = true;
            _roundsAttackKey = BestKey;
        }
        _buildingOptions = false;
    }

    private AttackPickRow NewOption(string key, string label, bool basisOnly = false)
    {
        var row = new AttackPickRow(key, label,
            shown: !_hiddenAttackKeys.Contains(key), isRoundsAttack: key == _roundsAttackKey,
            basisOnly: basisOnly);
        row.PropertyChanged += OnAttackOptionChanged;
        return row;
    }

    private void OnAttackOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_buildingOptions || sender is not AttackPickRow row) return;
        if (e.PropertyName == nameof(AttackPickRow.Shown))
        {
            if (row.Shown) _hiddenAttackKeys.Remove(row.Key);
            else _hiddenAttackKeys.Add(row.Key);
            SaveAttackSettings();
            if (SelectedEntry is not null) RebuildDetail();   // re-filter Your Matchup
        }
        // Radio: react only to the newly-checked row, and enforce single-selection
        // in the model ourselves (don't lean on XAML GroupName reaching across the
        // ItemsControl) — uncheck every other row before recomputing.
        else if (e.PropertyName == nameof(AttackPickRow.IsRoundsAttack) && row.IsRoundsAttack)
        {
            _buildingOptions = true;
            foreach (AttackPickRow other in AttackOptions)
                if (!ReferenceEquals(other, row)) other.IsRoundsAttack = false;
            _buildingOptions = false;
            _roundsAttackKey = row.Key;
            SaveAttackSettings();
            RecomputeRoundsColumn();
        }
    }

    // Refill only the rounds column (the radio changed, not gear/spells).
    private void RecomputeRoundsColumn()
    {
        if (!_hasCharacterContext) return;
        ComputeRoundsToKill(_inventory!.Snapshot.EquippedItems, _inventory.Snapshot.Encumbrance);
        RowsView.Refresh();
        RaiseCountChanged();
        if (SelectedEntry is not null) RebuildDetail();
    }

    // Char-tier resolve-then-write, mirroring OnRoundsToKillCapChanged — never a
    // bare new OtherSettings, so this doesn't clobber the tab's other fields.
    private void SaveAttackSettings()
    {
        if (!_hasCharacterContext) return;
        OtherSettings dto = _resolver.Resolve<OtherSettings>("Other");
        dto.MonsterIntelHiddenAttacks = _hiddenAttackKeys.OrderBy(k => k, System.StringComparer.Ordinal).ToList();
        dto.MonsterIntelRoundsAttack = _roundsAttackKey;
        _resolver.WriteAt(SettingsTier.Character, "Other", dto);
    }

    // ----- Apply Debuffs picker -----

    // Rebuild the debuff picker rows from the character's known stat-affecting
    // debuffs, carrying the applied set across the rebuild. Each row summarises
    // what the debuff strips so the user can pick by effect, not just name.
    private void RebuildDebuffOptions()
    {
        _buildingDebuffOptions = true;
        foreach (SpellPickRow r in DebuffOptions) r.PropertyChanged -= OnDebuffOptionChanged;
        DebuffOptions.Clear();
        foreach (KnownSpell s in _ownedDebuffSpells)
        {
            var row = new SpellPickRow(s.Short, s.Name,
                DebuffEffectSummary(s), applied: _appliedDebuffKeys.Contains(s.Short));
            row.PropertyChanged += OnDebuffOptionChanged;
            DebuffOptions.Add(row);
        }
        _buildingDebuffOptions = false;
        OnPropertyChanged(nameof(HasKnownDebuffs));
    }

    private void OnDebuffOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_buildingDebuffOptions || sender is not SpellPickRow row) return;
        if (e.PropertyName != nameof(SpellPickRow.Applied)) return;
        if (row.Applied) _appliedDebuffKeys.Add(row.Key);
        else _appliedDebuffKeys.Remove(row.Key);
        SaveDebuffSettings();
        RecomputeDebuff();
        // A debuff softens every monster, so the list's Hits You % and rounds move
        // with Your Matchup (report paradigm-20261014-121847: the panel moved, the
        // list didn't).
        RecomputeIncomingHits();
        RecomputeRoundsColumn();   // refreshes the list and re-runs the detail
    }

    // Fold the applied debuffs into the single effect the matchup sim subtracts,
    // and refresh the summary the panel shows above the debuffed numbers.
    private void RecomputeDebuff()
    {
        List<KnownSpell> applied = _ownedDebuffSpells
            .Where(s => _appliedDebuffKeys.Contains(s.Short)).ToList();
        _monsterDebuff = Game.Spells.MonsterDebuffCalculator.Fold(applied, _stats?.Level ?? 1);
        HasAppliedDebuffs = applied.Count > 0;
        DebuffSummary = HasAppliedDebuffs ? FormatDebuffSummary(_monsterDebuff) : "";
        ApplyDebuffsLabel = applied.Count > 0 ? $"Apply Debuffs ({applied.Count})…" : "Apply Debuffs…";
    }

    // Persist the applied debuff cast-codes on the character tier, resolve-then-write
    // like the attack picks so it never clobbers the tab's other Other fields.
    private void SaveDebuffSettings()
    {
        if (!_hasCharacterContext) return;
        OtherSettings dto = _resolver.Resolve<OtherSettings>("Other");
        dto.MonsterIntelAppliedDebuffs =
            _appliedDebuffKeys.OrderBy(k => k, System.StringComparer.OrdinalIgnoreCase).ToList();
        _resolver.WriteAt(SettingsTier.Character, "Other", dto);
    }

    // Short "what it strips" label for one debuff spell's picker row.
    private string DebuffEffectSummary(KnownSpell spell)
        => FormatDebuffSummary(Game.Spells.MonsterDebuffCalculator.Fold(new[] { spell }, _stats?.Level ?? 1));

    private static string FormatDebuffSummary(Game.Spells.MonsterDebuffEffect e)
    {
        List<string> parts = new();
        if (e.AcDelta != 0) parts.Add($"-{e.AcDelta} AC");
        if (e.DrDelta != 0) parts.Add($"-{e.DrDelta:0.#} DR");
        if (e.DodgeDelta != 0) parts.Add($"-{e.DodgeDelta} Dodge");
        if (e.AccDelta != 0) parts.Add($"-{e.AccDelta} Accuracy");
        if (e.Slowed) parts.Add("slowed");
        return parts.Count == 0 ? "no stat effect" : string.Join(" · ", parts);
    }

    // ----- Apply Buffs picker -----

    private void RebuildBuffOptions()
    {
        _buildingBuffOptions = true;
        foreach (SpellPickRow r in BuffOptions) r.PropertyChanged -= OnBuffOptionChanged;
        BuffOptions.Clear();
        foreach (KnownSpell s in _ownedOffenseBuffs)
        {
            var row = new SpellPickRow(s.Short, s.Name,
                FormatBuffSummary(
                    BuffOffenseCalculator.Fold(new[] { s }, _stats?.Level ?? 1, lowest: true),
                    BuffOffenseCalculator.Fold(new[] { s }, _stats?.Level ?? 1)),
                applied: _appliedBuffKeys.Contains(s.Short));
            row.PropertyChanged += OnBuffOptionChanged;
            BuffOptions.Add(row);
        }
        _buildingBuffOptions = false;
        OnPropertyChanged(nameof(HasKnownBuffs));
    }

    private void OnBuffOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_buildingBuffOptions || sender is not SpellPickRow row) return;
        if (e.PropertyName != nameof(SpellPickRow.Applied)) return;
        if (row.Applied) _appliedBuffKeys.Add(row.Key);
        else _appliedBuffKeys.Remove(row.Key);
        SaveBuffSettings();
        RecomputeBuffSummary();
        RecomputeRoundsColumn();   // buffs move every attack, so the column too
    }

    private List<KnownSpell> AppliedBuffs()
        => _ownedOffenseBuffs.Where(s => _appliedBuffKeys.Contains(s.Short)).ToList();

    // Folded fresh on each use rather than cached: whether a buff is up right now
    // (so its Stealth is already in the live stats) changes as it's cast and fades.
    //
    // At the bottom of each buff's roll: the game rolls a buff's value as it's cast,
    // and every verdict here ("sure one-stab kill", rounds to kill) has to hold
    // whatever was rolled. Folding the top of the roll called kills sure that a low
    // roll misses (report paradigm-20261002-142319).
    private BuffOffense CurrentSelfBuff()
        => BuffOffenseCalculator.Fold(AppliedBuffs(), _stats?.Level ?? 1, _isBuffUp, lowest: true);

    // The same buffs at the top of their roll, for showing how far a good roll reaches.
    private BuffOffense BestSelfBuff()
        => BuffOffenseCalculator.Fold(AppliedBuffs(), _stats?.Level ?? 1, _isBuffUp);

    private void RecomputeBuffSummary()
    {
        List<KnownSpell> applied = AppliedBuffs();
        HasAppliedBuffs = applied.Count > 0;
        BuffSummary = HasAppliedBuffs ? string.Join(", ", applied.Select(s => s.Name)) : "";
    }

    private void SaveBuffSettings()
    {
        if (!_hasCharacterContext) return;
        OtherSettings dto = _resolver.Resolve<OtherSettings>("Other");
        dto.MonsterIntelAppliedBuffs =
            _appliedBuffKeys.OrderBy(k => k, System.StringComparer.OrdinalIgnoreCase).ToList();
        _resolver.WriteAt(SettingsTier.Character, "Other", dto);
    }

    // low / high: the buff at the bottom and the top of its roll. A value the cast
    // rolls reads as its range ("+5–10"), a flat one as itself.
    private static string FormatBuffSummary(BuffOffense low, BuffOffense high)
    {
        static string Roll(int lo, int hi) => lo == hi ? $"+{hi}" : $"+{lo}–{hi}";
        List<string> parts = new();
        if (high.Stealth != 0) parts.Add($"{Roll(low.Stealth, high.Stealth)} Stealth");
        if (high.Accuracy != 0) parts.Add($"{Roll(low.Accuracy, high.Accuracy)} Accuracy");
        if (high.BsAccuracy != 0) parts.Add($"{Roll(low.BsAccuracy, high.BsAccuracy)} BS accuracy");
        if (high.BsMin != 0 || high.BsMax != 0)
            parts.Add($"{Roll(low.BsMin, high.BsMin)}/{Roll(low.BsMax, high.BsMax)} BS min/max");
        if (high.MaxDamage != 0) parts.Add($"{Roll(low.MaxDamage, high.MaxDamage)} max dmg");
        if (high.Crits != 0) parts.Add($"{Roll(low.Crits, high.Crits)} crits");
        return parts.Count == 0 ? "no offense effect" : string.Join(" · ", parts);
    }

    // ----- Backstab opener -----

    // The worn set as it stands for the opener: the Backstab set's weapon swapped
    // into the weapon hand when one is set (the equipment manager wields it for
    // the stab), so its damage and any bonuses it carries count. setWeapon names
    // that weapon when it differs from the one in hand.
    private IReadOnlyList<EquippedItem> BackstabWorn(IReadOnlyList<EquippedItem> worn, out string? setWeapon)
    {
        setWeapon = _profile?.Current?.Equipment?.BackstabSetWeapon();
        IReadOnlyList<EquippedItem> swapped = EquippedItem.WithWeapon(worn, setWeapon);
        if (ReferenceEquals(swapped, worn)) setWeapon = null;
        return swapped;
    }

    private PlayerMatchupProfile BackstabProfile(IReadOnlyList<EquippedItem> worn, EncumbranceReading encum, BuffOffense buff)
        => CharacterCalculator.BuildMeleeAttackProfile(
            MudAttackType.Backstab, _stats!, BackstabWorn(worn, out _), encum, _gameData, buff);

    private BackstabMatchup EvaluateBackstab(
        PlayerMatchupProfile bs, MonsterCatalogEntry m, MonsterMatchupProfile? target = null)
        => BackstabMatchupCalculator.Evaluate(
            bs, target ?? MonsterProfileFor(m), m.BsDefense, m.SeesHidden);

    // The backstab's Your Matchup line: a one-stab verdict judged on the min stab
    // after DR, with the working (range, DR, to-hit vs backstab defence) beneath.
    // best: the same stab with the applied buffs at the top of their roll, when that
    // differs — shown beside the figures the verdict is judged on.
    private MatchupAttackLine BackstabLine(
        PlayerMatchupProfile bs, MonsterCatalogEntry m, MonsterMatchupProfile target, bool basis,
        PlayerMatchupProfile? best = null)
    {
        BackstabMatchup r = EvaluateBackstab(bs, m, target);
        int hp = target.Hp;
        string text = r.Verdict switch
        {
            BackstabVerdict.SeesHidden => "Backstab: it sees hidden — your sneak won't surprise it",
            BackstabVerdict.NoDamage => $"Backstab: its {r.DamageResist} DR stops the whole stab",
            BackstabVerdict.CantKill => $"Backstab: can't kill it in one stab — max {r.MaxDamage} vs {hp} HP",
            BackstabVerdict.HighRollOnly => $"Backstab: kills only on a high roll — min {r.MinDamage} vs {hp} HP",
            BackstabVerdict.KillIfItLands => $"Backstab: one-stab kill if it lands — {IncomingHitEstimator.FormatPercent(r.HitChance)} to land, not the {r.HitCap}% ceiling",
            _ => $"Backstab: sure one-stab kill — min {r.MinDamage} ≥ {hp} HP · {IncomingHitEstimator.FormatPercent(r.HitChance)} to land",
        };
        if (r.Verdict == BackstabVerdict.SeesHidden) return new MatchupAttackLine(text, null, basis);

        BackstabWorn(_inventory!.Snapshot.EquippedItems, out string? setWeapon);
        string detail = $"{r.MinDamage}–{r.MaxDamage} dmg after {r.DamageResist} DR ({bs.BackstabMin}–{bs.BackstabMax} before) · "
            + $"{IncomingHitEstimator.FormatPercent(r.HitChance)} to land vs backstab defence {m.BsDefense} (a sure kill needs the {r.HitCap}% ceiling)"
            + (setWeapon is null ? "" : $" · with {setWeapon}");
        if (best is { } top && EvaluateBackstab(top, m, target) is var hi
            && (hi.MinDamage != r.MinDamage || hi.MaxDamage != r.MaxDamage))
            detail += $" · your buffs at their lowest roll; {hi.MinDamage}–{hi.MaxDamage} at their highest";
        return new MatchupAttackLine(text, detail, basis);
    }

    // "12 rounds", or "999+ rounds" past the display cap (same ceiling the column uses).
    private string FormatRounds(int rounds)
        => rounds > RoundsToKillCap ? $"{RoundsToKillCap}+ rounds"
                                    : $"{rounds} round{(rounds == 1 ? "" : "s")}";

    private void OnCharacterCapabilitiesChanged()
    {
        RebuildCharacterCapabilities();
        RowsView.Refresh();
        RaiseCountChanged();
        if (SelectedEntry is not null) RebuildDetail();
    }

    // Persists the new cap (Character tier, "Other") and re-filters the list —
    // the cap now DROPS monsters the selected attack can't drop within it,
    // rather than captioning them "<cap>+", so the table shows only fights you
    // can finish in that many rounds. Resolve-then-write (not a bare new
    // OtherSettings) so this doesn't clobber the tab's other fields.
    partial void OnRoundsToKillCapChanged(int value)
    {
        if (_suppressCapPersist || !_hasCharacterContext) return;
        OtherSettings dto = _resolver.Resolve<OtherSettings>("Other");
        dto.RoundsToKillCap = value;
        _resolver.WriteAt(SettingsTier.Character, "Other", dto);
        RowsView.Refresh();
        RaiseCountChanged();
    }

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.ManaType)) UpdateManaLabel();
    }

    private void UpdateManaLabel() => ManaLabel = _playerState?.ManaType == ManaType.Kai ? "Kai" : "Mana";

    // Build the Hits-You-% filter bands for the active realm — contiguous [lo,hi]
    // ranges from the realm's thresholds (Stock's first band runs up to its 9% armour
    // floor and holds anything dodge takes lower). Called once at construction; the realm is fixed
    // for the window's life, like the catalog itself.
    private void RebuildHitsFilterBuckets()
    {
        foreach (HitsFilterBucket b in HitsFilterBuckets) b.PropertyChanged -= OnHitsFilterBucketChanged;
        HitsFilterBuckets.Clear();
        _hitsBandsInclude1Pct = HitsBandsShouldInclude1Pct();
        int[] bands = _gameData.ActiveRealm != RealmType.ParaMud ? StockHitBands
            : _hitsBandsInclude1Pct ? ParadigmHitBands1Pct
            : ParadigmHitBands;
        int lo = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            int hi = bands[i];
            // First band is a ceiling ("≤1%"); a single-value band collapses to that
            // value ("2%"); the rest are ranges ("3–5%").
            string label = i == 0 ? $"≤{hi}%" : lo == hi ? $"{hi}%" : $"{lo}–{hi}%";
            HitsFilterBucket bucket = new(label, lo, hi);
            bucket.PropertyChanged += OnHitsFilterBucketChanged;
            HitsFilterBuckets.Add(bucket);
            lo = hi + 1;
        }
        OnPropertyChanged(nameof(HitsFilterLabel));
    }

    private void OnHitsFilterBucketChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HitsFilterBucket.Selected)) return;
        RowsView.Refresh();
        RaiseCountChanged();
        OnPropertyChanged(nameof(HitsFilterLabel));
    }

    private bool PassesFilter(object o) => o is MonsterIntelEntry e && PassesFilter(e, applyRoundsCap: true);

    // The cap's default, which reads as "no limit".
    private const int NoRoundsCap = 999;

    // Over the rounds cap: needs more rounds than it — or, once the cap is lowered
    // from its no-limit default, can't be killed by the selected attack at all ("—",
    // or no HP to judge by). At the default a can't-kill monster stays listed for its
    // Hits-You-% read; a lowered cap asks "what can I finish in N rounds", which a
    // can't-kill monster isn't (report paradigm-20260928-105509: Backstab at 1 round
    // still listed monsters one stab can't drop).
    private bool OverRoundsCap(MonsterIntelEntry e) =>
        e.EstimatedRoundsToKill > RoundsToKillCap
        || (RoundsToKillCap < NoRoundsCap && e.EstimatedRoundsToKill <= 0);

    // applyRoundsCap: false answers "would this monster show if the cap were off" —
    // the hidden-by-cap count is exactly the monsters that pass with it off and fail
    // with it on.
    private bool PassesFilter(MonsterIntelEntry e, bool applyRoundsCap)
    {
        if (!string.IsNullOrWhiteSpace(NameFilter)
            && !e.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase)) return false;

        // Optionally drop timed/boss respawns — a non-zero per-monster RegenTime.
        if (HideRegenMonsters && e.HasRegenTimer) return false;

        if (HideZeroExpMonsters && e.Exp <= 0) return false;

        // Once a character is loaded, a monster with no computable Hits You %
        // (no catalogued physical attack — an NPC/caster-only record, e.g. a
        // trainer or quest-giver) isn't a meaningful "can this thing hurt me"
        // entry, so it's dropped unconditionally, not just under a checkbox.
        if (_hasCharacterContext && e.IncomingHitPercent < 0) return false;

        // The rounds-to-kill cap FILTERS rather than captioning: a monster the
        // selected attack needs more than the cap to drop is removed (it used to
        // show "<cap>+").
        if (applyRoundsCap && _hasCharacterContext && OverRoundsCap(e)) return false;

        // Hits-You-% bands: selecting none shows every monster; selecting any
        // keeps a monster whose Hits You % falls in ANY selected band. Each band
        // is its OWN discrete range, not "at or under" — selecting the 6-10% band
        // never also surfaces the 2%/5% monsters beneath it.
        bool anySelected = false;
        foreach (HitsFilterBucket b in HitsFilterBuckets) if (b.Selected) { anySelected = true; break; }
        if (anySelected)
        {
            int hp = e.IncomingHitPercent;
            bool inBand = false;
            foreach (HitsFilterBucket b in HitsFilterBuckets)
                if (b.Selected && b.Contains(hp)) { inBand = true; break; }
            if (!inBand) return false;
        }
        return true;
    }

    // ----- detail panel -----
    // Deliberately narrow: this window answers "can I fight this thing right
    // now," not "tell me everything about it" (that's the Game Data Browser's
    // Monsters tab, which owns the full record — loot, locations, casts and the
    // automation overlay). The panel keeps Attacks, the monster's own abilities and
    // resistances, Your Matchup and Your Observations.

    public ObservableCollection<AttackRowViewModel> AttackRows { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleDetail))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _hasSelection;

    // ----- Your Matchup (needs a live character; blank without one) -----
    public ObservableCollection<string> MatchupLines { get; } = new();
    // Per-shown melee attack vs the selected monster (rounds to kill / hit% /
    // dmg-per-hit) — the physical counterpart to the attack-spell lists below,
    // both gated by the Edit Attacks picker's show/hide checkboxes.
    public ObservableCollection<MatchupAttackLine> MatchupMeleeLines { get; } = new();
    // Your attack spells vs the selected monster, split by target scope for the
    // panel's grouping: single-target (enemy scopes 4/8) and AOE (9/12). Both are
    // ranked by effective damage; hidden picks are filtered out.
    public ObservableCollection<MatchupSpellRow> AttackSpellsSingleTarget { get; } = new();
    public ObservableCollection<MatchupSpellRow> AttackSpellsAoe { get; } = new();
    public bool HasSingleTargetSpells => AttackSpellsSingleTarget.Count > 0;
    public bool HasAoeSpells => AttackSpellsAoe.Count > 0;
    public bool HasAnyAttackSpells => HasSingleTargetSpells || HasAoeSpells;
    public ObservableCollection<string> IncomingThreatLines { get; } = new();
    [ObservableProperty] private bool _hasMatchupContext;

    // ----- Abilities & resistances (the monster's OWN properties — elemental
    // weakness/strength, spell immunity, magic-weapon requirement, undead/non-living,
    // and other notable abilities; character-independent, always shown when the record
    // carries any) -----
    public ObservableCollection<string> AbilityLines { get; } = new();
    [ObservableProperty] private bool _hasAbilities;

    // ----- Your Observations (actual combat outcomes this character has seen
    // against the selected monster; blank until at least one has been recorded) -----
    public ObservableCollection<string> ObservationLines { get; } = new();
    [ObservableProperty] private bool _hasObservations;

    private void RebuildDetail()
    {
        AttackRows.Clear();
        MatchupLines.Clear();
        MatchupMeleeLines.Clear();
        AttackSpellsSingleTarget.Clear();
        AttackSpellsAoe.Clear();
        IncomingThreatLines.Clear();
        AbilityLines.Clear();
        ObservationLines.Clear();
        HasObservations = false;
        HasAbilities = false;
        HasSelection = SelectedEntry is not null;
        if (SelectedEntry is not { } entry) return;
        MonsterCatalogEntry m = entry.Source;

        RebuildAttackRows(m);
        RebuildAbilities(m);
        RebuildYourMatchup(m);
        RebuildObservations(m.Number);
    }

    private void RebuildAttackRows(MonsterCatalogEntry m)
    {
        AttackRows.Clear();
        foreach (MonsterAttackSlot a in m.Attacks)
        {
            double? hit = PerAttackHitYou(a, m);
            AttackRows.Add(BuildAttackRow(a, hit, SpellResistNote(a.SpellMagicRes, a.SpellDmgMin, a.SpellDmgMax)));
        }
        foreach (MonsterMidSpellSlot mid in m.MidSpells)
            AttackRows.Add(new AttackRowViewModel(
                $"({mid.Percent}%) Between-rounds spell", $"Spell #{mid.SpellId}"
                + (mid.Level > 0 ? $" lvl {mid.Level}" : string.Empty),
                mid.DmgMax > 0 ? $"{FormatDamageRange(mid.DmgMin, mid.DmgMax)} dmg" : string.Empty,
                string.Empty, SpellResistNote(mid.SpellMagicRes, mid.DmgMin, mid.DmgMax)));
    }

    // Renders MonsterObservationTracker's per-monster record, if any, as plain
    // display lines — deliberately its own group in the detail panel, never
    // merged into Overview/Attacks, so it stays visibly "what I've actually
    // seen" rather than "what the MDB says." Blank when no tracker was wired
    // in or nothing's been observed against this monster yet.
    private void RebuildObservations(int monsterNumber)
    {
        if (_observations?.For(monsterNumber) is not { } o) return;
        HasObservations = true;

        if (o.HitCount > 0)
            ObservationLines.Add(
                $"Landed hits: {o.HitCount}, {o.HitDamageMin}-{o.HitDamageMax} dmg (avg {o.AvgHitDamage:0.#})");
        if (o.SwingCount > 0)
            ObservationLines.Add($"Weapon hit rate: {o.HitRatePercent:0.#}% ({o.HitCount}/{o.SwingCount} swings)");
        if (o.PhysicalNoEffectCount > 0)
            ObservationLines.Add(
                $"Physical attacks had no effect {o.PhysicalNoEffectCount}x — your weapon/fists aren't magical enough for this monster");
        if (o.SpellNoEffectCount > 0)
            ObservationLines.Add(
                $"Spells had no effect {o.SpellNoEffectCount}x — blocked by this monster's spell immunity");
        ObservationLines.Add(
            $"First observed {o.FirstObservedAt:g}, last {o.LastObservedAt:g}");
    }

    [RelayCommand]
    private void ClearObservations() => _observations?.Clear();

    // The five elemental resist ability codes, in a stable display order
    // (Fire, Cold, Lightning, Stone, Water).
    private static readonly int[] ElementResistCodes = { 5, 3, 66, 65, 147 };

    // Ability codes already rendered as their own prose line above the generic
    // "Other" summary, so they aren't listed twice: the five elemental resists,
    // Magical (28), SpellImmu (139), NonLiving (109), plus M.R. (36) and Dodge (34)
    // which surface in the master list / defense simulator.
    private static readonly HashSet<int> AbilitiesShownAsProse =
        new() { 3, 5, 65, 66, 147, 28, 139, 109, 36, 34 };

    // The monster's OWN properties (character-independent): life state, the
    // physical/magical defences that gate whether you can even hurt it, its elemental
    // weakness/strength profile, and any other notable abilities. Wording mirrors the
    // Game Data Browser's Monsters tab so the two stay consistent.
    private void RebuildAbilities(MonsterCatalogEntry m)
    {
        if (m.Undead) AbilityLines.Add("Undead");
        if (m.NonLiving) AbilityLines.Add("Non-living — immune to life-drain");

        if (m.Magical > 0)
            AbilityLines.Add($"Magic weapon required — your weapon's HitMagic must be ≥ {m.Magical} to hit it");
        if (m.SpellImmunity > 0)
            AbilityLines.Add($"Spell-immune below level {m.SpellImmunity}");
        if (m.MagicRes != 0)
            AbilityLines.Add($"Magic resist {m.MagicRes} — cuts spell damage once above 50");
        if (m.DamageResist != 0)
            AbilityLines.Add($"Damage resist {m.DamageResist} — flat reduction to the physical damage it takes");

        // Elemental resists → weakness / strength. Negative = vulnerable (takes extra
        // damage) = weak vs; positive = resists / at 100 immune / over 100 healed by
        // that element = strong vs. The signed % already conveys the magnitude.
        foreach (int code in ElementResistCodes)
        {
            if (!m.ElementalResists.TryGetValue(code, out int pct) || pct == 0) continue;
            string name = ElementalResistIndex.NameForCode(code) ?? $"element {code}";
            string tag = pct < 0 ? "weak vs" : "strong vs";
            AbilityLines.Add($"{name} {pct:+0;-0}% ({tag})");
        }

        // Everything else the record carries, with the app's own ability labels
        // (see-hidden, fear, confusion, poison, drain-life, …), skipping the codes
        // already shown as prose above so nothing lists twice.
        string other = AbilityNames.SummarizeAbilities(
            m.Abilities.Select(x => (x.Code, x.Value)), AbilitiesShownAsProse);
        if (!string.IsNullOrEmpty(other))
            AbilityLines.Add($"Other: {other}");

        HasAbilities = AbilityLines.Count > 0;
    }

    // Live-character matchup preview. Deliberately does NOT
    // reproduce the Calculators tab's melee hit%/DPS engine (weapon swing
    // counts, crit chance, realm-aware accuracy) — that's real, intricate,
    // already-correct logic with its own tested home; duplicating it here
    // would risk drifting out of sync. This panel's job is the genuinely NEW
    // half nothing else does: is my weapon even magical enough to hit this
    // thing, which of my known spells actually gets through it, and what
    // elements is it going to hit ME with given my own resists. Blank
    // (HasMatchupContext = false) when no live character context was wired in
    // (e.g. no profile loaded).
    private void RebuildYourMatchup(MonsterCatalogEntry m)
    {
        HasMatchupContext = _hasCharacterContext;
        if (!HasMatchupContext) return;

        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        bool canHitPhysically = MonsterMatchupCalculatorSpells.WeaponMeetsMagical(_weaponHitMagic, m.Magical);
        MatchupLines.Add(m.Magical > 0
            ? $"Weapon HitMagic {_weaponHitMagic} vs required {m.Magical}: "
              + (canHitPhysically ? "you can hit it physically" : "your weapon is NOT magical enough to hit it")
            : $"Weapon HitMagic {_weaponHitMagic}: no magical requirement, any weapon hits");

        EquipmentStatBreakdown playerGear = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        IReadOnlyDictionary<int, int> playerResists = new Dictionary<int, int>
        {
            [3] = playerGear.Totals.PlusColdResist,
            [5] = playerGear.Totals.PlusFireResist,
            [65] = playerGear.Totals.PlusStoneResist,
            [66] = playerGear.Totals.PlusLightningResist,
            [147] = playerGear.Totals.PlusWaterResist,
        };

        foreach (string element in m.CastsElements)
        {
            int code = ElementalResistIndex.CodeForName(element);
            int myResist = code >= 0 && playerResists.TryGetValue(code, out int pct) ? pct : 0;
            IncomingThreatLines.Add(myResist == 0
                ? $"{element}: you have no resistance from your gear"
                : $"{element}: your gear resists {myResist:+0;-0}%");
        }

        // Any applied debuffs soften THIS monster in the physical/defense math
        // below (its spell immunity/resist is unaffected, so the ranked spells
        // don't change). Computed once and reused for the threat line and melee.
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        MonsterMatchupProfile debuffed = MonsterProfileFor(m, _monsterDebuff);

        // The monster's melee threat: how often + hard it hits YOU and at how many
        // swings/round — thinned by a slowness debuff, weakened by an accuracy
        // debuff. The swing model (energy/attEnergy) is new info this panel didn't
        // surface before, and the clearest place applied debuffs show their effect.
        MonsterMatchupResult threat = MonsterMatchupCalculator.Compute(
            CharacterCalculator.BuildMeleeAttackProfile(MudAttackType.Normal, _stats!, worn, encum, _gameData),
            debuffed);
        if (threat.MonsterHasPhysicalAttack
            && IncomingHitEstimator.Breakdown(m, SimDefense(), _gameData.ActiveRealm, _monsterDebuff.AccDelta)
                is { } incoming)
        {
            // The overall hit chance is the master list's "Hits You %" (the same
            // blend across all the physical attacks, the same debuff), so the two
            // agree. dmg/hit + swings stay the primary-slot figures; dmg/round rides
            // the blended chance. Each physical attack follows on its own line.
            double dps = incoming.Overall / 100.0 * threat.MonsterDamagePerHit * threat.MonsterSwingsPerRound;
            List<string> lines = new()
            {
                $"Melee: {IncomingHitEstimator.FormatPercent(incoming.Overall)} to hit you"
                + (incoming.Attacks.Count > 1 ? " over its attacks" : string.Empty)
                + $" · {threat.MonsterDamagePerHit} dmg/hit · "
                + $"{threat.MonsterSwingsPerRound} attack{(threat.MonsterSwingsPerRound == 1 ? "" : "s")}/round · ~{dps:0}/round"
                + (HasAppliedDebuffs ? "  (debuffed)" : string.Empty),
            };
            if (incoming.Attacks.Count > 1)
                foreach (IncomingAttackHit a in incoming.Attacks)
                    lines.Add(IncomingAttackLine(a));
            for (int i = 0; i < lines.Count; i++) IncomingThreatLines.Insert(i, lines[i]);
        }

        // Melee attacks the picker keeps shown — each projected against THIS
        // monster (rounds to kill, hit%, dmg/hit), folding in any applied debuff
        // so the numbers reflect the softened target.
        BuffOffense buff = CurrentSelfBuff();
        BuffOffense bestBuff = BestSelfBuff();
        bool rolled = bestBuff != buff;
        foreach (MudAttackType mt in _usableMelee)
        {
            if (_hiddenAttackKeys.Contains(MeleeKey(mt))) continue;
            bool basis = MeleeKey(mt) == _roundsAttackKey;
            if (mt == MudAttackType.Backstab)
            {
                MatchupMeleeLines.Add(BackstabLine(BackstabProfile(worn, encum, buff), m, debuffed, basis,
                    rolled ? BackstabProfile(worn, encum, bestBuff) : null));
                continue;
            }
            MonsterMatchupResult res = MonsterMatchupCalculator.Compute(
                CharacterCalculator.BuildMeleeAttackProfile(mt, _stats!, worn, encum, _gameData, buff),
                debuffed);
            string? reach = null;
            if (rolled && res.HasWeapon && res.RoundsToKill > 0)
            {
                MonsterMatchupResult top = MonsterMatchupCalculator.Compute(
                    CharacterCalculator.BuildMeleeAttackProfile(mt, _stats!, worn, encum, _gameData, bestBuff),
                    debuffed);
                if (top.PlayerDps != res.PlayerDps || top.RoundsToKill != res.RoundsToKill)
                    reach = $"Your buffs at their lowest roll; at their highest, {FormatRounds(top.RoundsToKill)} · ~{top.PlayerDps:0}/round";
            }
            MatchupMeleeLines.Add(new MatchupAttackLine(res.HasWeapon && res.RoundsToKill > 0
                ? $"{MeleeLabel(mt)}: {FormatRounds(res.RoundsToKill)} to kill · ~{res.PlayerDps:0}/round · {IncomingHitEstimator.FormatPercent(res.PlayerHitChance)} hit · {res.PlayerDamagePerHit} dmg/hit · {res.PlayerSwingsPerRound:0.0} swings"
                : $"{MeleeLabel(mt)}: can't out-damage it", reach, basis));
        }

        // Attack spells, ranked by mana efficiency (damage per mana), split
        // single-target vs AOE — hidden picks filtered out. Passing the monster's
        // HP lets each row carry rounds-to-kill + total mana-to-kill. (Stat debuffs
        // don't touch spell resist, so this side is unaffected by applied debuffs.)
        foreach (SpellEffectivenessResult r in MonsterMatchupCalculatorSpells.RankAttackSpells(
            _ownedAttackSpells, m.SpellImmunity, m.ElementalResists, m.Undead, m.Hp,
            m.SpellMagicResist, m.AntiMagic, _gameData!.ActiveRealm))
        {
            if (_hiddenAttackKeys.Contains(SpellKey(r.Short))) continue;
            MatchupSpellRow row = new(r, SpellKey(r.Short) == _roundsAttackKey);
            if (r.IsAoe) AttackSpellsAoe.Add(row);
            else AttackSpellsSingleTarget.Add(row);
        }
        OnPropertyChanged(nameof(HasSingleTargetSpells));
        OnPropertyChanged(nameof(HasAoeSpells));
        OnPropertyChanged(nameof(HasAnyAttackSpells));
    }

    // One physical attack under the Melee line: how often the monster throws it,
    // its chance to land on you, its accuracy (after an applied accuracy debuff)
    // and its damage range.
    private string IncomingAttackLine(IncomingAttackHit a)
    {
        string name = string.IsNullOrEmpty(a.Attack.Name) ? (a.Attack.Type == 3 ? "Rob" : "Attack") : a.Attack.Name;
        int acc = a.Attack.Accuracy - _monsterDebuff.AccDelta;
        string accText = _monsterDebuff.AccDelta != 0 ? $"acc {a.Attack.Accuracy}→{acc}" : $"acc {acc}";
        return string.Create(CultureInfo.InvariantCulture,
            $"   – {name} ({a.Weight:0.#}% of its attacks): {IncomingHitEstimator.FormatPercent(a.HitChance)} to hit you · {accText} · {FormatDamageRange(a.Attack.MinDamage, a.Attack.MaxDamage)} dmg");
    }

    // This one attack's own chance to land on the player, given the current
    // defense sim (and any applied accuracy debuff). Null without a character or
    // for a non-physical slot — those have no AC-based to-hit.
    private double? PerAttackHitYou(MonsterAttackSlot a, MonsterCatalogEntry m)
    {
        if (!_hasCharacterContext || (a.Type != 1 && a.Type != 3)) return null;
        return IncomingHitEstimator.AttackHitChance(
            a, m.Align, SimDefense(), _gameData.ActiveRealm, _monsterDebuff.AccDelta);
    }

    // "Majority" resolves a spell-attack slot's Accuracy field back to a spell
    // number for display (same field-reuse MonsterMdbInfoBuilder decodes). hitYou
    // is this physical attack's own chance to land on the player (null for spell
    // slots / no character), appended to the damage line so each attack shows its
    // individual to-hit next to its accuracy.
    private static AttackRowViewModel BuildAttackRow(MonsterAttackSlot a, double? hitYou, string resistNote)
    {
        string header = string.IsNullOrEmpty(a.Name) ? "Attack" : a.Name;
        string chance = $"({(a.TruePercent > 0 ? (int)Math.Round(a.TruePercent) : a.Percent)}%) {header}";
        if (a.Type == 2)
        {
            // A spell attack casts once when it lands, so lead its detail with the
            // resolved single-cast damage (the linked spell scaled to this
            // monster's cast level), then its cast-success chance — mirroring how a
            // physical slot leads with its damage range.
            string spellDetail = a.SpellDmgMax > 0
                ? $"{FormatDamageRange(a.SpellDmgMin, a.SpellDmgMax)} dmg · Success {a.MinDamage}%"
                : $"Success {a.MinDamage}%";
            return new AttackRowViewModel(chance, $"Spell #{a.Accuracy} lvl {a.MaxDamage}",
                spellDetail, a.Energy > 0 ? $"{a.Energy} energy" : string.Empty, resistNote);
        }
        string kind = a.Type == 3 ? "Rob" : "Physical";
        string detail = $"{a.MinDamage}-{a.MaxDamage} dmg, acc {a.Accuracy}"
            + (hitYou is { } h ? $" → {IncomingHitEstimator.FormatPercent(h)} to hit you" : string.Empty);
        return new AttackRowViewModel(chance, kind, detail,
            a.Energy > 0 ? $"{a.Energy} energy" : string.Empty);
    }

    // What the character's resists do to one of the monster's spells: the damage
    // range after the magic-resist and elemental cuts, the chance to resist it
    // outright, and "ignores MR" when magic resist does neither (GAME_MECHANICS
    // "Magic Resist (M.R.) and `TypeOfResists`"). Without a character it only says
    // which of the magic-resist effects the spell is open to.
    private string SpellResistNote(MonsterSpellMagicRes? spell, int dmgMin, int dmgMax)
    {
        if (spell is not { } sp) return string.Empty;
        int mr = _stats?.MagicRes ?? 0;
        if (!_hasCharacterContext || mr <= 0)
        {
            if (sp.CutsDamage) return sp.TypeOfResists == 2 ? "cut by MR, can be resisted" : "cut by MR";
            return sp.TypeOfResists == 2 ? "can be resisted" : "ignores MR";
        }

        RealmType realm = _gameData!.ActiveRealm;
        bool antimagic = ItemEquipFilter.ResolveClassProfile(_gameData, _stats!.Class).AntiMagic;
        int elemental = SpellDamageCalculator.PlayerElementalResist(SimResists, sp.Element, realm);
        double mrChange = sp.CutsDamage
            ? SpellDamageCalculator.PlayerMagicResistDamagePercent(mr, antimagic, realm) : 0;
        List<string> parts = new();
        if (dmgMax > 0 && (mrChange != 0 || elemental != 0))
        {
            long lo = SpellDamageCalculator.AfterTargetResists(dmgMin, sp.CutsDamage, mr, antimagic, elemental, realm);
            long hi = SpellDamageCalculator.AfterTargetResists(dmgMax, sp.CutsDamage, mr, antimagic, elemental, realm);
            List<string> why = new();
            if (mrChange != 0)
                why.Add(string.Create(CultureInfo.InvariantCulture,
                    $"MR {mr} {(mrChange < 0 ? "−" : "+")}{Math.Abs(mrChange):0.#}%"));
            if (elemental != 0)
                why.Add($"{sp.Element.ToString().ToLowerInvariant()} resist {elemental}%");
            string taken = hi <= 0 ? "no dmg" : $"{FormatDamageRange((int)Math.Max(0, lo), (int)hi)} dmg";
            parts.Add($"you take {taken} ({string.Join(", ", why)})");
        }
        else if (sp.CutsDamage)
        {
            parts.Add(dmgMax > 0 ? $"your MR {mr}: no change" : "cut by MR");
        }
        bool resistible = sp.TypeOfResists == 2 || (sp.TypeOfResists == 1 && antimagic);
        if (resistible)
            parts.Add($"{SpellDamageCalculator.PlayerFullResistChance(mr, realm):0}% to resist outright");
        if (!sp.CutsDamage && !resistible) parts.Add("ignores MR");
        return string.Join(" · ", parts);
    }

    // A damage range, collapsing an equal min/max to a single figure ("40" not
    // "40-40").
    private static string FormatDamageRange(int min, int max)
        => min == max ? max.ToString() : $"{min}-{max}";
}

// One line of the Attacks panel — deliberately loose text fields (Header,
// Kind, Detail, Energy) rather than a rigid schema, since a physical slot and
// a spell slot show genuinely different information. Resists is a spell slot's
// own line on what the character's resists do to it.
public sealed record AttackRowViewModel(string Header, string Kind, string Detail, string Energy, string Resists = "");
