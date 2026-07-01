using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Quests;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// CHARACTER INFO section — the live stat sheet:
/// <list type="bullet">
/// <item>Box A — Base Stats from the last <c>stat</c> snapshot
/// (<see cref="PlayerStats"/>). Mana relabels to Kai for Mystic classes.</item>
/// <item>Box C — Derived combat accuracy (Attack / Bash / Smash / Backstab)
/// from <see cref="CombatCalculator"/>. The aggregate it consumes additionally
/// folds in innate race + class ability bonuses <em>and</em> the permanent rewards
/// of completed quests (published by the Quest Status tab via
/// <see cref="QuestBonusState"/>); Smash shows only for smash-capable classes,
/// Backstab only when the character has stealth.</item>
/// <item>Quest Bonuses — a flat readout of every completed quest's permanent stat
/// reward, aggregated by ability. Empty when no completed quest grants a bonus.</item>
/// <item>Inventory — the full carry list harvested from the last <c>i</c> dump:
/// every worn item (with its slot) and every carried-but-unworn item.</item>
/// </list>
/// Every readout recomputes live — base stats when the <c>stat</c> snapshot
/// changes, encumbrance / currency / inventory when the <c>i</c> dump changes,
/// alignment on a <c>who</c> refresh, quest bonuses when the completed-quest set
/// changes. There is no manual refresh; the panel always mirrors the live state.
/// </summary>
public sealed partial class CharacterInfoSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly PlayerDatabase _playerDb;
    private readonly AlignmentTracker _alignmentTracker;
    private readonly QuestBonusState _questBonuses;
    private Control? _view;

    public override string Id => "characterinfo";
    public override string Title => "Character Info";
    public override Control View => _view ??= new CharacterInfoSectionView { DataContext = this };

    // ----- Box A: base stats (mirrors the in-game `stat` grid) -----------
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

    // ----- Quest Bonuses: completed-quest permanent rewards --------------
    /// <summary>One row per ability granted by a completed quest, summed across quests.</summary>
    public ObservableCollection<EquipBonusRow> QuestBonusRows { get; } = new();
    /// <summary>False when no completed quest grants a bonus — drives the empty-state hint.</summary>
    [ObservableProperty] private bool _hasQuestBonuses;

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

    // Martial-arts attacks (Mystic). Punch / Kick / Jumpkick accuracy + damage.
    [ObservableProperty] private string _punchAccuracy = "—";
    [ObservableProperty] private string _punchDamage = "—";
    [ObservableProperty] private string _kickAccuracy = "—";
    [ObservableProperty] private string _kickDamage = "—";
    [ObservableProperty] private string _jumpKickAccuracy = "—";
    [ObservableProperty] private string _jumpKickDamage = "—";
    /// <summary>Punch/Kick/Jumpkick rows visible only for Stock characters with a positive Martial Arts skill.</summary>
    [ObservableProperty] private bool _showMartialArts;

    // ----- Box A: alignment standing -------------------------------------
    // Rendered on the last row of Box A (where the game prints "You are
    // <standing>."). Alignment is really a numeric "evil points" stat; the title is just the
    // band `who` reports for it. We can't read exact EP in Stock, so we echo
    // the observed title verbatim (it's realm-specific via a modified helpfile,
    // so no fixed word ladder is hardcoded). Item alignment restrictions are a
    // richer flag set (good-only / no-good / neutral-only / evil-only / no-evil
    // / Abil-98 EP-range) handled by the Equipment Manager filter, not here.
    /// <summary>Alignment title from our own <c>who</c> observation, or "—" when unseen.</summary>
    [ObservableProperty] private string _alignment = "—";
    /// <summary>
    /// True after "A dark cloud passes over you" (alignment dropped) until the
    /// next <c>who</c> refresh — drives the "(stale)" hint next to Alignment.
    /// </summary>
    [ObservableProperty] private bool _alignmentStale;

    // ----- Box A: carry weight + carried currency ------------------------
    // Both are inventory-sourced (InventoryManager's snapshot), refreshed live
    // on every `i` dump or incremental coin line — no manual re-pull needed.
    /// <summary>Current / max carry weight from the last inventory reading ("cur / max").</summary>
    [ObservableProperty] private string _encumbrance = "—";
    /// <summary>Per-denomination coins currently carried (nonzero only), or "none".</summary>
    [ObservableProperty] private string _currencyHeld = "—";
    /// <summary>Consolidated wealth of every coin carried, broken into denominations.</summary>
    [ObservableProperty] private string _totalWealth = "—";

    // ----- Inventory: the full carry list from the last `i` dump ---------
    /// <summary>Worn items ("name (Slot)") harvested from the last inventory dump.</summary>
    public ObservableCollection<string> EquippedItems { get; } = new();
    /// <summary>Carried-but-unworn item names harvested from the last inventory dump.</summary>
    public ObservableCollection<string> CarriedItems { get; } = new();
    /// <summary>True once at least one worn item is known — gates the equipped list.</summary>
    [ObservableProperty] private bool _hasEquipped;
    /// <summary>True once at least one carried item is known — gates the carried list.</summary>
    [ObservableProperty] private bool _hasCarried;
    /// <summary>False until the first <c>i</c> dump is parsed — drives the "type i to load" hint.</summary>
    [ObservableProperty] private bool _inventoryLoaded;

    public CharacterInfoSectionViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory, PlayerDatabase playerDb, AlignmentTracker alignmentTracker, QuestBonusState questBonuses)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(playerDb);
        ArgumentNullException.ThrowIfNull(alignmentTracker);
        ArgumentNullException.ThrowIfNull(questBonuses);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _playerDb = playerDb;
        _alignmentTracker = alignmentTracker;
        _questBonuses = questBonuses;

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _playerDb.Players.CollectionChanged += OnPlayersChanged;
        _alignmentTracker.StaleChanged += OnAlignmentStaleChanged;
        _questBonuses.Changed += OnQuestBonusesChanged;
        Refresh();
    }

    // Re-pull every live readout: base stats + derived combat from the stat
    // snapshot, alignment from `who`, and wealth + full carry list from the
    // last `i` dump. Wired to every source's change event — no manual refresh.
    private void Refresh()
    {
        RefreshBaseStats();
        RefreshDerived();
        RefreshAlignment();
        RefreshWealth();
        RefreshInventory();
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

        // Box C consumes a COMBINED aggregate: worn gear plus the character's
        // innate race + class ability bonuses, which the in-game accuracy
        // formulas account for.
        EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
        if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
        if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);

        // Completed quests grant permanent stat rewards the same accuracy/damage
        // formulas account for — fold them into the combined aggregate (never Box B,
        // which is equipment-only) and surface them in their own readout.
        CharacterCalculator.ApplyQuestBonuses(combined, _questBonuses.Bonuses, "Quests");
        RebuildQuestBonusRows();

        ComputeDerivedCombat(combined.Totals, classRow, raceRow);
    }

    // Aggregate the published completed-quest bonuses by ability id (quests stack,
    // so a stat granted by two quests sums) into the Quest Bonuses box rows.
    private void RebuildQuestBonusRows()
    {
        QuestBonusRows.Clear();
        var byAbil = new Dictionary<int, int>();
        foreach (QuestBonus b in _questBonuses.Bonuses)
        {
            if (b.AbilityId <= 0 || b.Value == 0) continue;
            byAbil[b.AbilityId] = byAbil.TryGetValue(b.AbilityId, out int v) ? v + b.Value : b.Value;
        }
        foreach (KeyValuePair<int, int> kv in byAbil.OrderBy(p => p.Key))
        {
            if (kv.Value == 0) continue;
            string display = kv.Value.ToString("+0;-0", CultureInfo.InvariantCulture);
            QuestBonusRows.Add(new EquipBonusRow(AbilityNames.FormatId(kv.Key), display, null));
        }
        HasQuestBonuses = QuestBonusRows.Count > 0;
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

        // Martial-arts attacks — Mystic special attacks. Shown for any realm
        // with a positive Martial Arts skill; the damage formula branches Stock
        // vs GreaterMUD inside CalcMartialArtsDamage.
        int maSkill = _stats.MartialArts;
        bool showMa = maSkill > 0;
        ShowMartialArts = showMa;
        if (showMa && level > 0 && nCombatLevel > 0)
        {
            // MA accuracy is the normal-attack accuracy with weapon-hand accy
            // excluded — MME doesn't fold the wielded weapon's accy into a
            // martial-arts strike — plus the per-attack item accy bonus.
            int maWornAccy = t.TotalWornAccy - t.WeaponHandAccy - t.OffHandAccy;
            if (maWornAccy < 0) maWornAccy = 0;
            int maBaseAccy = CombatCalculator.CalcAccuracy(
                MudAttackType.Normal, realm, level, nCombatLevel,
                str, agi, intel, chm, maWornAccy, effectiveAbil22,
                encumCur, encumMax, weaponStrReq: 0);

            // GreaterMUD applies a per-attack accuracy penalty (kick -10,
            // jumpkick -15); Stock has none.
            int kickAccyPenalty = realm == RealmType.ParaMud ? 10 : 0;
            int jumpKickAccyPenalty = realm == RealmType.ParaMud ? 15 : 0;

            PunchAccuracy = (maBaseAccy + t.PlusPunchAccy).ToString(CultureInfo.InvariantCulture);
            KickAccuracy = (maBaseAccy + t.PlusKickAccy - kickAccyPenalty).ToString(CultureInfo.InvariantCulture);
            JumpKickAccuracy = (maBaseAccy + t.PlusJumpKickAccy - jumpKickAccyPenalty).ToString(CultureInfo.InvariantCulture);

            PunchDamage = MARange(MudAttackType.Punch, realm, level, maSkill, str, t.PlusMaxDamage, t.PlusPunchDmg);
            KickDamage = MARange(MudAttackType.Kick, realm, level, maSkill, str, t.PlusMaxDamage, t.PlusKickDmg);
            JumpKickDamage = MARange(MudAttackType.Jumpkick, realm, level, maSkill, str, t.PlusMaxDamage, t.PlusJumpKickDmg);
        }
        else
        {
            PunchAccuracy = KickAccuracy = JumpKickAccuracy = "—";
            PunchDamage = KickDamage = JumpKickDamage = "—";
        }
    }

    private static string MARange(MudAttackType type, RealmType realm, int level, int maSkill, int str,
                                  int plusMaxDamage, int maPlusDamage)
    {
        MeleeDamageResult d = CombatCalculator.CalcMartialArtsDamage(
            type, realm, level, maSkill, str, plusMaxDamage, maPlusDamage);
        return string.Create(CultureInfo.InvariantCulture, $"{d.MinDamage}-{d.MaxDamage}");
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

    // ----- Box A: alignment standing --------------------------------------

    // Our own character shows up in our own `who` output, so PlayerDatabase
    // already carries our alignment word — no new parsing needed here.
    private void RefreshAlignment()
    {
        AlignmentStale = _alignmentTracker.IsStale;

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

    // ----- Box A: carry weight + carried currency -------------------------

    // Encumbrance + currency both ride the same InventoryManager snapshot, so
    // they refresh together on every `i` dump or incremental coin line.
    private void RefreshWealth()
    {
        InventorySnapshot snap = _inventory.Snapshot;

        EncumbranceReading enc = snap.Encumbrance;
        Encumbrance = enc.MaxWeight > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{enc.CurrentWeight} / {enc.MaxWeight}")
            : "—";

        CurrencyHoldings coins = snap.Currency;
        CurrencyHeld = FormatCoins(coins);
        TotalWealth = coins.TotalCopperValue > 0 ? FormatWealth(coins.TotalCopperValue) : "—";
    }

    // Per-denomination coins currently carried, high → low, nonzero only.
    private static string FormatCoins(CurrencyHoldings c)
    {
        var parts = new List<string>(5);
        if (c.Runic > 0) parts.Add($"{c.Runic:N0} runic");
        if (c.Platinum > 0) parts.Add($"{c.Platinum:N0} platinum");
        if (c.Gold > 0) parts.Add($"{c.Gold:N0} gold");
        if (c.Silver > 0) parts.Add($"{c.Silver:N0} silver");
        if (c.Copper > 0) parts.Add($"{c.Copper:N0} copper");
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    // Greedy decomposition of the consolidated copper value back into the
    // largest denominations — the "what's it all worth" figure, independent of
    // which physical coins are actually held.
    private static string FormatWealth(long totalCopper)
    {
        long remaining = totalCopper;
        var parts = new List<string>(5);
        void Take(long unit, string label)
        {
            long n = remaining / unit;
            if (n <= 0) return;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n:N0} {label}"));
            remaining -= n * unit;
        }
        Take(1_000_000, "runic");
        Take(10_000, "platinum");
        Take(100, "gold");
        Take(10, "silver");
        Take(1, "copper");
        return parts.Count > 0 ? string.Join(", ", parts) : "—";
    }

    // ----- Inventory: the full carry list from the last `i` dump ----------

    private void RefreshInventory()
    {
        InventorySnapshot snap = _inventory.Snapshot;

        EquippedItems.Clear();
        foreach (EquippedItem item in snap.EquippedItems)
            EquippedItems.Add(string.IsNullOrEmpty(item.Slot)
                ? item.Name
                : string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({item.Slot})"));

        CarriedItems.Clear();
        foreach (string name in snap.CarriedItems)
            CarriedItems.Add(name);

        HasEquipped = EquippedItems.Count > 0;
        HasCarried = CarriedItems.Count > 0;
        InventoryLoaded = _inventory.IsLoaded;
    }

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    // An `i` dump (or incremental coin/item line) landed — refold gear into
    // derived combat and re-pull wealth + the full carry list.
    private void OnInventoryChanged()
    {
        RefreshDerived();
        RefreshWealth();
        RefreshInventory();
    }
    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlignment();
    // Dark-cloud line fired (or a `who` cleared it) — just sync the flag; the
    // alignment word itself refreshes on the PlayerDatabase update.
    private void OnAlignmentStaleChanged() => AlignmentStale = _alignmentTracker.IsStale;

    // The Quest Status tab republished the completed-quest bonus set — refold it
    // into derived combat (which consumes the combined aggregate).
    private void OnQuestBonusesChanged() => RefreshDerived();

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _playerDb.Players.CollectionChanged -= OnPlayersChanged;
        _alignmentTracker.StaleChanged -= OnAlignmentStaleChanged;
        _questBonuses.Changed -= OnQuestBonusesChanged;
    }
}
