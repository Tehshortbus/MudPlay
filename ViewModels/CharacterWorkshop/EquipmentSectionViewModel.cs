using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

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
    // Completed-quest permanent stat rewards, published by the Quest Status tab —
    // folded into the projected-AC line alongside race / class innate bonuses.
    private readonly QuestBonusState _questBonuses;
    private Control? _view;
    // Gates the edit callbacks while rows / selection are seeded programmatically,
    // so a profile load or set switch doesn't re-persist what it just read.
    private bool _suppress;

    public override string Id => "equipment";
    public override string Title => "Equipment Manager";
    public override Control View => _view ??= new EquipmentSectionView { DataContext = this };

    // The four fixed trigger-purposed sets, in roster order.
    public ObservableCollection<EquipmentSetRowViewModel> SetRows { get; } = new();

    // The slot rows, in EquipmentSlotMap.DisplayOrder.
    public ObservableCollection<EquipmentSlotRowViewModel> Rows { get; } = new();

    // Aggregate bonuses of the selected set's physical-slot items, one row per
    // non-zero stat with a per-item hover breakdown.
    public ObservableCollection<EquipBonusRow> BonusRows { get; } = new();

    // Projected total AC bonus — the set's item AC folded with the character's
    // innate race + class bonuses, completed-quest rewards, and any configured
    // self-buff spell that grants AC, plus the shadow property's flat bump. Sits
    // above the item-only "Armour Class" row so the user sees what the set
    // actually lands them at; the server-parsed AC on Character Info is untouched.
    [ObservableProperty] private string _projectedAc = "+0";
    // Newline breakdown of every AC source feeding ProjectedAc, shown on hover.
    [ObservableProperty] private string? _projectedAcTooltip;
    // Prot-Evil is a 1:1 AC bonus, but only versus evil monsters, so it rides its
    // own conditional line instead of folding into the flat projected total.
    [ObservableProperty] private bool _hasProjectedProtEvil;
    [ObservableProperty] private string _projectedProtEvil = "+0";

    // The row being viewed; null when no character is loaded.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSet))]
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

    // The last gear set the engine equipped (or confirmed already worn) — shown
    // next to the Item Finder button. "—" before any apply this session, or when
    // the tracked id no longer matches a set (e.g. after a profile swap).
    [ObservableProperty] private string _currentlyEquipped = "—";

    // False with no character loaded — gates the set list and the empty state.
    [ObservableProperty] private bool _hasProfile;

    // The "Don't swap to default upon entering combat" checkbox — the inverse of the
    // persisted EquipmentSettings.SwapToDefaultOnCombat, so the checkbox reads true
    // (checked) for the long-standing default (keep the pre-rest loadout through a
    // rest-interrupting fight). Unchecking it opts into swapping to Default for the
    // fight and back afterward. Persisted on change; loaded under _suppress.
    [ObservableProperty] private bool _dontSwapToDefaultOnCombat = true;

    // True when the bonuses panel has at least one non-zero stat row.
    [ObservableProperty] private bool _hasBonuses;

    // True when a gear set is selected — gates the slot grid and per-set actions.
    public bool HasSet => SelectedSetRow is not null;

    // True when nothing is selected (no character loaded) — shows the empty prompt.
    public bool ShowEmptyState => SelectedSetRow is null;

    // True while an Apply Now status line is showing.
    public bool HasApplyStatus => !string.IsNullOrEmpty(ApplyStatus);

    private EquipmentSet? SelectedSet => SelectedSetRow?.Set;

    public EquipmentSectionViewModel(
        ProfileService profile, InventoryManager inventory,
        GameDataCache gameData, EquipmentManager equipment,
        PlayerStats stats, PlayerDatabase players, QuestBonusState questBonuses)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(questBonuses);
        _profile = profile;
        _inventory = inventory;
        _gameData = gameData;
        _equipment = equipment;
        _stats = stats;
        _players = players;
        _questBonuses = questBonuses;

        BuildRows();
        ReloadFromProfile();
        RefreshCurrentlyEquipped();

        _profile.ProfileLoaded += OnProfileLoaded;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        _stats.PropertyChanged += OnStatsChanged;
        _players.ObservationRecorded += OnObservationRecorded;
        _questBonuses.Changed += OnQuestBonusesChanged;
        _equipment.CurrentSetChanged += OnCurrentSetChanged;
        _equipment.BlocksChanged += OnBlocksChanged;
    }

    // The engine's unwearable-slot block set changed (an apply, a refusal, an
    // alignment drift / return) — recolour the selected set's rows.
    private void OnBlocksChanged() => RefreshRowBlocks();

    // Mark each visible row blocked when the engine can't wear its item. Read on
    // the UI thread (all block mutations originate there), so no marshalling.
    private void RefreshRowBlocks()
    {
        string? setId = SelectedSet?.Id;
        foreach (EquipmentSlotRowViewModel row in Rows)
            row.Blocked = setId is not null && _equipment.IsSlotBlocked(setId, row.Slot);
    }

    // The engine equipped a different set (Equip Now or an auto-fire trigger) —
    // re-resolve the Currently Equipped name. Fires on the UI thread (all applies
    // are UI-thread), so the property update is safe without marshalling.
    private void OnCurrentSetChanged() => RefreshCurrentlyEquipped();

    private void RefreshCurrentlyEquipped()
    {
        string? id = _equipment.CurrentSetId;
        string? name = string.IsNullOrEmpty(id)
            ? null
            : SetRows.FirstOrDefault(r => string.Equals(r.Set.Id, id, StringComparison.Ordinal))?.Set.Name;
        CurrentlyEquipped = string.IsNullOrWhiteSpace(name) ? "—" : name!;
    }

    // ----- enable / disable / apply ---------------------------------------

    // Arm the selected set so automation may equip it at its trigger.
    [RelayCommand(CanExecute = nameof(CanEnable))]
    private void Enable() => SetEnabled(true);

    // Disarm the selected set — automation leaves it alone.
    [RelayCommand(CanExecute = nameof(CanDisable))]
    private void Disable() => SetEnabled(false);

    private bool CanEnable => SelectedSetRow is { Enabled: false };
    private bool CanDisable => SelectedSetRow is { Enabled: true };

    private void SetEnabled(bool enabled)
    {
        if (SelectedSetRow is not { Set: { } set } row) return;
        row.Enabled = enabled;
        set.Enabled = enabled;
        _profile.Save();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
    }

    // The checkbox is the inverse of the persisted flag; write it through and save,
    // unless we're mid-load (seeding the box from the profile).
    partial void OnDontSwapToDefaultOnCombatChanged(bool value)
    {
        if (_suppress) return;
        if (_profile.Current?.Equipment is not { } cfg) return;
        cfg.SwapToDefaultOnCombat = !value;
        _profile.Save();
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
        if (SelectedSet is { } set)
        {
            // The user addressed this slot — drop any block on it (incl. a
            // server-confirmed refusal), then re-check the new pick so an item
            // the current alignment / level / class can't wear is flagged +
            // skipped straight away (the add-time alignment check).
            _equipment.ClearBlock(set.Id, row.Slot);
            _equipment.RefreshBlocksForSet(set);
        }
        RefreshRowBlocks();
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
            DontSwapToDefaultOnCombat = true;
            if (_profile.Current is { } p)
            {
                EquipmentSettings cfg = p.Equipment ??= new EquipmentSettings();
                if (EnsureSets(cfg)) _profile.Save();
                foreach (EquipmentSet s in cfg.Sets)
                    SetRows.Add(new EquipmentSetRowViewModel(s));
                DontSwapToDefaultOnCombat = !cfg.SwapToDefaultOnCombat;
            }
            SelectedSetRow = SetRows.FirstOrDefault();
        }
        finally { _suppress = false; }

        LoadSelectedSetIntoRows();
        RefreshAvailableItems();
        RefreshCurrentlyEquipped();
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

        // Colour the freshly-loaded set from the engine's block set — evaluate it
        // silently first (no terminal notice for a set the user just clicked).
        if (SelectedSet is { } loaded) _equipment.RefreshBlocksForSet(loaded, announce: false);
        RefreshRowBlocks();
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

        RebuildProjectedAc(worn, b);

        foreach (EquipBonusRow r in EquipBonusRowBuilder.Build(b))
            BonusRows.Add(r);

        HasBonuses = BonusRows.Count > 0;
    }

    // ----- projected total AC ---------------------------------------------

    // The shadow property is a flat +10 AC that lands ONCE no matter how many
    // sources carry it — ten shadow items still grant one +10, not +100.
    private const int ShadowAcBonus = 10;
    // VileWard (Abil 1113) scales AC with the wearer's evil; the magnitude model
    // isn't nailed down, so it's surfaced as presence-only, never a number.
    private const int VileWardAbilityCode = 1113;
    // Ability-slot counts mirror CharacterCalculator: items carry Abil-0..19,
    // race / class records the first ten.
    private const int MaxItemAbilSlots = 20;
    private const int MaxRecordAbilSlots = 10;

    // The AC-relevant portion of the character's configured self-buff spells,
    // summed once per bonus rebuild. ProtEvil rides its own conditional line;
    // shadow / VileWard are presence flags (magnitude handled separately).

    // Fold the set's item AC together with the character's innate race / class
    // bonuses, completed-quest rewards, configured AC self-buffs, and the shadow
    // property into a single projected total — what the set actually lands the
    // character at once worn. The item-only "Armour Class" row below stays as-is,
    // and the server-parsed AC on Character Info is untouched.
    private void RebuildProjectedAc(IReadOnlyList<EquippedItem> worn, EquipmentStatBreakdown itemBreakdown)
    {
        EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
        if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);
        CharacterCalculator.ApplyQuestBonuses(combined, _questBonuses.Bonuses, "Quests");

        // Configured self-applicable buffs, assuming up — the shared roster
        // ("everything that lands on you": self-only, whole-party-on, and
        // single-target cast-on-self) used identically by Monster Intel and
        // Character Info, so the three surfaces never drift.
        BuffDefense buff = Game.Spells.BuffDefenseCalculator.Compute(
            _profile.Current?.PartyBuffs, _stats.Level, AppServices.Current.Spellbook.Available);

        double itemAc = itemBreakdown.Totals.PlusAC;
        double innateAc = combined.Totals.PlusAC - itemAc;   // race + class + quest

        // Shadow lands once across gear / race / class / quest (folded into
        // PlusShadowResist) or any configured self-buff that grants it.
        bool hasShadow = combined.Totals.PlusShadowResist != 0 || buff.HasShadow;
        int shadowAc = hasShadow ? ShadowAcBonus : 0;

        double total = combined.Totals.PlusAC + buff.Ac + shadowAc;
        ProjectedAc = total.ToString("+0.#;-0.#", CultureInfo.InvariantCulture);

        // Prot-Evil is 1 AC per point but only versus evil monsters, so it's a
        // conditional line rather than part of the flat projected total.
        int protEvil = combined.Totals.PlusProtEvil + buff.ProtEvil;
        HasProjectedProtEvil = protEvil != 0;
        ProjectedProtEvil = protEvil.ToString("+0;-0", CultureInfo.InvariantCulture);

        bool hasVileWard = buff.HasVileWard
            || WornHasAbility(worn, VileWardAbilityCode)
            || RowHasAbility(raceRow, VileWardAbilityCode, MaxRecordAbilSlots)
            || RowHasAbility(classRow, VileWardAbilityCode, MaxRecordAbilSlots)
            || _questBonuses.Bonuses.Any(q => q.AbilityId == VileWardAbilityCode);

        ProjectedAcTooltip = BuildProjectedAcTooltip(itemAc, innateAc, buff.Ac, shadowAc, hasVileWard);
    }

    // True when any worn item carries the given ability code in its slots.
    private bool WornHasAbility(IReadOnlyList<EquippedItem> worn, int code)
    {
        foreach (EquippedItem item in worn)
            if (RowHasAbility(_gameData.FindRowByName("Items", item.Name), code, MaxItemAbilSlots))
                return true;
        return false;
    }

    // Scan an Items / Races / Classes row's Abil-0..N slots for a code.
    private static bool RowHasAbility(JsonElement? row, int code, int maxSlots)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return false;
        for (int i = 0; i < maxSlots; i++)
            if (el.TryGetProperty($"Abil-{i}", out JsonElement a)
                && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int v) && v == code)
                return true;
        return false;
    }

    private static string? BuildProjectedAcTooltip(
        double itemAc, double innateAc, double spellAc, int shadowAc, bool hasVileWard)
    {
        var sb = new StringBuilder();
        AppendAcLine(sb, "Items", itemAc);
        AppendAcLine(sb, "Race / class / quests", innateAc);
        AppendAcLine(sb, "Self-buff spells", spellAc);
        if (shadowAc != 0) AppendAcLine(sb, "Shadow property", shadowAc);
        if (hasVileWard)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("VileWard active (scales with your evil)");
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void AppendAcLine(StringBuilder sb, string label, double value)
    {
        if (value == 0) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(label).Append("  ")
          .Append(value.ToString("+0.#;-0.#", CultureInfo.InvariantCulture));
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

    // ----- service signals ------------------------------------------------

    private void OnProfileLoaded(CharacterProfile _) => ReloadFromProfile();

    private void OnActiveSetChanged(string? _)
    {
        BuildRows();                // new item table → rebuild rows
        LoadSelectedSetIntoRows();
        RefreshAvailableItems();
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Level / class / name (name drives the alignment lookup) re-filter the
        // per-slot suggestion lists.
        if (e.PropertyName is nameof(PlayerStats.Level)
            or nameof(PlayerStats.Class)
            or nameof(PlayerStats.Name))
            RefreshAvailableItems();

        // Level / class / race feed the projected-AC line's innate + self-buff
        // contributions, so re-derive it when any of them change.
        if (e.PropertyName is nameof(PlayerStats.Level)
            or nameof(PlayerStats.Class)
            or nameof(PlayerStats.Race))
            RebuildBonusRows();
    }

    // A completed-quest reward change shifts the innate AC bucket — re-derive.
    private void OnQuestBonusesChanged() => RebuildBonusRows();

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
        _questBonuses.Changed -= OnQuestBonusesChanged;
        _equipment.CurrentSetChanged -= OnCurrentSetChanged;
        _equipment.BlocksChanged -= OnBlocksChanged;
    }
}
