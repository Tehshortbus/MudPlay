using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// CHARACTER INFO section — the live stat sheet. Three boxes:
/// <list type="bullet">
/// <item>Box A — Base Stats from the last <c>stat</c> snapshot
/// (<see cref="PlayerStats"/>). Mana relabels to Kai for Mystic classes.</item>
/// <item>Box B — Equipment Bonuses: the worn-gear-only aggregate from
/// <see cref="CharacterCalculator.AggregateEquipmentStats"/>, one row per
/// non-zero stat with a per-item hover breakdown.</item>
/// <item>Box C — Derived combat accuracy (Attack / Bash / Smash / Backstab)
/// from <see cref="CombatCalculator"/>. The aggregate it consumes additionally
/// folds in innate race + class ability bonuses; Smash shows only for
/// smash-capable classes, Backstab only when the character has stealth.</item>
/// </list>
/// Recomputes whenever the live <see cref="PlayerStats"/> snapshot changes or
/// inventory updates; the Reset / Refresh buttons force a re-pull.
/// </summary>
public sealed partial class CharacterInfoSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly PlayerDatabase _playerDb;
    private Control? _view;

    public override string Id => "characterinfo";
    public override string Title => "Character Info";
    public override Control View => _view ??= new CharacterInfoSectionView { DataContext = this };

    // ----- Box A: base stats ---------------------------------------------
    [ObservableProperty] private string _name = "—";
    [ObservableProperty] private string _race = "—";
    [ObservableProperty] private string _charClass = "—";
    [ObservableProperty] private int _level;
    [ObservableProperty] private int _exp;
    [ObservableProperty] private int _lives;
    [ObservableProperty] private int _cp;
    [ObservableProperty] private string _hits = "—";
    /// <summary>"Mana" for casters, "Kai" for Mystic (magery type 5) classes.</summary>
    [ObservableProperty] private string _manaLabel = "Mana";
    [ObservableProperty] private string _manaValue = "—";
    [ObservableProperty] private string _armourClass = "—";

    [ObservableProperty] private int _strength;
    [ObservableProperty] private int _intellect;
    [ObservableProperty] private int _willpower;
    [ObservableProperty] private int _agility;
    [ObservableProperty] private int _health;
    [ObservableProperty] private int _charm;

    [ObservableProperty] private int _perception;
    [ObservableProperty] private int _stealth;
    [ObservableProperty] private int _thievery;
    [ObservableProperty] private int _traps;
    [ObservableProperty] private int _picklocks;
    [ObservableProperty] private int _tracking;
    [ObservableProperty] private int _martialArts;
    [ObservableProperty] private int _magicRes;
    [ObservableProperty] private int _spellcasting;

    // ----- Box B: equipment bonuses --------------------------------------
    public ObservableCollection<EquipBonusRow> BonusRows { get; } = new();
    /// <summary>False when no worn item contributes a bonus — drives the empty-state hint.</summary>
    [ObservableProperty] private bool _hasBonuses;

    // ----- Box C: derived combat -----------------------------------------
    [ObservableProperty] private string _attackAccuracy = "—";
    [ObservableProperty] private string _bashAccuracy = "—";
    [ObservableProperty] private string _smashAccuracy = "—";
    [ObservableProperty] private string _backstabAccuracy = "—";
    /// <summary>Normal-attack damage range ("min-max") for the equipped weapon; em-dash when unarmed.</summary>
    [ObservableProperty] private string _attackDamage = "—";
    /// <summary>Bash damage range ("min-max") for the equipped weapon; em-dash when unarmed.</summary>
    [ObservableProperty] private string _bashDamage = "—";
    /// <summary>Smash damage range ("min-max") for the equipped weapon; em-dash when unarmed or not smash-capable.</summary>
    [ObservableProperty] private string _smashDamage = "—";
    /// <summary>Backstab damage range ("min-max") for the equipped weapon; empty when not stealth-capable.</summary>
    [ObservableProperty] private string _backstabDamage = string.Empty;
    /// <summary>Smash row visible only for smash-capable classes.</summary>
    [ObservableProperty] private bool _showSmash;
    /// <summary>Backstab row visible only when the character has innate (race or class) stealth.</summary>
    [ObservableProperty] private bool _showBackstab;

    // ----- Box D: alignment ----------------------------------------------
    // Alignment is really a numeric "evil points" stat; the title is just the
    // band `who` reports for it. We can't read exact EP in Stock, so we echo
    // the observed title verbatim (it's realm-specific via a modified helpfile,
    // so no fixed word ladder is hardcoded). Item alignment restrictions are a
    // richer flag set (good-only / no-good / neutral-only / evil-only / no-evil
    // / Abil-98 EP-range) handled by the Equipment Manager filter, not here.
    /// <summary>Alignment title from our own <c>who</c> observation, or "—" when unseen.</summary>
    [ObservableProperty] private string _alignment = "—";

    // ----- Box E: monster matchup ----------------------------------------
    /// <summary>
    /// Typeahead source — one entry per monster, labelled <c>"name (#number)"</c>
    /// so duplicate-named monsters stay distinguishable and the user can read off
    /// the exact record number.
    /// </summary>
    public ObservableCollection<string> MonsterNames { get; } = new();
    /// <summary>Maps each typeahead label back to its monster Number for exact-record lookup.</summary>
    private readonly Dictionary<string, int> _monsterNumberByLabel = new(StringComparer.Ordinal);
    /// <summary>The label the user picked; null/empty clears the matchup readout.</summary>
    [ObservableProperty] private string? _selectedMonsterName;
    /// <summary>True once a valid monster row is resolved — gates the whole Box E readout.</summary>
    [ObservableProperty] private bool _hasMatchup;
    // Player → monster.
    [ObservableProperty] private string _matchupPlayerHit = "—";
    [ObservableProperty] private string _matchupPlayerDamage = "—";
    [ObservableProperty] private string _matchupSwings = "—";
    [ObservableProperty] private string _matchupDps = "—";
    [ObservableProperty] private string _matchupRounds = "—";
    /// <summary>False when unarmed — hides the DPS / rounds-to-kill rows that need a weapon.</summary>
    [ObservableProperty] private bool _matchupHasWeapon;
    // Monster → player.
    [ObservableProperty] private string _matchupMonsterHit = "—";
    [ObservableProperty] private string _matchupMonsterDamage = "—";
    /// <summary>False when the monster has no physical attack slot to preview.</summary>
    [ObservableProperty] private bool _matchupMonsterHasAttack;

    // Player-side values captured by ComputeDerivedCombat so the matchup can be
    // recomputed (on monster pick or stat/gear change) without re-aggregating.
    private RealmType _mRealm;
    private int _mNormalAccuracy;
    private int _mAvgWeaponDamage;
    private double _mSwingsPerRound;
    private bool _mHasWeapon;
    private int _mArmourClass;
    private int _mDodge;
    private int _mProtEvil;
    private int _mProtGood;
    private int _mDamageResist;

    public CharacterInfoSectionViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory, PlayerDatabase playerDb)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(playerDb);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _playerDb = playerDb;

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _playerDb.Players.CollectionChanged += OnPlayersChanged;
        EnsureMonsterNames();
        Refresh();
    }

    /// <summary>Re-pull live base stats, re-aggregate gear, recompute derived combat.</summary>
    [RelayCommand]
    public void Refresh()
    {
        RefreshBaseStats();
        RefreshDerived();
        RefreshAlignment();
        RefreshMatchup();
    }

    // ----- Box A ----------------------------------------------------------

    private void RefreshBaseStats()
    {
        Name = Display(_stats.Name);
        Race = Display(_stats.Race);
        CharClass = Display(_stats.Class);
        Level = _stats.Level;
        Exp = _stats.Exp;
        Lives = _stats.Lives;
        Cp = _stats.Cp;
        Hits = $"{_stats.Hits}/{_stats.MaxHits}";

        // Mana vs Kai: Mystic classes (MageryType 5) carry Kai, not Mana.
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        bool isKai = GetInt(classRow, "MageryType") == 5;
        ManaLabel = isKai ? "Kai" : "Mana";
        ManaValue = isKai
            ? $"{_stats.Kai}/{_stats.MaxKai}"
            : $"{_stats.Mana}/{_stats.MaxMana}";

        ArmourClass = $"{_stats.ArmourClass}/{_stats.MaxArmourClass}";

        Strength = _stats.Strength;
        Intellect = _stats.Intellect;
        Willpower = _stats.Willpower;
        Agility = _stats.Agility;
        Health = _stats.Health;
        Charm = _stats.Charm;

        Perception = _stats.Perception;
        Stealth = _stats.Stealth;
        Thievery = _stats.Thievery;
        Traps = _stats.Traps;
        Picklocks = _stats.Picklocks;
        Tracking = _stats.Tracking;
        MartialArts = _stats.MartialArts;
        MagicRes = _stats.MagicRes;
        Spellcasting = _stats.Spellcasting;
    }

    // ----- Box B + C ------------------------------------------------------

    private void RefreshDerived()
    {
        IReadOnlyList<EquippedItem> worn = _inventory.Snapshot.EquippedItems;

        // Box B is equipment-ONLY — its title says "Equipment Bonuses", so
        // racial / class innate bonuses must not leak into it.
        EquipmentStatBreakdown equip = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        RebuildBonusRows(equip);

        // Box C consumes a COMBINED aggregate: worn gear plus the character's
        // innate race + class ability bonuses, which the in-game accuracy
        // formulas account for.
        EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
        if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
        if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);

        ComputeDerivedCombat(combined.Totals, classRow, raceRow);
    }

    private void ComputeDerivedCombat(EquipmentStatSummary t, JsonElement? classRow, JsonElement? raceRow)
    {
        RealmType realm = _gameData.ActiveRealm;
        int level = _stats.Level;
        int nCombatLevel = GetInt(classRow, "CombatLVL");
        int str = _stats.Strength, agi = _stats.Agility, intel = _stats.Intellect, chm = _stats.Charm;

        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        int encumCur = encum.CurrentWeight, encumMax = encum.MaxWeight;

        // Abil 22/105/106 accuracy: ParaMUD sums all sources, Stock takes the
        // single highest. PlusAccuracy holds the sum; MaxSingleAbil22 the max.
        int effectiveAbil22 = realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22;

        HashSet<string>? smashClasses = ClassCapabilities.GetSmashCapableClasses(_gameData);
        bool canSmash = smashClasses is null
            || (!string.IsNullOrEmpty(_stats.Class) && smashClasses.Contains(_stats.Class));
        ShowSmash = canSmash;

        if (level > 0 && nCombatLevel > 0)
        {
            AttackAccuracy = Acc(MudAttackType.Normal, realm, level, nCombatLevel, str, agi, intel, chm, t, encumCur, encumMax);
            BashAccuracy = Acc(MudAttackType.Bash, realm, level, nCombatLevel, str, agi, intel, chm, t, encumCur, encumMax);
            SmashAccuracy = canSmash
                ? Acc(MudAttackType.Smash, realm, level, nCombatLevel, str, agi, intel, chm, t, encumCur, encumMax)
                : "—";
        }
        else
        {
            AttackAccuracy = BashAccuracy = SmashAccuracy = "—";
        }

        // Weapon damage ranges. Only meaningful with a weapon equipped — the
        // unarmed / martial-arts damage path is out of scope for this panel.
        if (t.WeaponMax > 0)
        {
            AttackDamage = MeleeRange(MudAttackType.Normal, realm, str, t);
            BashDamage = MeleeRange(MudAttackType.Bash, realm, str, t);
            SmashDamage = canSmash ? MeleeRange(MudAttackType.Smash, realm, str, t) : "—";
        }
        else
        {
            AttackDamage = BashDamage = SmashDamage = "—";
        }

        bool hasClassStealth = ClassCapabilities.ClassHasStealth(classRow);
        bool hasRaceStealth = ClassCapabilities.RaceHasStealth(raceRow);
        bool canBackstab = hasClassStealth || hasRaceStealth;
        ShowBackstab = canBackstab;

        int stealth = _stats.Stealth;
        if (canBackstab && level > 0 && stealth > 0)
        {
            int bsNormAccy = t.TotalWornAccy + effectiveAbil22;
            int bsAccy = CombatCalculator.CalcBackstabAccuracy(
                stealth, agi, level, str, t.WeaponStrReq,
                t.PlusBSAccuracy, bsNormAccy, hasClassStealth, realm);
            BackstabAccuracy = bsAccy.ToString(CultureInfo.InvariantCulture);

            // Damage range for the equipped weapon (WeaponMin/Max are 0 when
            // unarmed, which CalcBSDamage handles as the strength-only profile).
            BSDamageResult bsDmg = CombatCalculator.CalcBSDamage(
                level, stealth, str, t.WeaponMin, t.WeaponMax,
                t.PlusBSMin, t.PlusBSMax, t.PlusMaxDamage, hasClassStealth, realm);
            BackstabDamage = string.Create(CultureInfo.InvariantCulture,
                $"{bsDmg.MinDamage}-{bsDmg.MaxDamage}");
        }
        else
        {
            // Char has a stealth source but can't compute yet (no level / stealth
            // snapshot) → em-dash; genuinely non-stealth chars read N/A.
            BackstabAccuracy = stealth > 0 ? "—" : "N/A";
            BackstabDamage = string.Empty;
        }

        CapturePlayerMatchupInputs(t, realm, level, nCombatLevel, str, agi, intel, chm, encumCur, encumMax);
    }

    // Snapshot the player-side numbers the monster matchup needs so Box E can
    // recompute against any selected monster without re-aggregating gear.
    private void CapturePlayerMatchupInputs(EquipmentStatSummary t, RealmType realm,
                                            int level, int nCombatLevel,
                                            int str, int agi, int intel, int chm,
                                            int encumCur, int encumMax)
    {
        _mRealm = realm;
        _mNormalAccuracy = (level > 0 && nCombatLevel > 0)
            ? CombatCalculator.CalcAccuracy(MudAttackType.Normal, realm, level, nCombatLevel,
                str, agi, intel, chm, t.TotalWornAccy,
                realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22,
                encumCur, encumMax, t.WeaponStrReq)
            : 0;

        _mHasWeapon = t.WeaponMax > 0;
        MeleeDamageResult dmg = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, realm, str, t.WeaponMin, t.WeaponMax, t.PlusMaxDamage);
        _mAvgWeaponDamage = _mHasWeapon ? (dmg.MinDamage + dmg.MaxDamage) / 2 : 0;

        SwingCalcResult swings = CombatCalculator.CalcSwings(
            nCombatLevel, level, t.WeaponSpeed, agi, str, t.WeaponStrReq,
            encumCur, encumMax, realmType: realm);
        _mSwingsPerRound = swings.RawSwings;

        _mArmourClass = _stats.ArmourClass;
        _mDodge = CombatCalculator.CalcDodge(level, agi, chm, t.PlusDodge, encumCur, encumMax);
        _mProtEvil = t.PlusProtEvil;
        _mProtGood = t.PlusProtGood;
        _mDamageResist = (int)System.Math.Round(t.PlusDR, System.MidpointRounding.AwayFromZero);
    }

    private static string Acc(MudAttackType type, RealmType realm, int level, int nCombatLevel,
                              int str, int agi, int intel, int chm,
                              EquipmentStatSummary t, int encumCur, int encumMax)
    {
        int v = CombatCalculator.CalcAccuracy(type, realm, level, nCombatLevel,
            str, agi, intel, chm, t.TotalWornAccy,
            realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22,
            encumCur, encumMax, t.WeaponStrReq);
        return v.ToString(CultureInfo.InvariantCulture);
    }

    private static string MeleeRange(MudAttackType type, RealmType realm, int str, EquipmentStatSummary t)
    {
        MeleeDamageResult d = CombatCalculator.CalcMeleeDamage(
            type, realm, str, t.WeaponMin, t.WeaponMax, t.PlusMaxDamage);
        return string.Create(CultureInfo.InvariantCulture, $"{d.MinDamage}-{d.MaxDamage}");
    }

    private void RebuildBonusRows(EquipmentStatBreakdown b)
    {
        BonusRows.Clear();
        EquipmentStatSummary t = b.Totals;

        AddDoubleRow(b, "Armour Class", t.PlusAC);
        AddDoubleRow(b, "Damage Resist", t.PlusDR);
        AddIntRow(b, "Strength", t.PlusStrength);
        AddIntRow(b, "Intellect", t.PlusIntellect);
        AddIntRow(b, "Willpower", t.PlusWillpower);
        AddIntRow(b, "Agility", t.PlusAgility);
        AddIntRow(b, "Health", t.PlusHealth);
        AddIntRow(b, "Charm", t.PlusCharm);
        AddIntRow(b, "Max HP", t.PlusMaxHp);
        AddIntRow(b, "Max Mana", t.PlusMaxMana);
        AddIntRow(b, "HP Regen", t.HpRegenPercent);
        AddIntRow(b, "Mana Regen", t.MpRegenPercent);
        AddIntRow(b, "Crits", t.PlusCrits);
        AddAccuracyRow(b, t);
        AddIntRow(b, "Max Damage", t.PlusMaxDamage);
        AddIntRow(b, "Spell Damage", t.SpellDamageBonus);
        AddIntRow(b, "Hit Magic", t.PlusHitMagic);
        AddIntRow(b, "Dodge", t.PlusDodge);
        AddIntRow(b, "Magic Resist", t.PlusMagicResist);
        AddIntRow(b, "BS Accuracy", t.PlusBSAccuracy);
        AddIntRow(b, "BS Min Dmg", t.PlusBSMin);
        AddIntRow(b, "BS Max Dmg", t.PlusBSMax);
        AddIntRow(b, "Stealth", t.PlusStealth);
        AddIntRow(b, "Perception", t.PlusPerception);
        AddIntRow(b, "Spellcasting", t.PlusSpellcasting);
        AddIntRow(b, "Encumbrance", t.PlusEncumbrance);
        AddIntRow(b, "Traps", t.PlusTraps);
        AddIntRow(b, "Picklocks", t.PlusPicklocks);
        AddIntRow(b, "Illuminate", t.PlusIlluminate);
        AddIntRow(b, "Quickness", t.PlusQuickness);
        AddIntRow(b, "Cold Resist", t.PlusColdResist);
        AddIntRow(b, "Fire Resist", t.PlusFireResist);
        AddIntRow(b, "Stone Resist", t.PlusStoneResist);
        AddIntRow(b, "Lightning Resist", t.PlusLightningResist);
        AddIntRow(b, "Water Resist", t.PlusWaterResist);
        AddIntRow(b, "Prot Evil", t.PlusProtEvil);
        AddIntRow(b, "Prot Good", t.PlusProtGood);
        AddIntRow(b, "Punch Dmg", t.PlusPunchDmg);
        AddIntRow(b, "Punch Accy", t.PlusPunchAccy);
        AddIntRow(b, "Kick Dmg", t.PlusKickDmg);
        AddIntRow(b, "Kick Accy", t.PlusKickAccy);
        AddIntRow(b, "JumpKick Dmg", t.PlusJumpKickDmg);
        AddIntRow(b, "JumpKick Accy", t.PlusJumpKickAccy);

        HasBonuses = BonusRows.Count > 0;
    }

    private void AddIntRow(EquipmentStatBreakdown b, string statKey, int value)
    {
        if (value == 0) return;
        string display = value.ToString("+0;-0", CultureInfo.InvariantCulture);
        BonusRows.Add(new EquipBonusRow(statKey, display, BuildTooltip(b, statKey)));
    }

    private void AddDoubleRow(EquipmentStatBreakdown b, string statKey, double value)
    {
        if (value == 0) return;
        string display = value.ToString("+0.#;-0.#", CultureInfo.InvariantCulture);
        BonusRows.Add(new EquipBonusRow(statKey, display, BuildTooltip(b, statKey)));
    }

    // Accuracy total combines worn-item Accy fields with the abil-22 sum — the
    // same number Box C feeds the accuracy formula. Tooltip lists item sources.
    private void AddAccuracyRow(EquipmentStatBreakdown b, EquipmentStatSummary t)
    {
        int total = t.TotalWornAccy + t.PlusAccuracy;
        if (total == 0) return;
        string display = total.ToString("+0;-0", CultureInfo.InvariantCulture);
        BonusRows.Add(new EquipBonusRow("Accuracy", display, BuildTooltip(b, "Accuracy")));
    }

    private static string? BuildTooltip(EquipmentStatBreakdown b, string statKey)
    {
        if (!b.PerStatSources.TryGetValue(statKey, out var sources) || sources.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (StatContribution s in sources)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s.ItemName).Append("  ").Append(s.DisplayValue);
            if (!string.IsNullOrEmpty(s.Tag)) sb.Append(' ').Append(s.Tag);
        }
        return sb.ToString();
    }

    // ----- Box D ----------------------------------------------------------

    // Our own character shows up in our own `who` output, so PlayerDatabase
    // already carries our alignment word — no new parsing needed here.
    private void RefreshAlignment()
    {
        if (string.IsNullOrEmpty(_stats.Name))
        {
            Alignment = "—";
            return;
        }

        (string given, _) = PlayerRecord.SplitName(_stats.Name);
        PlayerRecord? self = null;
        foreach (PlayerRecord r in _playerDb.Players)
        {
            if (string.Equals(r.GivenName, given, StringComparison.OrdinalIgnoreCase))
            {
                self = r;
                break;
            }
        }

        string? word = self?.Alignment;
        Alignment = string.IsNullOrEmpty(word) ? "—" : word;
    }

    // ----- Box E ----------------------------------------------------------

    // Populate the typeahead list once from the active set. Cheap to retry if
    // the set wasn't loaded at construction (no monsters yet).
    private void EnsureMonsterNames()
    {
        if (MonsterNames.Count > 0) return;
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            int number = row.TryGetProperty("Number", out JsonElement numEl)
                         && numEl.ValueKind == JsonValueKind.Number && numEl.TryGetInt32(out int n)
                ? n : 0;
            string label = string.Create(CultureInfo.InvariantCulture, $"{name} (#{number})");
            MonsterNames.Add(label);
            _monsterNumberByLabel[label] = number;
        }
    }

    // Resolve the exact monster record by its Number — names aren't unique, so
    // the typeahead label carries the number and we look up against it.
    private JsonElement? FindMonsterRowByNumber(int number)
    {
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Number", out JsonElement n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v) && v == number)
                return row;
        }
        return null;
    }

    partial void OnSelectedMonsterNameChanged(string? value) => RefreshMatchup();

    private void RefreshMatchup()
    {
        EnsureMonsterNames();

        if (string.IsNullOrEmpty(SelectedMonsterName)
            || !_monsterNumberByLabel.TryGetValue(SelectedMonsterName, out int monsterNumber))
        {
            HasMatchup = false;
            return;
        }

        JsonElement? rowOpt = FindMonsterRowByNumber(monsterNumber);
        if (rowOpt is not JsonElement row)
        {
            HasMatchup = false;
            return;
        }

        int align = GetInt(row, "Align");
        bool isEvil = align is 1 or 2 or 5 or 6;
        bool isGood = align is 0 or 4;

        // Primary physical attack = first slot (0..4) with AttType 1 (melee) or
        // 3 (rob). For those, AttAcc / AttMin / AttMax are numeric; for spell
        // slots (type 2) those columns are spell metadata, so we skip them.
        bool hasPhysical = false;
        int attackAcc = 0, attackAvg = 0;
        for (int i = 0; i < 5; i++)
        {
            int type = GetInt(row, $"AttType-{i}");
            if (type is not (1 or 3)) continue;
            attackAcc = GetInt(row, $"AttAcc-{i}");
            int min = GetInt(row, $"AttMin-{i}");
            int max = GetInt(row, $"AttMax-{i}");
            attackAvg = (min + max) / 2;
            hasPhysical = true;
            break;
        }

        var monster = new MonsterMatchupProfile(
            ArmourClass: GetInt(row, "ArmourClass"),
            DamageResist: GetInt(row, "DamageResist"),
            Hp: GetInt(row, "HP"),
            HasPhysicalAttack: hasPhysical,
            AttackAccuracy: attackAcc,
            AvgAttackDamage: attackAvg,
            IsEvil: isEvil,
            IsGood: isGood);

        var player = new PlayerMatchupProfile(
            Realm: _mRealm,
            NormalAccuracy: _mNormalAccuracy,
            AvgWeaponDamage: _mAvgWeaponDamage,
            SwingsPerRound: _mSwingsPerRound,
            HasWeapon: _mHasWeapon,
            ArmourClass: _mArmourClass,
            Dodge: _mDodge,
            ProtEvil: _mProtEvil,
            ProtGood: _mProtGood,
            DamageResist: _mDamageResist);

        MonsterMatchupResult r = MonsterMatchupCalculator.Compute(player, monster);

        MatchupHasWeapon = r.HasWeapon;
        MatchupPlayerHit = $"{r.PlayerHitPercent}%";
        MatchupPlayerDamage = $"{r.PlayerDamagePerHit} / hit";
        MatchupSwings = r.HasWeapon
            ? r.PlayerSwingsPerRound.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        MatchupDps = r.HasWeapon
            ? r.PlayerDps.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        MatchupRounds = r.RoundsToKill > 0
            ? r.RoundsToKill.ToString(CultureInfo.InvariantCulture)
            : "—";

        MatchupMonsterHasAttack = r.MonsterHasPhysicalAttack;
        if (r.MonsterHasPhysicalAttack)
        {
            MatchupMonsterHit = $"{r.MonsterHitPercent}%";
            MatchupMonsterDamage = $"{r.MonsterDamagePerHit} / hit";
        }
        else
        {
            MatchupMonsterHit = "N/A";
            MatchupMonsterDamage = "N/A";
        }

        HasMatchup = true;
    }

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void OnInventoryChanged()
    {
        RefreshDerived();
        RefreshMatchup();
    }
    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlignment();
}
