using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// EQUIPMENT MANAGER section — one unified view. The left list holds the four
// fixed trigger-purposed gear sets (Default / Backstab / Pre-rest HP / Pre-rest
// Mana); Enable / Disable arm the selected set for automation. Selecting a set
// shows its slot grid on the right: one row per equippable slot (plus the two
// virtual Alt Weapon / Alt Off-Hand rows), each a search box whose suggestions
// are the game-data items that fit the slot and that the live character (level /
// class / alignment) can wear. A slot left blank is {no change} — skipped on
// apply. Every edit auto-persists to CharacterProfile.Equipment; there is no Save
// button.
//
// The set list is fixed, not user-managed: EnsureSets seeds / normalizes exactly
// one set per EquipTriggerType on load. The per-row suggestion lists re-filter
// live as the character's level / class / alignment change (via PlayerStats and
// PlayerDatabase.ObservationRecorded). Apply Now hands the set to
// EquipmentManager.ApplyBySetId; the auto-fire side (resting / combat moments)
// lives in AutoEquipCoordinator.
public sealed partial class EquipmentSectionViewModel : WorkshopSectionViewModel
{
    // The fixed set roster, in left-list display order: trigger → seeded name +
    // @equip- keyword. EnsureSets reconciles the persisted blob to this.
    private static readonly (EquipTriggerType Trigger, string Name, string Keyword)[] Roster =
    {
        (EquipTriggerType.Default, "Default", "default"),
        (EquipTriggerType.Backstab, "Backstab", "backstab"),
        (EquipTriggerType.PreRestHp, "Pre-rest HP", "prerest-hp"),
        (EquipTriggerType.PreRestMana, "Pre-rest Mana", "prerest-mana"),
    };

    private readonly ProfileService _profile;
    private readonly InventoryManager _inventory;
    private readonly GameDataCache _gameData;
    private readonly EquipmentManager _equipment;
    private readonly PlayerStats _stats;
    private readonly PlayerDatabase _players;
    private readonly SettingsResolver _resolver;
    private readonly ItemNameStore _itemNames;
    private readonly ItemOverlaySeedStore _overlaySeed;
    private Control? _view;
    // Gates the edit callbacks while rows / selection are seeded programmatically,
    // so a profile load or set switch doesn't re-persist what it just read.
    private bool _suppress;

    public override string Id => "equipment";
    public override string Title => "Equipment Manager";
    public override Control View => _view ??= new EquipmentSectionView { DataContext = this };

    // The four fixed trigger-purposed sets plus the synthetic Inventory row, in
    // roster order.
    public ObservableCollection<EquipmentSetRowViewModel> SetRows { get; } = new();

    // The slot rows, in EquipmentSlotMap.DisplayOrder.
    public ObservableCollection<EquipmentSlotRowViewModel> Rows { get; } = new();

    // Carried-item rows shown when the Inventory row is selected — one per distinct
    // pack item, with its weight and the loot-automation toggles.
    public ObservableCollection<InventoryItemRowViewModel> InventoryRows { get; } = new();

    // Aggregate bonuses of the selected set's physical-slot items, one row per
    // non-zero stat with a per-item hover breakdown.
    public ObservableCollection<EquipBonusRow> BonusRows { get; } = new();

    // The row being viewed; null when no character is loaded.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSet))]
    [NotifyPropertyChangedFor(nameof(IsInventory))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapshotCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyNowCommand))]
    private EquipmentSetRowViewModel? _selectedSetRow;

    // Transient one-line result of the last Apply Now press.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyStatus))]
    private string _applyStatus = string.Empty;

    // False with no character loaded — gates the set list and the empty state.
    [ObservableProperty] private bool _hasProfile;

    // True when the bonuses panel has at least one non-zero stat row.
    [ObservableProperty] private bool _hasBonuses;

    // True when the Inventory row holds at least one carried item.
    [ObservableProperty] private bool _hasInventoryItems;

    // True when a gear set is selected — gates the slot grid and per-set actions.
    // False for the Inventory row.
    public bool HasSet => SelectedSetRow is { IsInventory: false };

    // True when the Inventory row is selected — gates the carried-item view.
    public bool IsInventory => SelectedSetRow is { IsInventory: true };

    // True when nothing is selected (no character loaded) — shows the empty prompt.
    public bool ShowEmptyState => SelectedSetRow is null;

    // True while an Apply Now status line is showing.
    public bool HasApplyStatus => !string.IsNullOrEmpty(ApplyStatus);

    private EquipmentSet? SelectedSet => SelectedSetRow?.Set;

    public EquipmentSectionViewModel(
        ProfileService profile, InventoryManager inventory,
        GameDataCache gameData, EquipmentManager equipment,
        PlayerStats stats, PlayerDatabase players,
        SettingsResolver resolver, ItemNameStore itemNames,
        ItemOverlaySeedStore overlaySeed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(itemNames);
        ArgumentNullException.ThrowIfNull(overlaySeed);
        _profile = profile;
        _inventory = inventory;
        _gameData = gameData;
        _equipment = equipment;
        _stats = stats;
        _players = players;
        _resolver = resolver;
        _itemNames = itemNames;
        _overlaySeed = overlaySeed;

        BuildRows();
        ReloadFromProfile();

        _profile.ProfileLoaded += OnProfileLoaded;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _stats.PropertyChanged += OnStatsChanged;
        _players.ObservationRecorded += OnObservationRecorded;
        _inventory.Changed += OnInventoryChanged;
    }

    // ----- enable / disable / apply ---------------------------------------

    // Arm the selected set so automation may equip it at its trigger.
    [RelayCommand(CanExecute = nameof(CanEnable))]
    private void Enable() => SetEnabled(true);

    // Disarm the selected set — automation leaves it alone.
    [RelayCommand(CanExecute = nameof(CanDisable))]
    private void Disable() => SetEnabled(false);

    private bool CanEnable => SelectedSetRow is { IsInventory: false, Enabled: false };
    private bool CanDisable => SelectedSetRow is { IsInventory: false, Enabled: true };

    private void SetEnabled(bool enabled)
    {
        if (SelectedSetRow is not { Set: { } set } row) return;
        row.Enabled = enabled;
        set.Enabled = enabled;
        _profile.Save();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
    }

    // Hand the selected set to the engine to walk the character into it.
    [RelayCommand(CanExecute = nameof(HasSet))]
    private void ApplyNow()
    {
        if (SelectedSet is not { } set) return;
        ApplyStatus = _equipment.ApplyBySetId(set.Id) switch
        {
            EquipResult.Applied => "Applying gear set…",
            EquipResult.NoChange => "Already wearing this set.",
            EquipResult.Busy => "An apply is already in progress.",
            _ => "Set could not be resolved.",
        };
    }

    // Open the read-only Item Finder — the full equippable-item catalog with
    // grouped class / level / alignment / stat filters. A browse aid for picking
    // slot items; it doesn't write the set. Concurrent opens are blocked by the
    // async command, so the button can't stack multiple finder windows.
    [RelayCommand]
    private async Task OpenItemFinder()
    {
        var finder = new ItemFinderViewModel(
            _gameData, _stats, _inventory,
            ItemEquipFilter.BucketForWord(LocalAlignmentWord()));
        await AppServices.Current.Dialogs
            .OpenWindowAsync<ItemFinderViewModel, bool>(finder);
    }

    // Seed the physical slots from the live worn loadout.
    [RelayCommand(CanExecute = nameof(HasSet))]
    private void SnapshotCurrent()
    {
        if (SelectedSet is null) return;

        var assigned = new Dictionary<EquipmentSlot, string>();
        foreach (EquippedItem item in _inventory.Snapshot.EquippedItems)
        {
            if (EquipmentSlotMap.FromWornString(item.Slot) is not { } slot) continue;
            assigned[ResolvePairedSlot(slot, assigned)] = item.Name;
        }

        _suppress = true;
        try
        {
            foreach (EquipmentSlotRowViewModel row in Rows)
            {
                if (row.IsVirtual) continue;   // snapshot captures worn gear only
                row.Load(assigned.TryGetValue(row.Slot, out string? name) ? name : null);
            }
        }
        finally { _suppress = false; }

        PersistRowsToSet();
        RebuildBonusRows();
        ApplyStatus = string.Empty;
    }

    partial void OnSelectedSetRowChanged(EquipmentSetRowViewModel? value)
    {
        if (_suppress) return;
        if (value is { IsInventory: true })
            RefreshInventoryRows();
        else
            LoadSelectedSetIntoRows();
        ApplyStatus = string.Empty;
    }

    // FromWornString resolves ambiguous "Finger" / "Wrist" to slot 1; fall through
    // to slot 2 when slot 1 is already taken by an earlier worn piece.
    private static EquipmentSlot ResolvePairedSlot(
        EquipmentSlot slot, IReadOnlyDictionary<EquipmentSlot, string> assigned) => slot switch
    {
        EquipmentSlot.Finger1 when assigned.ContainsKey(EquipmentSlot.Finger1) => EquipmentSlot.Finger2,
        EquipmentSlot.Wrist1 when assigned.ContainsKey(EquipmentSlot.Wrist1) => EquipmentSlot.Wrist2,
        _ => slot,
    };

    // ----- row editing ----------------------------------------------------

    private void OnRowEdited(EquipmentSlotRowViewModel row)
    {
        if (_suppress) return;
        PersistRowsToSet();
        RebuildBonusRows();
        ApplyStatus = string.Empty;
    }

    // Sets persist only their item-bearing slots — a {no change} (blank) row drops
    // out, so the stored list stays sparse.
    private void PersistRowsToSet()
    {
        if (SelectedSet is not { } set) return;
        set.Slots = Rows.Select(r => r.ToEntry()).OfType<EquipmentSlotEntry>().ToList();
        _profile.Save();
    }

    // ----- load / rebuild -------------------------------------------------

    // Materialize the slot row VMs once per game-data set. Suggestions are filled
    // separately by RefreshAvailableItems (which depends on live player stats), so
    // a set swap (different item table) rebuilds the rows then re-filters.
    private void BuildRows()
    {
        Rows.Clear();
        // Once the realm's item table is loaded, drop any slot it has no gear for
        // (e.g. an Eyes / Face slot the data leaves empty) so the grid lists only
        // fillable slots. Before any data loads, show every slot rather than an empty
        // grid — a later ActiveSetChanged rebuilds once the table arrives.
        bool dataLoaded = _gameData.GetRawTable("Items") is not null;
        foreach (EquipmentSlot slot in EquipmentSlotMap.DisplayOrder)
        {
            if (dataLoaded && !EquipmentSlotMap.SlotHasItems(_gameData, slot)) continue;
            Rows.Add(new EquipmentSlotRowViewModel(
                slot, EquipmentSlotMap.Label(slot), EquipmentSlotMap.IsVirtual(slot),
                Array.Empty<string>(), OnRowEdited));
        }
    }

    private void ReloadFromProfile()
    {
        HasProfile = _profile.Current is not null;
        _suppress = true;
        try
        {
            SetRows.Clear();
            if (_profile.Current is { } p)
            {
                EquipmentSettings cfg = p.Equipment ??= new EquipmentSettings();
                if (EnsureSets(cfg)) _profile.Save();
                foreach (EquipmentSet s in cfg.Sets)
                    SetRows.Add(new EquipmentSetRowViewModel(s));
                // The Inventory row trails the gear sets — a live view of the pack,
                // not a persisted set, so it's appended here rather than in EnsureSets.
                SetRows.Add(new EquipmentSetRowViewModel("Inventory"));
            }
            SelectedSetRow = SetRows.FirstOrDefault();
        }
        finally { _suppress = false; }

        LoadSelectedSetIntoRows();
        RefreshAvailableItems();
    }

    // Reconcile the persisted set blob to exactly the four roster entries, one per
    // trigger, in roster order — seeding any missing, normalizing names, seeding a
    // blank keyword, and dropping legacy / duplicate sets. Returns whether anything
    // changed so the caller can persist.
    private static bool EnsureSets(EquipmentSettings cfg)
    {
        var ordered = new List<EquipmentSet>(Roster.Length);
        bool changed = false;
        foreach ((EquipTriggerType trigger, string name, string keyword) in Roster)
        {
            EquipmentSet? set = cfg.Sets.FirstOrDefault(s => s.Trigger == trigger);
            if (set is null)
            {
                // The Default set is enabled out of the box (it's the baseline
                // loadout + the backstab fallback source); the others stay
                // disabled until the user opts in.
                set = new EquipmentSet { Trigger = trigger, Enabled = trigger == EquipTriggerType.Default };
                changed = true;
            }
            if (!string.Equals(set.Name, name, StringComparison.Ordinal)) { set.Name = name; changed = true; }
            if (string.IsNullOrWhiteSpace(set.Keyword)) { set.Keyword = keyword; changed = true; }
            ordered.Add(set);
        }
        // Any set not picked up above (legacy free-form set, a duplicate trigger) is
        // discarded — a count mismatch is the signal it happened.
        if (cfg.Sets.Count != ordered.Count) changed = true;
        cfg.Sets = ordered;
        return changed;
    }

    private void LoadSelectedSetIntoRows()
    {
        var bySlot = new Dictionary<EquipmentSlot, EquipmentSlotEntry>();
        if (SelectedSet is { } set)
            foreach (EquipmentSlotEntry e in set.Slots)
                bySlot.TryAdd(e.Slot, e);

        // The alternate-weapon swap only happens during normal weapon combat, so
        // only the Default set uses the virtual Alt rows. Every other set hides
        // them: backstab fires only on the opening round, and the pre-rest sets
        // trigger out of combat.
        bool hideAlternates = SelectedSet?.Trigger != EquipTriggerType.Default;

        _suppress = true;
        try
        {
            foreach (EquipmentSlotRowViewModel row in Rows)
            {
                row.Applies = !(hideAlternates && row.IsVirtual);
                row.Load(bySlot.TryGetValue(row.Slot, out EquipmentSlotEntry? e) ? e.ItemName : null);
            }
        }
        finally { _suppress = false; }

        RebuildBonusRows();
    }

    // Re-derive every row's suggestion list from the live character's level /
    // class / alignment. A non-positive level / class or an unknown alignment word
    // disables that dimension (ItemEquipFilter degrades gracefully), so an un-stat'd
    // character still sees the slot's full item list rather than an empty one.
    private void RefreshAvailableItems()
    {
        int level = _stats.Level;
        ClassEquipProfile cls = ItemEquipFilter.ResolveClassProfile(_gameData, _stats.Class);
        AlignmentBucket? bucket = ItemEquipFilter.BucketForWord(LocalAlignmentWord());
        foreach (EquipmentSlotRowViewModel row in Rows)
            row.SetAvailableItems(
                EquipmentSlotMap.GetItemsForSlot(_gameData, row.Slot, level, cls, bucket));
    }

    // Our own character appears in our own `who`, so PlayerDatabase already carries
    // our alignment word; match it by given name.
    private string? LocalAlignmentWord()
    {
        if (string.IsNullOrEmpty(_stats.Name)) return null;
        (string given, _) = PlayerRecord.SplitName(_stats.Name);
        foreach (PlayerRecord r in _players.Players)
            if (string.Equals(r.GivenName, given, StringComparison.OrdinalIgnoreCase))
                return r.Alignment;
        return null;
    }

    // ----- equipment bonuses ----------------------------------------------

    // Aggregate the selected set's physical-slot items the same way Character Info
    // aggregates worn gear, so the panel previews exactly what this set grants once
    // worn. Virtual (Alt) slots are swap-time alternates, never worn alongside the
    // primaries, so they're excluded.
    private void RebuildBonusRows()
    {
        BonusRows.Clear();

        var worn = new List<EquippedItem>();
        foreach (EquipmentSlotRowViewModel row in Rows)
        {
            if (row.IsVirtual) continue;
            string? name = string.IsNullOrWhiteSpace(row.ItemName) ? null : row.ItemName!.Trim();
            if (name is null) continue;
            worn.Add(new EquippedItem(name, SlotTag(row.Slot)));
        }

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
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

    // The slot's worn-string tag drives which fields AggregateEquipmentStats fills
    // (weapon damage from "Weapon Hand", off-hand accy from "Off-Hand"); every
    // other field aggregates regardless, so the rest map to a neutral "Worn".
    private static string SlotTag(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Weapon or EquipmentSlot.AlternateWeapon => "Weapon Hand",
        EquipmentSlot.OffHand or EquipmentSlot.AlternateOffHand => "Off-Hand",
        _ => "Worn",
    };

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

    // Accuracy total combines worn-item Accy fields with the abil-22 sum — the same
    // number Character Info feeds the accuracy formula. Tooltip lists item sources.
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

    // ----- inventory ------------------------------------------------------

    // Rebuild the carried-item rows from the last 'i' dump. Identical names are
    // grouped (the overlay is keyed by item type, not instance), so three torches
    // become one row + one set of toggles with a ×3 badge. Weight and the toggle
    // states come from the active game data; an unresolved name still lists (weight
    // "—", toggles disabled) so the player sees the whole pack.
    private void RefreshInventoryRows()
    {
        InventoryRows.Clear();
        foreach (IGrouping<string, string> group in _inventory.Snapshot.CarriedItems
                     .GroupBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string name = group.Key;
            int count = group.Count();
            int number = _itemNames.FindByName(name) ?? 0;
            string weight = _itemNames.WeightOf(name) is int w
                ? w.ToString(CultureInfo.InvariantCulture)
                : "—";   // em dash — weight unknown for this name

            (bool stash, bool collect, bool discard) = ResolveInventoryFlags(number);
            InventoryRows.Add(new InventoryItemRowViewModel(
                number, name, count, weight, stash, collect, discard, OnInventoryFlagToggled));
        }
        HasInventoryItems = InventoryRows.Count > 0;
    }

    // Effective (seed + all-tier override) automation flags for the checkbox
    // initial state, so each toggle shows what the loot engines actually see.
    private (bool Stash, bool Collect, bool Discard) ResolveInventoryFlags(int number)
    {
        if (number <= 0) return (false, false, false);
        string id = number.ToString(CultureInfo.InvariantCulture);
        ItemOverlay seed = _overlaySeed.GetOverlay(number);
        ItemOverlay eff = _resolver.ResolveGameData<ItemOverlay>("Items", id, seed);
        return (eff.AutoStash == true, eff.AutoCollect == true, eff.AutoDiscard == true);
    }

    // Persist a toggled loot flag onto the item's game-data overlay at the Character
    // tier. The write base is the item's own override layer (empty seed defaults),
    // so only the deltas the user actually sets land in the character override — the
    // decoded realm baseline stays in Defaults rather than being copied up. When any
    // loot flag is on and the item carries no policy yet, seed keep-none / get-all
    // (MegaMUD "None" / "All" sentinels); the player refines it in the Game Data
    // browser. Mirrors the item editor's write semantics (a flag stores true or null;
    // there is no explicit false override).
    private void OnInventoryFlagToggled(InventoryItemRowViewModel row)
    {
        if (row.Number <= 0) return;

        string id = row.Number.ToString(CultureInfo.InvariantCulture);
        ItemOverlay existing = _resolver.ResolveGameData<ItemOverlay>("Items", id, new ItemOverlay());

        bool anyAuto = row.AutoStash || row.AutoCollect || row.AutoDiscard;
        ItemOverlay updated = existing with
        {
            AutoStash   = row.AutoStash   ? true : null,
            AutoCollect = row.AutoCollect ? true : null,
            AutoDiscard = row.AutoDiscard ? true : null,
            MinToKeep   = anyAuto ? existing.MinToKeep ?? "None" : existing.MinToKeep,
            MaxToGet    = anyAuto ? existing.MaxToGet  ?? "All"  : existing.MaxToGet,
        };

        _resolver.WriteGameDataAt(SettingsTier.Character, "Items", id, updated);
    }

    // ----- service signals ------------------------------------------------

    private void OnProfileLoaded(CharacterProfile _) => ReloadFromProfile();

    private void OnActiveSetChanged(string? _)
    {
        BuildRows();                // new item table → rebuild rows
        LoadSelectedSetIntoRows();
        RefreshAvailableItems();
        if (IsInventory) RefreshInventoryRows();   // new item table → new weights / flags
    }

    // A full 'i' dump (or an incremental item line) landed — refresh the carried
    // list when it's the view on screen.
    private void OnInventoryChanged()
    {
        if (IsInventory) RefreshInventoryRows();
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Level / class / name (name drives the alignment lookup) re-filter the
        // per-slot suggestion lists.
        if (e.PropertyName is nameof(PlayerStats.Level)
            or nameof(PlayerStats.Class)
            or nameof(PlayerStats.Name))
            RefreshAvailableItems();
    }

    private void OnObservationRecorded(string givenName)
    {
        if (string.IsNullOrEmpty(_stats.Name)) return;
        (string self, _) = PlayerRecord.SplitName(_stats.Name);
        if (string.Equals(self, givenName, StringComparison.OrdinalIgnoreCase))
            RefreshAvailableItems();   // our own who line updated our alignment
    }

    public override void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileLoaded;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
        _stats.PropertyChanged -= OnStatsChanged;
        _players.ObservationRecorded -= OnObservationRecorded;
        _inventory.Changed -= OnInventoryChanged;
    }
}
