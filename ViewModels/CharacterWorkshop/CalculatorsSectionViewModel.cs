using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Quests;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// CALCULATORS section — combat what-if tools that sit apart from the live
/// stat sheet:
/// <list type="bullet">
/// <item><b>Monster Matchup</b> — pick a monster by name and see the
/// <em>You → Monster</em> projection (hit%, damage, swings, DPS,
/// rounds-to-kill) computed from your actual gear-derived offense.</item>
/// <item><b>Monster → You</b> — the return direction, made interactive:
/// your AC and dodge seed from the live stat + gear values but are editable,
/// and every physical attack the monster has is listed with its own editable
/// accuracy, so you can dial either side and watch the hit chance move.</item>
/// </list>
/// The player-side offense/defense numbers refresh live from the stat snapshot,
/// inventory, and completed-quest bonuses; the editable inputs re-seed to the
/// actuals on each refresh but are free to override in between.
/// </summary>
public sealed partial class CalculatorsSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly QuestBonusState _questBonuses;
    private Control? _view;

    public override string Id => "calculators";
    public override string Title => "Calculators";
    public override Control View => _view ??= new CalculatorsSectionView { DataContext = this };

    // ----- Monster typeahead ---------------------------------------------
    /// <summary>Typeahead source: "&lt;name&gt; (#&lt;number&gt;)" per monster.</summary>
    public ObservableCollection<string> MonsterNames { get; } = new();
    private readonly Dictionary<string, int> _monsterNumberByLabel = new();
    [ObservableProperty] private string? _selectedMonsterName;

    // ----- Monster values (editable, seeded by the name picker) -----------
    /// <summary>Monster AC used by the You → Monster hit calc — seeded on pick, editable. May be negative.</summary>
    [ObservableProperty] private int _monsterAc;
    /// <summary>Monster damage resist — seeded on pick, editable; trims each of your hits.</summary>
    [ObservableProperty] private int _monsterDr;
    /// <summary>Monster HP — seeded on pick, editable; drives rounds-to-kill.</summary>
    [ObservableProperty] private int _monsterHp;

    // ----- Your values (editable, seeded from the live actuals) -----------
    /// <summary>Your attack accuracy — seeded from the gear-derived actual, editable.</summary>
    [ObservableProperty] private int _playerAccuracy;
    /// <summary>Your AC used in the incoming-hit calc — seeded from actuals, editable. May be negative.</summary>
    [ObservableProperty] private int _playerAc;
    /// <summary>Your raw dodge used in the incoming-hit calc — seeded from actuals, editable. May be negative.</summary>
    [ObservableProperty] private int _playerDodge;

    // ----- Monster → You (incoming) --------------------------------------
    /// <summary>
    /// One row per monster physical attack — or a single "Custom attack" row
    /// when no monster is picked, so the incoming-hit calculator always works.
    /// Each row's accuracy is editable and drives its own hit% vs your AC + dodge.
    /// </summary>
    public ObservableCollection<MonsterAttackRowViewModel> MonsterAttacks { get; } = new();

    // ----- You → Monster (offense projection vs the picked monster) -------
    [ObservableProperty] private string _matchupPlayerHit = "—";
    [ObservableProperty] private string _matchupPlayerDamage = "—";
    [ObservableProperty] private string _matchupSwings = "—";
    [ObservableProperty] private string _matchupDps = "—";
    [ObservableProperty] private string _matchupRounds = "—";
    /// <summary>False when unarmed — gates the swings / DPS / rounds rows.</summary>
    [ObservableProperty] private bool _matchupHasWeapon;

    // Captured player-side numbers (recomputed on every data refresh).
    private RealmType _realm;
    private int _normalAccuracy;
    private int _avgWeaponDamage;
    private double _swingsPerRound;
    private bool _hasWeapon;
    private int _critChance;
    private int _avgCritDamage;
    private int _damageResist;
    private int _protEvil;
    private int _protGood;
    private int _actualAc;
    private int _actualDodge;

    // Alignment of the picked monster — decides which of the player's wards
    // applies in the incoming-hit calc.
    private bool _monsterIsEvil;
    private bool _monsterIsGood;

    public CalculatorsSectionViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory, QuestBonusState questBonuses)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(questBonuses);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _questBonuses = questBonuses;

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _questBonuses.Changed += OnQuestBonusesChanged;
        EnsureMonsterNames();
        Refresh();
    }

    private void Refresh()
    {
        CaptureActuals();
        RebuildMonster();
    }

    // Re-aggregate worn gear + innate race/class bonuses + completed-quest
    // rewards into a combined stat total, then derive the player's offense and
    // defense numbers. This repeats the Character Info tab's aggregation rather
    // than sharing it: the two tabs consume the result for different views, and
    // routing them through one helper would entangle the delicate combat math
    // for no real gain. Second occurrence — extract only if a third appears.
    private void CaptureActuals()
    {
        IReadOnlyList<EquippedItem> worn = _inventory.Snapshot.EquippedItems;
        EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
        if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
        if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);
        CharacterCalculator.ApplyQuestBonuses(combined, _questBonuses.Bonuses, "Quests");
        EquipmentStatSummary t = combined.Totals;

        _realm = _gameData.ActiveRealm;
        int level = _stats.Level;
        int nCombatLevel = GetInt(classRow, "CombatLVL");
        int str = _stats.Strength, agi = _stats.Agility, intel = _stats.Intellect, chm = _stats.Charm;
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        int encumCur = encum.CurrentWeight, encumMax = encum.MaxWeight;

        _normalAccuracy = (level > 0 && nCombatLevel > 0)
            ? CombatCalculator.CalcAccuracy(MudAttackType.Normal, _realm, level, nCombatLevel,
                str, agi, intel, chm, t.TotalWornAccy,
                _realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22,
                encumCur, encumMax, t.WeaponStrReq)
            : 0;

        _hasWeapon = t.WeaponMax > 0;
        MeleeDamageResult dmg = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, _realm, str, t.WeaponMin, t.WeaponMax, t.PlusMaxDamage);
        _avgWeaponDamage = _hasWeapon ? (dmg.MinDamage + dmg.MaxDamage) / 2 : 0;

        SwingCalcResult swings = CombatCalculator.CalcSwings(
            nCombatLevel, level, t.WeaponSpeed, agi, str, t.WeaponStrReq,
            encumCur, encumMax, realmType: _realm);
        _swingsPerRound = swings.RawSwings;

        // Crit folds into DPS the same way CalculateAttack does: gear/quest crit
        // (abil 58) + the Quick-and-Deadly bonus (only when STR meets the weapon's
        // requirement), then diminishing returns. A crit averages 3× the max.
        int qnd = (t.WeaponStrReq <= 0 || str >= t.WeaponStrReq) ? swings.QnDCritBonus : 0;
        _critChance = _hasWeapon ? CombatCalculator.CalcCritChance(t.PlusCrits, qnd, _realm) : 0;
        _avgCritDamage = _hasWeapon ? dmg.MaxDamage * 3 : 0;

        _actualAc = _stats.ArmourClass;
        _actualDodge = CombatCalculator.CalcDodge(level, agi, chm, t.PlusDodge, encumCur, encumMax);
        _protEvil = t.PlusProtEvil;
        _protGood = t.PlusProtGood;
        _damageResist = (int)Math.Round(t.PlusDR, MidpointRounding.AwayFromZero);

        // Seed the editable inputs from the actuals. The setter recompute is
        // cheap and harmless when the attack list is still empty / no monster.
        PlayerAccuracy = _normalAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;
    }

    // Populate the typeahead once from the active set. Cheap to retry if the set
    // wasn't loaded at construction (no monsters yet).
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

    partial void OnSelectedMonsterNameChanged(string? value) => RebuildMonster();
    partial void OnPlayerAccuracyChanged(int value) => RecomputeOutgoing();
    partial void OnPlayerAcChanged(int value) => RecomputeAllRows();
    partial void OnPlayerDodgeChanged(int value) => RecomputeAllRows();
    partial void OnMonsterAcChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterDrChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterHpChanged(int value) => RecomputeOutgoing();

    // Seed the editable monster values + attack rows from the picked monster (or
    // reset to a blank, single-row state when the name is cleared) so the calc is
    // usable as a general-purpose tool with hand-entered numbers on either side.
    private void RebuildMonster()
    {
        EnsureMonsterNames();
        MonsterAttacks.Clear();

        if (string.IsNullOrEmpty(SelectedMonsterName)
            || !_monsterNumberByLabel.TryGetValue(SelectedMonsterName, out int monsterNumber)
            || FindMonsterRowByNumber(monsterNumber) is not JsonElement row)
        {
            _monsterIsEvil = _monsterIsGood = false;
            MonsterAc = MonsterDr = MonsterHp = 0;
            MonsterAttacks.Add(NewAttackRow("—", 50));
            RenumberAttacks();
            RecomputeAllRows();
            RecomputeOutgoing();
            return;
        }

        int align = GetInt(row, "Align");
        _monsterIsEvil = align is 1 or 2 or 5 or 6;
        _monsterIsGood = align is 0 or 4;

        MonsterAc = GetInt(row, "ArmourClass");
        MonsterDr = GetInt(row, "DamageResist");
        MonsterHp = GetInt(row, "HP");

        // Enumerate every physical attack (melee = 1, rob = 3) into an editable
        // row. Spell slots (type 2) carry spell metadata in those columns, so we
        // skip them.
        for (int i = 0; i < 5; i++)
        {
            int type = GetInt(row, $"AttType-{i}");
            if (type is not (1 or 3)) continue;
            int acc = GetInt(row, $"AttAcc-{i}");
            int min = GetInt(row, $"AttMin-{i}");
            int max = GetInt(row, $"AttMax-{i}");
            string damage = string.Create(CultureInfo.InvariantCulture, $"{min}-{max}");
            MonsterAttacks.Add(NewAttackRow(damage, acc));
        }

        // A caster / passive monster with no physical slot still gets one editable
        // row so the incoming calculator isn't left empty.
        if (MonsterAttacks.Count == 0)
            MonsterAttacks.Add(NewAttackRow("—", 50));

        RenumberAttacks();
        RecomputeAllRows();
        RecomputeOutgoing();
    }

    private MonsterAttackRowViewModel NewAttackRow(string damage, int accuracy)
        => new(string.Empty, damage, accuracy, RecomputeRow, RemoveAttackRow);

    // Keep the labels sequential ("Attack 1..N") after any add / remove / rebuild.
    private void RenumberAttacks()
    {
        for (int i = 0; i < MonsterAttacks.Count; i++)
            MonsterAttacks[i].Label = string.Create(CultureInfo.InvariantCulture, $"Attack {i + 1}");
    }

    /// <summary>Append a fresh editable attack row (custom what-if attack).</summary>
    [RelayCommand]
    private void AddAttack()
    {
        MonsterAttackRowViewModel row = NewAttackRow("—", 50);
        MonsterAttacks.Add(row);
        RenumberAttacks();
        RecomputeRow(row);
    }

    // Row-invoked removal. Keep at least one row so the incoming calc is never
    // empty; clearing the last row just resets it to a blank custom attack.
    private void RemoveAttackRow(MonsterAttackRowViewModel row)
    {
        MonsterAttacks.Remove(row);
        if (MonsterAttacks.Count == 0)
            MonsterAttacks.Add(NewAttackRow("—", 50));
        RenumberAttacks();
        RecomputeAllRows();
    }

    // You → Monster projection. Uses the editable player accuracy (so tweaking the
    // ticker moves the hit% and DPS) against the editable monster AC / DR / HP.
    // Always computed — with no monster picked the values default to 0, which the
    // user can hand-enter. Rounds-to-kill shows "—" until HP > 0.
    private void RecomputeOutgoing()
    {
        // Only the monster's AC / DR / HP feed the player-side outputs we show;
        // the attack fields drive the return direction (computed per-row instead),
        // so they can stay zeroed here.
        var monster = new MonsterMatchupProfile(
            ArmourClass: MonsterAc,
            DamageResist: MonsterDr,
            Hp: MonsterHp,
            HasPhysicalAttack: false,
            AttackAccuracy: 0,
            AvgAttackDamage: 0,
            IsEvil: _monsterIsEvil,
            IsGood: _monsterIsGood);

        var player = new PlayerMatchupProfile(
            Realm: _realm,
            NormalAccuracy: PlayerAccuracy,
            AvgWeaponDamage: _avgWeaponDamage,
            SwingsPerRound: _swingsPerRound,
            HasWeapon: _hasWeapon,
            ArmourClass: PlayerAc,
            Dodge: PlayerDodge,
            ProtEvil: _protEvil,
            ProtGood: _protGood,
            DamageResist: _damageResist,
            CritChancePercent: _critChance,
            AvgCritDamage: _avgCritDamage);

        MonsterMatchupResult res = MonsterMatchupCalculator.Compute(player, monster);
        MatchupHasWeapon = res.HasWeapon;
        MatchupPlayerHit = $"{res.PlayerHitPercent}%";
        MatchupPlayerDamage = $"{res.PlayerDamagePerHit} / hit";
        MatchupSwings = res.HasWeapon
            ? res.PlayerSwingsPerRound.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        MatchupDps = res.HasWeapon
            ? res.PlayerDps.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        MatchupRounds = res.RoundsToKill > 0
            ? res.RoundsToKill.ToString(CultureInfo.InvariantCulture)
            : "—";
    }

    // Return-hit chance of one monster attack against the current (editable)
    // player AC + dodge, with the player's ward applied only for the matching
    // alignment. Mirrors MonsterMatchupCalculator's Monster → player direction.
    private int HitFor(int accuracy) => CombatCalculator.CalculateHitChance(
        attackerAccuracy: accuracy,
        defenderAC: PlayerAc,
        defenderDodge: PlayerDodge,
        protEvil: _monsterIsEvil ? _protEvil : 0,
        protGood: _monsterIsGood ? _protGood : 0,
        realmType: _realm).OverallHitPercent;

    private void RecomputeRow(MonsterAttackRowViewModel row)
        => row.HitPercent = string.Create(CultureInfo.InvariantCulture, $"{HitFor(row.Accuracy)}%");

    private void RecomputeAllRows()
    {
        foreach (MonsterAttackRowViewModel row in MonsterAttacks) RecomputeRow(row);
    }

    /// <summary>Discard manual accuracy / AC / dodge edits and re-seed from the live actuals.</summary>
    [RelayCommand]
    private void ResetDefenses()
    {
        PlayerAccuracy = _normalAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;
    }

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void OnInventoryChanged() => Refresh();
    private void OnQuestBonusesChanged() => Refresh();

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _questBonuses.Changed -= OnQuestBonusesChanged;
    }
}
