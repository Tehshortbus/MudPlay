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
/// <em>You → Monster</em> projection (hit%, damage, swings, DPS) computed from
/// your gear-derived offense, with an optional weapon picker to model a
/// different weapon's damage against the monster's damage resist.</item>
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

    // ----- Weapon picker (what-if offense) -------------------------------
    /// <summary>Typeahead source: "&lt;name&gt; (#&lt;number&gt;)" per weapon item.</summary>
    public ObservableCollection<string> WeaponNames { get; } = new();
    private readonly Dictionary<string, int> _weaponNumberByLabel = new();
    /// <summary>Selected what-if weapon — null / unmatched means the equipped weapon.</summary>
    [ObservableProperty] private string? _selectedWeaponName;

    // ----- Monster values (editable, seeded by the name picker) -----------
    /// <summary>Monster AC used by the You → Monster hit calc — seeded on pick, editable. May be negative.</summary>
    [ObservableProperty] private int _monsterAc;
    /// <summary>Monster damage resist — seeded on pick, editable; trims each of your hits.</summary>
    [ObservableProperty] private int _monsterDr;
    /// <summary>Monster dodge (the Dodge ability, abil 34) — seeded on pick, editable; lowers your hit chance. 0 for most monsters.</summary>
    [ObservableProperty] private int _monsterDodge;

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
    /// <summary>False when unarmed — gates the swings / DPS / rounds rows.</summary>
    [ObservableProperty] private bool _matchupHasWeapon;

    // ----- Movement Speed calculator -------------------------------------
    /// <summary>Encumbrance percentage feeding the movement calc — seeded live, editable.</summary>
    [ObservableProperty] private int _moveEncumbrance;
    /// <summary>Total quickness feeding the movement calc — seeded from gear, editable.</summary>
    [ObservableProperty] private int _moveQuickness;
    /// <summary>Modelled slowness effect — seeds to 0, editable.</summary>
    [ObservableProperty] private int _moveSlowness;
    [ObservableProperty] private string _moveSpeedText = "—";
    [ObservableProperty] private string _moveStatusText = "—";
    [ObservableProperty] private string _moveAdviceText = string.Empty;

    // ----- Swing calculator ----------------------------------------------
    /// <summary>Selected swing weapon — null / unmatched means the equipped weapon.</summary>
    [ObservableProperty] private string? _selectedSwingWeaponName;
    /// <summary>Character level feeding the swing calc — seeded live, editable.</summary>
    [ObservableProperty] private int _swingLevel;
    /// <summary>Class combat level (1–5) — seeded from the class row, editable.</summary>
    [ObservableProperty] private int _swingCombatLevel;
    [ObservableProperty] private int _swingAgility;
    [ObservableProperty] private int _swingStrength;
    [ObservableProperty] private int _swingEncumbrance;
    /// <summary>Speed-modifier label: "Normal (100)" / "Sped (85)" / "Slow (125)".</summary>
    [ObservableProperty] private string _swingSpeedOption = "Normal (100)";
    [ObservableProperty] private bool _swingBashing;
    [ObservableProperty] private bool _swingSlowness;

    [ObservableProperty] private string _swingEnergyText = "—";
    [ObservableProperty] private string _swingRawText = "—";
    [ObservableProperty] private string _swingEncumText = "—";
    [ObservableProperty] private string _swingQndText = "—";
    /// <summary>False when no weapon (equipped or picked) — gates the swing outputs.</summary>
    [ObservableProperty] private bool _swingHasWeapon;

    /// <summary>Fixed speed-modifier choices for the swing calc's combo box.</summary>
    public string[] SwingSpeedOptions { get; } = { "Normal (100)", "Sped (85)", "Slow (125)" };

    /// <summary>10-round swings / energy-carried breakdown for the picked setup.</summary>
    public ObservableCollection<SwingRoundRow> SwingRounds { get; } = new();

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

    // Offense inputs captured on refresh so the weapon picker can re-derive
    // damage / swings / crit without re-running the full stat aggregation.
    private int _str, _agi, _level, _nCombatLevel, _encumCur, _encumMax;
    private int _plusMaxDamage, _plusCrits;
    private int _equipWeaponMin, _equipWeaponMax, _equipWeaponSpeed, _equipWeaponStrReq;

    // Weapon override from the picker — empty / unmatched falls back to equipped.
    private bool _hasSelectedWeapon;
    private int _selWeaponMin, _selWeaponMax, _selWeaponSpeed, _selWeaponStrReq;

    // Movement inputs captured on refresh — encumbrance% and quickness have live
    // sources; slowness is a modelled effect that always re-seeds to 0.
    private int _actualEncumPercent;
    private int _plusQuickness;

    // Swing weapon override from the picker — empty / unmatched falls back to the
    // equipped weapon's speed / str-req captured above.
    private int _swingWeaponSpeed, _swingWeaponStrReq;

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
        EnsureWeaponNames();
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
        _level = _stats.Level;
        _nCombatLevel = GetInt(classRow, "CombatLVL");
        _str = _stats.Strength;
        _agi = _stats.Agility;
        int intel = _stats.Intellect, chm = _stats.Charm;
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        _encumCur = encum.CurrentWeight;
        _encumMax = encum.MaxWeight;

        // Accuracy stays keyed to the actual equipped weapon's str requirement —
        // it's an editable input the weapon picker deliberately leaves alone.
        _normalAccuracy = (_level > 0 && _nCombatLevel > 0)
            ? CombatCalculator.CalcAccuracy(MudAttackType.Normal, _realm, _level, _nCombatLevel,
                _str, _agi, intel, chm, t.TotalWornAccy,
                _realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22,
                _encumCur, _encumMax, t.WeaponStrReq)
            : 0;

        // Capture the offense inputs, then derive damage / swings / crit through
        // the shared helper so the weapon picker can re-run it in isolation.
        _plusMaxDamage = t.PlusMaxDamage;
        _plusCrits = t.PlusCrits;
        _equipWeaponMin = t.WeaponMin;
        _equipWeaponMax = t.WeaponMax;
        _equipWeaponSpeed = t.WeaponSpeed;
        _equipWeaponStrReq = t.WeaponStrReq;
        ComputeOffense();

        _actualAc = _stats.ArmourClass;
        _actualDodge = CombatCalculator.CalcDodge(_level, _agi, chm, t.PlusDodge, _encumCur, _encumMax);
        _protEvil = t.PlusProtEvil;
        _protGood = t.PlusProtGood;
        _damageResist = (int)Math.Round(t.PlusDR, MidpointRounding.AwayFromZero);

        _actualEncumPercent = encum.Percentage;
        _plusQuickness = t.PlusQuickness;

        // Seed the editable inputs from the actuals. The setter recompute is
        // cheap and harmless when the attack list is still empty / no monster.
        PlayerAccuracy = _normalAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;

        MoveEncumbrance = _actualEncumPercent;
        MoveQuickness = _plusQuickness;
        MoveSlowness = 0;
        // Seeding to values that equal the current backing field won't fire the
        // setter, so recompute explicitly to guarantee the outputs are current.
        RecomputeMovement();

        SwingLevel = _level;
        SwingCombatLevel = _nCombatLevel;
        SwingAgility = _agi;
        SwingStrength = _str;
        SwingEncumbrance = _actualEncumPercent;
        // Keep any weapon the user picked; refresh its fallback speed / str-req.
        ResolveSwingWeapon();
        RecomputeSwing();
    }

    // Derive avg damage / swings / crit from the captured offense inputs,
    // honoring the weapon-picker override (empty picker = the equipped weapon).
    // Accuracy is intentionally excluded — it's a separate editable input the
    // weapon choice doesn't touch.
    private void ComputeOffense()
    {
        int wMin = _hasSelectedWeapon ? _selWeaponMin : _equipWeaponMin;
        int wMax = _hasSelectedWeapon ? _selWeaponMax : _equipWeaponMax;
        int wSpeed = _hasSelectedWeapon ? _selWeaponSpeed : _equipWeaponSpeed;
        int wStrReq = _hasSelectedWeapon ? _selWeaponStrReq : _equipWeaponStrReq;

        _hasWeapon = wMax > 0;
        MeleeDamageResult dmg = CombatCalculator.CalcMeleeDamage(
            MudAttackType.Normal, _realm, _str, wMin, wMax, _plusMaxDamage);
        _avgWeaponDamage = _hasWeapon ? (dmg.MinDamage + dmg.MaxDamage) / 2 : 0;

        SwingCalcResult swings = CombatCalculator.CalcSwings(
            _nCombatLevel, _level, wSpeed, _agi, _str, wStrReq,
            _encumCur, _encumMax, realmType: _realm);
        _swingsPerRound = swings.RawSwings;

        // Crit folds into DPS the same way CalculateAttack does: gear/quest crit
        // (abil 58) + the Quick-and-Deadly bonus (only when STR meets the weapon's
        // requirement), then diminishing returns. A crit averages 3× the max.
        int qnd = (wStrReq <= 0 || _str >= wStrReq) ? swings.QnDCritBonus : 0;
        _critChance = _hasWeapon ? CombatCalculator.CalcCritChance(_plusCrits, qnd, _realm) : 0;
        _avgCritDamage = _hasWeapon ? dmg.MaxDamage * 3 : 0;
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

    // Populate the weapon typeahead once from the active set. Weapons are Items
    // with ItemType == 1 (armour is 0). Cheap to retry if the set wasn't loaded
    // at construction (no items yet).
    private void EnsureWeaponNames()
    {
        if (WeaponNames.Count > 0) return;
        JsonDocument? doc = _gameData.GetRawTable("Items");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (GetInt(row, "ItemType") != 1) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            int number = GetInt(row, "Number");
            string label = string.Create(CultureInfo.InvariantCulture, $"{name} (#{number})");
            WeaponNames.Add(label);
            _weaponNumberByLabel[label] = number;
        }
    }

    // Resolve an Items record by its Number — names aren't unique, so the
    // typeahead label carries the number and we look up against it.
    private JsonElement? FindItemRowByNumber(int number)
    {
        JsonDocument? doc = _gameData.GetRawTable("Items");
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Number", out JsonElement n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v) && v == number)
                return row;
        }
        return null;
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

    partial void OnSelectedWeaponNameChanged(string? value)
    {
        ResolveSelectedWeapon();
        ComputeOffense();
        RecomputeOutgoing();
    }

    // Load the picked weapon's damage / speed / str-req, or clear the override
    // (fall back to the equipped weapon) when the picker is empty or unmatched.
    private void ResolveSelectedWeapon()
    {
        if (SelectedWeaponName is not null
            && _weaponNumberByLabel.TryGetValue(SelectedWeaponName, out int number)
            && FindItemRowByNumber(number) is JsonElement row)
        {
            _selWeaponMin = GetInt(row, "Min");
            _selWeaponMax = GetInt(row, "Max");
            _selWeaponSpeed = GetInt(row, "Speed");
            _selWeaponStrReq = GetInt(row, "StrReq");
            _hasSelectedWeapon = true;
        }
        else
        {
            _hasSelectedWeapon = false;
        }
    }

    partial void OnPlayerAccuracyChanged(int value) => RecomputeOutgoing();
    partial void OnPlayerAcChanged(int value) => RecomputeAllRows();
    partial void OnPlayerDodgeChanged(int value) => RecomputeAllRows();
    partial void OnMonsterAcChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterDrChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterDodgeChanged(int value) => RecomputeOutgoing();

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
            MonsterAc = MonsterDr = MonsterDodge = 0;
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
        // Dodge isn't a top-level column — it rides in the monster's ability
        // slots (Abil-N == 34), like Lord of the Hunt's 70 dodge.
        MonsterDodge = GetAbilityValue(row, 34);

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
    // ticker moves the hit% and DPS) against the editable monster AC / DR. Always
    // computed — with no monster picked the values default to 0, hand-editable.
    private void RecomputeOutgoing()
    {
        // Only the monster's AC / DR feed the player-side outputs we show; the
        // attack fields drive the return direction (computed per-row instead) and
        // HP would only drive rounds-to-kill (dropped — this isn't a round sim),
        // so they can stay zeroed here.
        var monster = new MonsterMatchupProfile(
            ArmourClass: MonsterAc,
            DamageResist: MonsterDr,
            Hp: 0,
            Dodge: MonsterDodge,
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

    /// <summary>Discard manual weapon / accuracy / AC / dodge edits and re-seed from the live actuals.</summary>
    [RelayCommand]
    private void ResetDefenses()
    {
        SelectedWeaponName = null;
        PlayerAccuracy = _normalAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;
    }

    // ----- Movement Speed --------------------------------------------------

    partial void OnMoveEncumbranceChanged(int value) => RecomputeMovement();
    partial void OnMoveQuicknessChanged(int value) => RecomputeMovement();
    partial void OnMoveSlownessChanged(int value) => RecomputeMovement();

    // Solve the movement speed vs the 1-second cap and phrase the advice. "Above
    // cap" means faster than the cap (quickness to spare, since the game clamps
    // movement at one second); "too slow" means more quickness is needed.
    private void RecomputeMovement()
    {
        MovementSpeedResult res = MovementSpeedCalculator.Compute(MoveEncumbrance, MoveQuickness, MoveSlowness);
        MoveSpeedText = string.Create(CultureInfo.InvariantCulture, $"{res.SpeedMillis / 1000.0:0.00} s");
        (MoveStatusText, MoveAdviceText) = res.State switch
        {
            MovementCapState.AboveCap => ("Above cap",
                string.Create(CultureInfo.InvariantCulture,
                    $"You can shed {res.QuicknessToCap:0.0} quickness and stay capped.")),
            MovementCapState.TooSlow => ("Too slow",
                string.Create(CultureInfo.InvariantCulture,
                    $"Need {res.QuicknessToCap:0.0} more quickness to reach the 1-second cap.")),
            _ => ("Perfect", "Exactly at the 1-second movement cap."),
        };
    }

    /// <summary>Discard manual movement edits and re-seed encumbrance / quickness from the live actuals.</summary>
    [RelayCommand]
    private void ResetMovement()
    {
        MoveEncumbrance = _actualEncumPercent;
        MoveQuickness = _plusQuickness;
        MoveSlowness = 0;
    }

    // ----- Swings ----------------------------------------------------------

    partial void OnSelectedSwingWeaponNameChanged(string? value)
    {
        ResolveSwingWeapon();
        RecomputeSwing();
    }

    partial void OnSwingLevelChanged(int value) => RecomputeSwing();
    partial void OnSwingCombatLevelChanged(int value) => RecomputeSwing();
    partial void OnSwingAgilityChanged(int value) => RecomputeSwing();
    partial void OnSwingStrengthChanged(int value) => RecomputeSwing();
    partial void OnSwingEncumbranceChanged(int value) => RecomputeSwing();
    partial void OnSwingSpeedOptionChanged(string value) => RecomputeSwing();
    partial void OnSwingBashingChanged(bool value) => RecomputeSwing();
    partial void OnSwingSlownessChanged(bool value) => RecomputeSwing();

    // Load the picked swing weapon's speed / str-req, or fall back to the equipped
    // weapon when the picker is empty or unmatched.
    private void ResolveSwingWeapon()
    {
        if (SelectedSwingWeaponName is not null
            && _weaponNumberByLabel.TryGetValue(SelectedSwingWeaponName, out int number)
            && FindItemRowByNumber(number) is JsonElement row)
        {
            _swingWeaponSpeed = GetInt(row, "Speed");
            _swingWeaponStrReq = GetInt(row, "StrReq");
        }
        else
        {
            _swingWeaponSpeed = _equipWeaponSpeed;
            _swingWeaponStrReq = _equipWeaponStrReq;
        }
    }

    // Run the full swing model (energy per swing → 1000-budget swings with the
    // round remainder carried forward) for the picked weapon + modifiers.
    private void RecomputeSwing()
    {
        SwingHasWeapon = _swingWeaponSpeed > 0;
        SwingRounds.Clear();

        if (!SwingHasWeapon)
        {
            SwingEnergyText = SwingRawText = SwingEncumText = SwingQndText = "—";
            return;
        }

        int speedModifier = SwingSpeedOption switch
        {
            "Sped (85)" => 85,
            "Slow (125)" => 125,
            _ => 100,
        };

        // CalcSwings derives the encumbrance % from current/max weight; the swing
        // calc exposes the percentage directly, so pass it against a max of 100
        // to make the internal ratio equal the entered percent.
        SwingCalcResult res = CombatCalculator.CalcSwings(
            SwingCombatLevel, SwingLevel, _swingWeaponSpeed, SwingAgility, SwingStrength,
            _swingWeaponStrReq, currentEncum: SwingEncumbrance, maxEncum: 100,
            speedModifier: speedModifier, hasSlowness: SwingSlowness,
            isBashing: SwingBashing, realmType: _realm);

        SwingEnergyText = res.EnergyPerSwing.ToString(CultureInfo.InvariantCulture);
        SwingRawText = res.RawSwings.ToString("0.0", CultureInfo.InvariantCulture);
        SwingEncumText = string.Create(CultureInfo.InvariantCulture, $"{res.EncumPercent}%");
        SwingQndText = res.QnDCritBonus.ToString(CultureInfo.InvariantCulture);

        for (int i = 0; i < res.SwingsPerRound.Length; i++)
            SwingRounds.Add(new SwingRoundRow(i + 1, res.SwingsPerRound[i], res.EnergyRemaining[i]));
    }

    /// <summary>Discard manual swing edits and re-seed weapon / stats from the live actuals.</summary>
    [RelayCommand]
    private void ResetSwing()
    {
        SelectedSwingWeaponName = null;
        SwingLevel = _level;
        SwingCombatLevel = _nCombatLevel;
        SwingAgility = _agi;
        SwingStrength = _str;
        SwingEncumbrance = _actualEncumPercent;
        SwingSpeedOption = "Normal (100)";
        SwingBashing = false;
        SwingSlowness = false;
    }

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    // Scan a monster's ability slots (Abil-0..9) for the given code and return
    // its paired AbilVal, or 0 when absent — how monsters carry Dodge (34) and
    // other stat-style perks that aren't top-level columns.
    private static int GetAbilityValue(JsonElement row, int code)
    {
        for (int i = 0; i < 10; i++)
        {
            if (GetInt(row, $"Abil-{i}") == code)
                return GetInt(row, $"AbilVal-{i}");
        }
        return 0;
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
