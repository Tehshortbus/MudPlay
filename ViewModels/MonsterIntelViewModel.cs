using System.Collections.Generic;
using System.Collections.ObjectModel;
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

namespace MudPlay.ViewModels;

// Modeless "Monster Intel" window — a FAST pre-fight check, not a monster
// database browser (that's the Game Data Browser's Monsters tab, which still
// owns the full record: loot, locations, elemental matrix, automation
// overlay editing). A persistent character bar (level, live HP/mana, weapon
// HitMagic, known attack-spell count) sits above a searchable master list
// over MonsterCatalog narrowed to what THIS character can safely fight right
// now: Hittable / Castable (can I land a hit or a spell on it at all) and a
// set of Hits-You-% threshold checkboxes (its own attack's chance to land on
// ME, given my live AC/Dodge/wards). A monster with no computable Hits You %
// (no catalogued physical attack — an NPC/caster-only record) is dropped
// from the list entirely once a character is loaded, since it isn't a
// meaningful "can this thing hurt me" entry. The per-monster detail panel
// keeps only what that decision needs beyond the list: Attacks (how
// dangerous is its swing), Your Matchup (weapon eligibility + ranked spell
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
    // so the projected AC / DR assumes those buffs are up. Null in tests.
    private readonly System.Func<Models.Profile.BuffSettings?>? _buffProvider;
    private readonly ItemMagicIndex? _itemMagic;
    private readonly MonsterObservationTracker? _observations;
    private readonly IReadOnlyList<MonsterIntelEntry> _all;
    private readonly IReadOnlyDictionary<int, int> _spellAttType;
    private readonly bool _hasCharacterContext;
    private readonly List<PlayerAttackSpell> _ownedAttackSpells = new();
    private int _weaponHitMagic;
    // Live player combat totals behind the Hits-You-% threshold checkboxes /
    // master-list "Hits You %" column — recomputed alongside weapon/spell
    // capabilities in RebuildCharacterCapabilities whenever gear changes.
    private int _playerAc;
    private int _playerDodge;
    private int _playerProtEvil;
    private int _playerProtGood;
    // Shadow (Abil 9) is a flat +10 AC that stacks only once no matter how
    // many worn sources carry it — a boolean gate, not the raw accumulated
    // PlusShadowResist total (see GAME_MECHANICS.md's Armour Class section).
    private bool _playerHasShadow;
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

    // "Edit Attacks" picker state. AttackOptions is the picker's rows (usable
    // melee attacks + obtained attack spells); _hiddenAttackKeys are the attacks
    // hidden from Your Matchup; _roundsAttackKey is the single attack driving the
    // master list's Est. Rounds to Kill. Persisted per character in OtherSettings;
    // _buildingOptions suppresses row-event write-back while the list is (re)built.
    private readonly List<MudAttackType> _usableMelee = new();
    private readonly HashSet<string> _hiddenAttackKeys = new(System.StringComparer.Ordinal);
    private string _roundsAttackKey = MeleeKey(MudAttackType.Normal);
    private bool _buildingOptions;
    public ObservableCollection<AttackPickRow> AttackOptions { get; } = new();

    // "Apply Debuffs" picker state. DebuffOptions is the picker's rows (the
    // character's known stat-affecting enemy debuffs); _ownedDebuffSpells backs
    // them; _appliedDebuffKeys are the cast codes folded onto the selected
    // monster; _monsterDebuff is their summed effect. Persisted per character.
    private readonly List<KnownSpell> _ownedDebuffSpells = new();
    private readonly HashSet<string> _appliedDebuffKeys = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _buildingDebuffOptions;
    private Game.Spells.MonsterDebuffEffect _monsterDebuff;
    public ObservableCollection<DebuffPickRow> DebuffOptions { get; } = new();
    [ObservableProperty] private bool _hasAppliedDebuffs;
    [ObservableProperty] private string _debuffSummary = "";
    public bool HasKnownDebuffs => DebuffOptions.Count > 0;

    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string? _nameFilter;
    [ObservableProperty] private MonsterIntelEntry? _selectedEntry;

    public string CountText => $"{RowsView.Count} monster{(RowsView.Count == 1 ? "" : "s")}";

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
        ProfileService? profile = null)
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
            if (e.PropertyName is nameof(NameFilter) or nameof(HideRegenMonsters))
            { RowsView.Refresh(); OnPropertyChanged(nameof(CountText)); }
            else if (e.PropertyName == nameof(SelectedEntry)) { RebuildDetail(); UpdateAcVsTarget(); }
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

            // RowsView was just constructed with Filter = PassesFilter, which
            // reads IncomingHitPercent — still every entry's default -1 until
            // this rebuild runs. Without a Refresh here the view's initial
            // snapshot filters the whole catalog out (the -1 sentinel reads
            // as "no computable Hits You %") and never re-evaluates until the
            // next gear/spell change, so the master list opens empty.
            RebuildCharacterCapabilities();
            RowsView.Refresh();
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
    [ObservableProperty] private int _simAc;
    [ObservableProperty] private int _simProtEvil;
    [ObservableProperty] private int _simVileWard;
    [ObservableProperty] private bool _simShadow;
    // Default Villain/Fiend: worn Vile Ward implies an evil character, and with
    // zero Vile Ward the tier is inert anyway (AdjustVileWard returns 0), so a
    // full-benefit default never overstates a non-evil character's defense.
    [ObservableProperty] private int _simVileWardAlignIndex = 2;

    public IReadOnlyList<string> VileWardAlignOptions { get; } =
        new[] { "Not evil (0%)", "Outlaw / Criminal (50%)", "Villain / Fiend (100%)" };

    private EvilLevel SimEvilLevel => SimVileWardAlignIndex switch
    {
        1 => EvilLevel.Criminal,
        2 => EvilLevel.Fiend,
        _ => EvilLevel.Saint,
    };

    // The effective AC the selected monster's attack actually rolls against —
    // base AC (worn + buffs) + Shadow (vs all) + the wards that apply to THAT
    // monster's alignment (Prot Evil + converted Vile Ward vs evil, Prot Good vs
    // good). "—" until a monster is selected. This is the same `defense` figure
    // that feeds Hits You %, surfaced as a plain number for the picked target.
    [ObservableProperty] private string _acVsTargetText = "—";

    // Hits-You-% filter: a multi-select dropdown of contiguous %-bands. Selecting
    // none shows every monster (still subject to the "no computable value" drop);
    // selecting any keeps a monster whose Hits You % falls in ANY selected band.
    // The band set is REALM-DEPENDENT — the lowest a monster's attack can land is
    // 8% on Stock and 2% on ParaMUD (CombatCalculator.GetHitMin), so Stock drops
    // the 2/5 bands (dead below its floor) and gains an 8% band. Session-only (not
    // persisted), built once from the active realm at construction.
    public ObservableCollection<HitsFilterBucket> HitsFilterBuckets { get; } = new();

    private static readonly int[] ParadigmHitBands =
        { 2, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 100 };
    private static readonly int[] StockHitBands =
        { 8, 10, 15, 20, 25, 30, 35, 40, 45, 50, 100 };

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

    // Ceiling for the master list's "Est. Rounds to Kill" column — edited
    // right here instead of Settings → Other so changing it doesn't mean
    // leaving the window. Loaded once from OtherSettings (Character tier)
    // in the constructor; every edit persists straight back via WriteAt and
    // re-stamps every entry's cap without a full RebuildCharacterCapabilities
    // (only the display ceiling changed, not the underlying projections).
    [ObservableProperty] private int _roundsToKillCap = 999;

    // Recomputes weapon HitMagic, the owned-attack-spell set, and the live
    // AC/Dodge/ward totals behind the Hits-You-% threshold checkboxes,
    // the master list's "Hits You %" / "Est. Rounds to Kill" columns, and
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

    // Your Matchup. Called once at startup and again whenever gear or the
    // spellbook changes, so a mid-session weapon swap or a newly-obtained
    // spell updates the character bar and re-filters the list without
    // reselecting a monster.
    private void RebuildCharacterCapabilities()
    {
        IReadOnlyList<EquippedItem> worn = _inventory!.Snapshot.EquippedItems;
        string? weaponName = worn.FirstOrDefault(w => w.Slot == "Weapon Hand").Name;
        _weaponHitMagic = string.IsNullOrEmpty(weaponName) ? 0 : _itemMagic!.HitMagic(weaponName);
        WeaponSummaryText = string.IsNullOrEmpty(weaponName)
            ? "No weapon equipped"
            : $"{weaponName} (HitMagic {_weaponHitMagic})";

        _ownedAttackSpells.Clear();
        foreach (KnownSpell known in _spellbook!.Available)
        {
            if (!_spellbook.IsObtained(known.Number)) continue;
            long maxDmg = SpellCalculator.MaxDamage(known.Formula, _stats!.Level);
            if (maxDmg <= 0) continue;   // not an attack spell
            int attType = _spellAttType.TryGetValue(known.Number, out int at) ? at : -1;
            // Abil 23 / 108 (AbilityNames.cs) — a caster-side target-type gate
            // independent of SpellImmu/resist: an undead-only spell (e.g. a
            // turn-undead-style attack) does nothing to a living monster, and
            // a living-only spell does nothing to an undead one.
            bool undeadOnly = known.Formula.Abilities.Any(a => a.Code == 23);
            bool livingOnly = known.Formula.Abilities.Any(a => a.Code == 108);
            _ownedAttackSpells.Add(new PlayerAttackSpell(
                known.Name, known.Short, known.ReqLevel, attType,
                maxDmg, SpellCalculator.ManaCost(known.Formula),
                undeadOnly, livingOnly, known.Targets));
        }
        KnownAttackSpellCount = _ownedAttackSpells.Count;

        // Stat-affecting enemy debuffs the character knows — a 0-energy
        // between-round spell aimed at an enemy (single or area) that carries an
        // AC / DR / Dodge / accuracy / slowness ability the matchup sim can fold
        // onto the selected monster. Feeds the "Apply Debuffs" picker.
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
        _playerAc = def.Ac;
        _playerDodge = def.Dodge;
        _playerProtEvil = def.ProtEvil;
        _playerProtGood = def.ProtGood;
        _playerHasShadow = def.Shadow;

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
            SimAc = _playerAc;                   // worn + buffs; Shadow is its own toggle
            SimProtEvil = _playerProtEvil;
            SimVileWard = def.VileWard;
            SimShadow = _playerHasShadow;
            _suppressSimRecompute = false;
        }

        // The monster → player direction — the master list's "Hits You %" column
        // and threshold checkboxes — computed from the (possibly tweaked) simulator.
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
        // the Normal melee swing). The rounds-to-kill cap then filters the list
        // (see PassesFilter), so a superboss projecting into the millions of
        // rounds simply drops out rather than showing a noise number.
        ComputeRoundsToKill(worn, encum);
    }

    // Fill every entry's Est. Rounds to Kill from the currently-selected attack:
    // a melee pick runs the full Compute() matchup; a spell pick divides the
    // monster's HP by that spell's resist-adjusted per-round damage against it.
    private void ComputeRoundsToKill(IReadOnlyList<EquippedItem> worn, EncumbranceReading encum)
    {
        MudAttackType? roundsMelee = MeleeTypeForKey(_roundsAttackKey);
        PlayerAttackSpell? roundsSpell = roundsMelee is null ? SpellForKey(_roundsAttackKey) : null;
        if (roundsMelee is null && roundsSpell is null) roundsMelee = MudAttackType.Normal;  // saved pick gone
        PlayerMatchupProfile? meleeProfile = roundsMelee is { } mt
            ? CharacterCalculator.BuildMeleeAttackProfile(mt, _stats!, worn, encum, _gameData)
            : null;

        foreach (MonsterIntelEntry entry in _all)
        {
            if (entry.Hp <= 0) { entry.EstimatedRoundsToKill = -1; continue; }
            MonsterCatalogEntry m = entry.Source;
            entry.EstimatedRoundsToKill = meleeProfile is { } mp
                ? MonsterMatchupCalculator.Compute(mp, MonsterProfileFor(m)).RoundsToKill
                : SpellRoundsToKill(roundsSpell!.Value, m);
        }
    }

    // Refill every entry's Hits-You-% from the current defense-simulator inputs
    // (AC / Prot Evil / Vile Ward + its alignment scale / Shadow). Dodge and Prot
    // Good stay at their live worn values (the simulator doesn't expose them). The
    // evil-only wards are applied per row against each monster's own alignment
    // inside IncomingHitPercent.
    private void RecomputeIncomingHits()
    {
        if (!_hasCharacterContext) return;
        EvilLevel evil = SimEvilLevel;
        foreach (MonsterIntelEntry entry in _all)
            // Weighted across every physical attack (each hit% blended by its
            // use-chance), not just the majority slot. No debuff on the master
            // list — debuffs are a per-selected-monster what-if in the detail.
            entry.IncomingHitPercent = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
                entry.Source.PhysicalAttacks, accuracyDelta: 0, entry.Source.Align,
                SimAc, _playerDodge, SimProtEvil, _playerProtGood,
                _gameData.ActiveRealm, SimShadow, SimVileWard, evil) ?? -1;
    }

    // A defense-simulator input changed — recompute Hits-You-%, re-filter, and
    // refresh the selected-target AC readout.
    private void OnSimInputChanged()
    {
        if (_suppressSimRecompute || !_hasCharacterContext) return;
        RecomputeIncomingHits();
        RowsView.Refresh();
        OnPropertyChanged(nameof(CountText));
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
        int ac = SimAc + (SimShadow ? 10 : 0);
        if (isEvil)
        {
            ac += SimProtEvil;
            // Vile Ward only counts on Paradigm — CalculateHitChance gates it there.
            if (_gameData.ActiveRealm == RealmType.ParaMud)
                ac += CombatCalculator.AdjustVileWard(SimVileWard, SimEvilLevel);
        }
        if (isGood) ac += _playerProtGood;
        AcVsTargetText = ac.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnSimAcChanged(int value) => OnSimInputChanged();
    partial void OnSimProtEvilChanged(int value) => OnSimInputChanged();
    partial void OnSimVileWardChanged(int value) => OnSimInputChanged();
    partial void OnSimShadowChanged(bool value) => OnSimInputChanged();
    partial void OnSimVileWardAlignIndexChanged(int value) => OnSimInputChanged();

    // The monster's matchup inputs, optionally with an applied debuff folded in.
    // Debuff deltas SUBTRACT from the monster's defense (AC / DR / Dodge) and its
    // to-hit (Accuracy) and are deliberately NOT clamped — a monster pushed below
    // zero accuracy can't land a hit, below-zero AC is trivially hit. Slowness
    // raises the primary attack's energy x1.5 (MMUD-Explorer's ×3/2), thinning the
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
            AttackAccuracy: (m.PhysicalAccuracy?.Majority ?? 0) - debuff.AccDelta,
            AvgAttackDamage: m.PrimaryPhysicalAvgDamage,
            IsEvil: m.Align is 1 or 2 or 5 or 6, IsGood: m.Align is 0 or 4,
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
            new[] { spell }, m.SpellImmunity, m.ElementalResists, m.Undead)[0];
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
    // fall back to Normal so the column always has a basis.
    private void RebuildAttackOptions()
    {
        _buildingOptions = true;
        foreach (AttackPickRow r in AttackOptions) r.PropertyChanged -= OnAttackOptionChanged;
        AttackOptions.Clear();

        foreach (MudAttackType t in _usableMelee)
            AttackOptions.Add(NewOption(MeleeKey(t), MeleeLabel(t), isSpell: false));
        foreach (PlayerAttackSpell s in _ownedAttackSpells)
            AttackOptions.Add(NewOption(SpellKey(s.Short), s.Name, isSpell: true));

        if (AttackOptions.Count > 0 && !AttackOptions.Any(o => o.IsRoundsAttack))
        {
            AttackPickRow fallback = AttackOptions.FirstOrDefault(o => o.Key == MeleeKey(MudAttackType.Normal))
                ?? AttackOptions[0];
            fallback.IsRoundsAttack = true;
            _roundsAttackKey = fallback.Key;
        }
        _buildingOptions = false;
    }

    private AttackPickRow NewOption(string key, string label, bool isSpell)
    {
        var row = new AttackPickRow(key, label, isSpell,
            shown: !_hiddenAttackKeys.Contains(key), isRoundsAttack: key == _roundsAttackKey);
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
        foreach (DebuffPickRow r in DebuffOptions) r.PropertyChanged -= OnDebuffOptionChanged;
        DebuffOptions.Clear();
        foreach (KnownSpell s in _ownedDebuffSpells)
        {
            var row = new DebuffPickRow(s.Short, s.Name,
                DebuffEffectSummary(s), applied: _appliedDebuffKeys.Contains(s.Short));
            row.PropertyChanged += OnDebuffOptionChanged;
            DebuffOptions.Add(row);
        }
        _buildingDebuffOptions = false;
        OnPropertyChanged(nameof(HasKnownDebuffs));
    }

    private void OnDebuffOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_buildingDebuffOptions || sender is not DebuffPickRow row) return;
        if (e.PropertyName != nameof(DebuffPickRow.Applied)) return;
        if (row.Applied) _appliedDebuffKeys.Add(row.Key);
        else _appliedDebuffKeys.Remove(row.Key);
        SaveDebuffSettings();
        RecomputeDebuff();
        if (SelectedEntry is not null) RebuildDetail();   // re-run the matchup debuffed
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

    // "12 rounds", or "999+ rounds" past the display cap (same ceiling the column uses).
    private string FormatRounds(int rounds)
        => rounds > RoundsToKillCap ? $"{RoundsToKillCap}+ rounds"
                                    : $"{rounds} round{(rounds == 1 ? "" : "s")}";

    private void OnCharacterCapabilitiesChanged()
    {
        RebuildCharacterCapabilities();
        RowsView.Refresh();
        OnPropertyChanged(nameof(CountText));
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
        OnPropertyChanged(nameof(CountText));
    }

    private void OnPlayerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerState.ManaType)) UpdateManaLabel();
    }

    private void UpdateManaLabel() => ManaLabel = _playerState?.ManaType == ManaType.Kai ? "Kai" : "Mana";

    // Build the Hits-You-% filter bands for the active realm — contiguous [lo,hi]
    // ranges from the realm's thresholds (Stock omits the 2/5 bands below its 8%
    // floor, and adds an 8% band). Called once at construction; the realm is fixed
    // for the window's life, like the catalog itself.
    private void RebuildHitsFilterBuckets()
    {
        foreach (HitsFilterBucket b in HitsFilterBuckets) b.PropertyChanged -= OnHitsFilterBucketChanged;
        HitsFilterBuckets.Clear();
        int[] bands = _gameData.ActiveRealm == RealmType.ParaMud ? ParadigmHitBands : StockHitBands;
        int lo = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            int hi = bands[i];
            string label = i == 0 ? $"≤{hi}%" : $"{lo}–{hi}%";
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
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HitsFilterLabel));
    }

    private bool PassesFilter(object o)
    {
        if (o is not MonsterIntelEntry e) return false;
        if (!string.IsNullOrWhiteSpace(NameFilter)
            && !e.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase)) return false;

        // Optionally drop timed/boss respawns — a non-zero per-monster RegenTime.
        if (HideRegenMonsters && e.HasRegenTimer) return false;

        // Once a character is loaded, a monster with no computable Hits You %
        // (no catalogued physical attack — an NPC/caster-only record, e.g. a
        // trainer or quest-giver) isn't a meaningful "can this thing hurt me"
        // entry, so it's dropped unconditionally, not just under a checkbox.
        if (_hasCharacterContext && e.IncomingHitPercent < 0) return false;

        // The rounds-to-kill cap FILTERS rather than captioning: a monster the
        // selected attack needs more than the cap to drop is removed (it used to
        // show "<cap>+"). A monster it can't drop at all still shows as "—" — a
        // different axis (can't-kill, not slow-kill) whose Hits-You-% read stays
        // useful — so only a positive projection over the cap is filtered.
        if (_hasCharacterContext && e.EstimatedRoundsToKill > RoundsToKillCap) return false;

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
    // Monsters tab). Attacks (how dangerous is its swing) and Your
    // Matchup/Your Observations (below) are what's left once Overview, the
    // Elemental Defenses matrix, Casts, Loot, Locations, and the Automation
    // overlay editor moved back out — all still reachable from the Game Data
    // Browser's Monsters tab, which owns the full record.

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
    public ObservableCollection<string> MatchupMeleeLines { get; } = new();
    // Your attack spells vs the selected monster, split by target scope for the
    // panel's grouping: single-target (enemy scopes 4/8) and AOE (9/12). Both are
    // ranked by effective damage; hidden picks are filtered out.
    public ObservableCollection<SpellEffectivenessResult> AttackSpellsSingleTarget { get; } = new();
    public ObservableCollection<SpellEffectivenessResult> AttackSpellsAoe { get; } = new();
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

        // Your flat physical damage-resist — the amount subtracted from each of the
        // monster's hits, needed for the per-attack incoming DPM below. 0 without a
        // character (the DPM then stays blank, like the per-attack hit%).
        int playerDr = 0;
        if (_hasCharacterContext)
            playerDr = CharacterCalculator.BuildMeleeAttackProfile(
                MudAttackType.Normal, _stats!, _inventory!.Snapshot.EquippedItems,
                _inventory.Snapshot.Encumbrance, _gameData).DamageResist;

        foreach (MonsterAttackSlot a in m.Attacks)
        {
            int? hit = PerAttackHitYou(a, m);
            AttackRows.Add(BuildAttackRow(a, hit, IncomingAttackDpm(a, m, hit, playerDr)));
        }
        foreach (MonsterMidSpellSlot mid in m.MidSpells)
            AttackRows.Add(new AttackRowViewModel(
                $"({mid.Percent}%) Between-rounds spell", $"Spell #{mid.SpellId}"
                + (mid.Level > 0 ? $" lvl {mid.Level}" : string.Empty), string.Empty, string.Empty, string.Empty));

        RebuildAbilities(m);
        RebuildYourMatchup(m);
        RebuildObservations(m.Number);
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
            ObservationLines.Add($"Hit rate: {o.HitRatePercent:0.#}% ({o.HitCount}/{o.SwingCount} swings)");
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
        if (threat.MonsterHasPhysicalAttack)
        {
            // Hit% here is the SAME weighted blend across all the monster's physical
            // attacks the master list's "Hits You %" uses (folding the accuracy
            // debuff), not just the primary slot — so the summary matches the column.
            // dmg/hit + swings stay the primary-slot figures; dmg/round rides the
            // weighted hit%.
            int hitYou = MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
                m.PhysicalAttacks, _monsterDebuff.AccDelta, m.Align,
                SimAc, _playerDodge, SimProtEvil, _playerProtGood,
                _gameData.ActiveRealm, SimShadow, SimVileWard, SimEvilLevel) ?? threat.MonsterHitPercent;
            double dps = hitYou / 100.0 * threat.MonsterDamagePerHit * threat.MonsterSwingsPerRound;
            // Per-minute rides the rollover-averaged (fractional) swing rate rather than
            // the single-round floor above, so it reflects a real minute of combat.
            double avgSwings = MonsterMatchupCalculator.AverageMonsterSwingsPerRound(
                debuffed.EnergyPerRound, debuffed.PrimaryAttackEnergy);
            double dpm = hitYou / 100.0 * threat.MonsterDamagePerHit * avgSwings
                         * MonsterMatchupCalculator.RoundsPerMinute;
            IncomingThreatLines.Insert(0,
                $"Melee: {hitYou}% to hit you · {threat.MonsterDamagePerHit} dmg/hit · "
                + $"{threat.MonsterSwingsPerRound} attack{(threat.MonsterSwingsPerRound == 1 ? "" : "s")}/round · ~{dps:0}/round · ~{dpm:0} dmg/min"
                + (HasAppliedDebuffs ? "  (debuffed)" : string.Empty));
        }

        // Melee attacks the picker keeps shown — each projected against THIS
        // monster (rounds to kill, hit%, dmg/hit), folding in any applied debuff
        // so the numbers reflect the softened target.
        foreach (MudAttackType mt in _usableMelee)
        {
            if (_hiddenAttackKeys.Contains(MeleeKey(mt))) continue;
            MonsterMatchupResult res = MonsterMatchupCalculator.Compute(
                CharacterCalculator.BuildMeleeAttackProfile(mt, _stats!, worn, encum, _gameData),
                debuffed);
            MatchupMeleeLines.Add(res.HasWeapon && res.RoundsToKill > 0
                ? $"{MeleeLabel(mt)}: ~{res.PlayerDps * MonsterMatchupCalculator.RoundsPerMinute:0} dmg/min · {FormatRounds(res.RoundsToKill)} to kill · {res.PlayerHitPercent}% hit · {res.PlayerDamagePerHit} dmg/hit · {res.PlayerSwingsPerRound:0.0} swings"
                : $"{MeleeLabel(mt)}: can't out-damage it");
        }

        // Attack spells, ranked by effective damage, split single-target vs AOE —
        // hidden picks filtered out. (Stat debuffs don't touch spell resist, so
        // this side is unaffected by the applied debuffs.)
        foreach (SpellEffectivenessResult r in MonsterMatchupCalculatorSpells.RankAttackSpells(
            _ownedAttackSpells, m.SpellImmunity, m.ElementalResists, m.Undead))
        {
            if (_hiddenAttackKeys.Contains(SpellKey(r.Short))) continue;
            if (r.IsAoe) AttackSpellsAoe.Add(r);
            else AttackSpellsSingleTarget.Add(r);
        }
        OnPropertyChanged(nameof(HasSingleTargetSpells));
        OnPropertyChanged(nameof(HasAoeSpells));
        OnPropertyChanged(nameof(HasAnyAttackSpells));
    }

    // This one attack's own chance to land on the player, given the current
    // defense sim (and any applied accuracy debuff). Null without a character or
    // for a non-physical slot — those have no AC-based to-hit.
    private int? PerAttackHitYou(MonsterAttackSlot a, MonsterCatalogEntry m)
    {
        if (!_hasCharacterContext || (a.Type != 1 && a.Type != 3)) return null;
        return MonsterMatchupCalculatorSpells.AttackHitPercent(
            a.Accuracy - _monsterDebuff.AccDelta, m.Align,
            SimAc, _playerDodge, SimProtEvil, _playerProtGood,
            _gameData.ActiveRealm, SimShadow, SimVileWard, SimEvilLevel);
    }

    // This attack's damage per MINUTE against the player: its own to-hit × its avg
    // damage (after your damage-resist) × its rollover-averaged swings/round
    // (m.Energy / attackEnergy, no cap) × 12 rounds. Null for a spell slot or without
    // a character — the same gate as PerAttackHitYou (no AC-based to-hit there).
    private int? IncomingAttackDpm(MonsterAttackSlot a, MonsterCatalogEntry m, int? hitYou, int playerDr)
    {
        if (hitYou is not { } h || a.Energy <= 0) return null;
        double avgPerHit = System.Math.Max(0, (a.MinDamage + a.MaxDamage) / 2.0 - playerDr);
        double swings = MonsterMatchupCalculator.AverageMonsterSwingsPerRound(m.Energy, a.Energy);
        return (int)System.Math.Round(
            h / 100.0 * avgPerHit * swings * MonsterMatchupCalculator.RoundsPerMinute);
    }

    // "Majority" resolves a spell-attack slot's Accuracy field back to a spell
    // number for display (same field-reuse MonsterMdbInfoBuilder decodes). hitYou
    // is this physical attack's own chance to land on the player (null for spell
    // slots / no character), appended to the damage line so each attack shows its
    // individual to-hit next to its accuracy.
    private static AttackRowViewModel BuildAttackRow(MonsterAttackSlot a, int? hitYou, int? incomingDpm)
    {
        string header = string.IsNullOrEmpty(a.Name) ? "Attack" : a.Name;
        string chance = $"({(a.TruePercent > 0 ? (int)Math.Round(a.TruePercent) : a.Percent)}%) {header}";
        if (a.Type == 2)
            return new AttackRowViewModel(chance, $"Spell #{a.Accuracy} lvl {a.MaxDamage}",
                $"Success {a.MinDamage}%", a.Energy > 0 ? $"{a.Energy} energy" : string.Empty, string.Empty);
        string kind = a.Type == 3 ? "Rob" : "Physical";
        string detail = $"{a.MinDamage}-{a.MaxDamage} dmg, acc {a.Accuracy}"
            + (hitYou is { } h ? $" → {h}% to hit you" : string.Empty);
        string dpm = incomingDpm is { } d ? $"~{d} dmg/min incoming" : string.Empty;
        return new AttackRowViewModel(chance, kind, detail,
            a.Energy > 0 ? $"{a.Energy} energy" : string.Empty, dpm);
    }
}

// One line of the Attacks panel — deliberately loose text fields (Header,
// Kind, Detail, Energy) rather than a rigid schema, since a physical slot and
// a spell slot show genuinely different information.
public sealed record AttackRowViewModel(string Header, string Kind, string Detail, string Energy, string Dpm);
