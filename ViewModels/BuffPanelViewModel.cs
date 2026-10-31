using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels;

// The Buff Watchdog window's buff panel: add / remove party-buff slots, pick a buff from
// the spellbook, set its recast timer, and choose targeting (whole-party on/off,
// or which members for a single-target buff). Backed by CharacterProfile.PartyBuffs
// (char-only), persisted on every edit. The member checklist rebuilds when the
// party changes, preserving the stored per-slot selection by given name.
public sealed partial class BuffPanelViewModel : ObservableObject, IDisposable
{
    private readonly Game.PartyState _party;
    private readonly ProfileService _profile;
    private readonly SpellbookState _spellbook;
    private BuffSettings _settings = new();
    private IReadOnlyList<Game.Spells.BuffOverwritePair> _overwritePairs = Array.Empty<Game.Spells.BuffOverwritePair>();
    private bool _disposed;

    // Every configured buff row, bound STRAIGHT to the config ItemsControl (not through
    // a computed view) so an add / remove touches a single container instead of forcing
    // the whole list to rebuild — the source of the remove-a-buff lag when the list ran
    // long. Default layout auto-groups by category (buffs you aim, then whole-party,
    // then item "on use") via InsertSorted / ResortRow; once the user re-arranges rows
    // (ManualOrder) it mirrors the stored order 1:1 and new buffs append at the bottom.
    public ObservableCollection<BuffSlotRowViewModel> Slots { get; } = new();

    // Current party's non-self members as column headers (capitalised given names),
    // in the same order every row builds its target checkboxes — so the header
    // names line up over the per-row checkbox columns in the grid.
    public ObservableCollection<string> Members { get; } = new();

    // True when there's at least one non-self party member — gates the row targeting
    // column HEADERS (the member names + the "All/None" label). Solo hides them so a
    // solo player sees only the Self column and isn't confused by an empty All/None.
    [ObservableProperty]
    private bool _showPartyColumns;

    // The one mana floor for every buff (BuffSettings.BlessIfAboveMa), shown above
    // the list. Editing it changes only what new buffs start at; "Set on all" also
    // writes it onto every configured buff.
    [ObservableProperty]
    private int _allBuffsFloor = BuffSlot.DefaultBlessIfAboveMa;

    partial void OnAllBuffsFloorChanged(int value)
    {
        if (_settings.BlessIfAboveMa == value) return;
        _settings.BlessIfAboveMa = value;
        Persist();
    }

    public string AllBuffsFloorLabel => AppServices.Current.ManaThresholdsAreAbsolute
        ? "Cast buffs if mana ≥" : "Cast buffs if mana ≥ %";
    public int AllBuffsFloorMaximum => AppServices.Current.ManaThresholdsAreAbsolute ? 100_000 : 100;

    [RelayCommand]
    private void SetFloorOnAllBuffs()
    {
        foreach (BuffSlot dto in _settings.Slots) dto.BlessIfAboveMa = AllBuffsFloor;
        foreach (BuffSlotRowViewModel row in Slots) row.Refresh();
        AppServices.Current.Log.Info("Buffs",
            $"mana floor {AllBuffsFloor} set on all {_settings.Slots.Count} buff(s)");
        Persist();
    }

    // Picker source: LEARNED buffs (zero energy — self / single-target / whole-party
    // scopes, plus whole-party cast-on-use items) NOT already slotted. A given buff is
    // one slot: two slots of the same spell would double-track its recast timer, so a
    // spell already in a slot drops out of the picker.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    [NotifyPropertyChangedFor(nameof(CanAddBuff))]
    [NotifyPropertyChangedFor(nameof(SetupHint))]
    [NotifyPropertyChangedFor(nameof(HasSetupHint))]
    [NotifyPropertyChangedFor(nameof(CanReadSpellList))]
    private IReadOnlyList<SpellPick> _buffPicks = Array.Empty<SpellPick>();

    // True when no slot is configured yet — drives the empty-state hint.
    public bool HasSlots => Slots.Count > 0;

    // True once the user hand-arranged the rows (layout customised) — gates the
    // "Reset order" button that returns to the automatic category grouping.
    public bool HasManualOrder => _settings.ManualOrder;

    // The cast-priority toggle state + its label. Independent of the layout: the
    // user can arrange rows however they like and still cast by the default type
    // order, OR flip this so the engine casts top-to-bottom in the shown order.
    public bool IsPriorityTopDown => _settings.PriorityTopDown;
    public string PriorityModeLabel => _settings.PriorityTopDown
        ? "Cast priority: top → bottom (your order)"
        : "Cast priority: default (by type)";

    // Whether the Add button can do anything — every qualifying buff already
    // slotted leaves nothing to add.
    public bool CanAddBuff => BuffPicks.Count > 0;

    // Why there is no buff to add, in the user's terms, or empty when there is one.
    // The usual case is a new profile: the class's spells are known but which of them
    // the character has learned is not, until the game's own list has been read. The
    // window used to show nothing but "add buffs in the config panel" beside a panel
    // it had hidden.
    public string SetupHint
    {
        get
        {
            if (CanAddBuff) return string.Empty;
            if (_spellbook.ClassNumber == 0)
                return "MudPlay hasn't read your character yet. Enter the game and it reads your stats "
                    + "on its own; your buffs can be added here after that.";
            bool classHasBuffs = false;
            foreach (KnownSpell s in _spellbook.ClassSpells)
                if (BuffClassifier.IsAnyBuff(s)) { classHasBuffs = true; break; }
            if (classHasBuffs && _spellbook.ObtainedCount == 0)
                return "MudPlay doesn't know which spells you have learned yet, so it has no buffs to offer. "
                    + $"Click “Read my spell list” (it types `{AppServices.Current.SpellListCommand}` in the game) "
                    + "and the buffs you know can be added here.";
            if (HasSlots) return "Every buff you have learned is already listed.";
            return classHasBuffs
                ? "None of the spells you have learned is a buff. One you learn later can be added here."
                : "Your class has no buff spells, and you carry no item that casts one, so there is nothing to set up here.";
        }
    }

    public bool HasSetupHint => SetupHint.Length > 0;

    // The learned-spell list has never been read: offer to read it.
    public bool CanReadSpellList =>
        !CanAddBuff && _spellbook.ClassNumber != 0 && _spellbook.ObtainedCount == 0
        && _spellbook.ClassSpells.Count > 0;

    [RelayCommand]
    private void ReadSpellList() => AppServices.Current.SendGameCommand(AppServices.Current.SpellListCommand);

    private void RaiseSetupHint()
    {
        OnPropertyChanged(nameof(SetupHint));
        OnPropertyChanged(nameof(HasSetupHint));
        OnPropertyChanged(nameof(CanReadSpellList));
    }

    // "Add all blesses" candidates — recomputed alongside the picker (see
    // RefreshBuffPicks). Self-only and single-target-on-self buffs, with the full
    // RemovesSpell-family recommendation treatment (see SelectSelfBlessCandidates).
    private IReadOnlyList<Game.Spells.SelfBlessPick> _selfBlessCandidates =
        Array.Empty<Game.Spells.SelfBlessPick>();

    // Whole-party buff candidates for the same button — listed alongside the self
    // picks (merged + sorted together, see AddAllBlesses) but NEVER pre-checked:
    // a whole-party cast affects other players, a bigger decision than what a bulk
    // "add my blesses" click should make on someone's behalf, so the player always
    // opts in per-buff by hand via the row's own Party toggle.
    private IReadOnlyList<Game.Spells.SelfBlessCandidate> _partyBlessCandidates =
        Array.Empty<Game.Spells.SelfBlessCandidate>();

    // Whether "Add all blesses" has anything to add.
    public bool CanAddAllBlesses => _selfBlessCandidates.Count > 0 || _partyBlessCandidates.Count > 0;

    // Whether the "Unlearned spells" report can run — the class has a learnable
    // spell roster at all (empty for non-magery classes). It reports the whole
    // class roster, not just buffs, so it keys off Available rather than the pick
    // lists above.
    public bool CanReportUnlearned => _spellbook.Available.Count > 0;

    // Whether to show the buff panel at all: a class with no party-buff spells
    // (and no existing slots) hides it entirely, rather than showing an empty
    // panel it can never use.
    public bool ShowPanel => BuffPicks.Count > 0 || Slots.Count > 0;

    // Live mana-budget readout, both sides expressed per passive regen TICK (30 s —
    // the "MP +N after ~30s" cadence, so the two numbers are directly comparable):
    //   • RequiredManaPerTick — mana to maintain everything currently checked. Each
    //     slot costs (manaCost / duration) × 30 per cast; a single-target slot
    //     multiplies by how many members (plus self) it's cast on, a whole-party
    //     slot always counts as one cast regardless of party size. Recomputed on
    //     every edit (Persist) and party-roster change (a single-target slot's cast
    //     count follows the roster). 0 when nothing is checked.
    //   • ManaGainedPerTick — the character's natural passive regen per tick
    //     (AppServices.PassiveManaRegenTick), so you can see whether the set is
    //     self-sustaining. 0 for a non-caster or before the first stat parse.
    [ObservableProperty] private double _requiredManaPerTick;
    [ObservableProperty] private double _manaGainedPerTick;

    public BuffPanelViewModel(Game.PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
        _profile = AppServices.Current.Profile;
        _spellbook = AppServices.Current.Spellbook;

        _party.Members.CollectionChanged += OnMembersChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _spellbook.Changed += OnSpellbookChanged;
        AppServices.Current.Inventory.FullInventoryParsed += OnInventoryReloaded;
        AppServices.Current.CastDirector.BuffNegated += OnBuffNegated;
        AppServices.Current.CombatProfiles.Changed += OnCombatProfilesChanged;

        Load();
    }

    // Health's mana thresholds live in the combat profile, so a Settings save or a
    // profile switch can turn the floor between a percent and an amount mid-game.
    private void OnCombatProfilesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshFloorUnit();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshFloorUnit(); });
    }

    private void RefreshFloorUnit()
    {
        OnPropertyChanged(nameof(AllBuffsFloorLabel));
        OnPropertyChanged(nameof(AllBuffsFloorMaximum));
    }

    // The casting engine marked a member as negating a buff: tint their box at once.
    private void OnBuffNegated(string _, string __)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) RefreshNegatedMarks();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshNegatedMarks(); });
    }

    private void RefreshNegatedMarks()
    {
        foreach (BuffSlotRowViewModel row in Slots) row.RefreshNegatedMarks();
    }

    // Config-list order: buffs you aim (self / single-target) first, then whole-party
    // buffs, then item ("on use") buffs. Shared with the casting engine's Default
    // priority so display grouping and Default cast order agree (BuffPriorityOrder).
    private static int SlotCategory(BuffSlotRowViewModel r) =>
        BuffPriorityOrder.Category(r.IsItemCast, r.IsWholeParty);

    // Add a row at the END of its category block, keeping the groups contiguous and
    // order-within-group stable (Add all blesses adds level-sorted, so that survives).
    private void InsertSorted(BuffSlotRowViewModel row)
    {
        int cat = SlotCategory(row);
        int idx = 0;
        while (idx < Slots.Count && SlotCategory(Slots[idx]) <= cat) idx++;
        Slots.Insert(idx, row);
    }

    // After an edit that changed a row's spell — and so possibly its category (a self
    // buff swapped for a whole-party one, say) — slide it back into the right block.
    // No-op when it's already ordered, so an ordinary edit doesn't churn the container.
    private void ResortRow(BuffSlotRowViewModel row)
    {
        int cur = Slots.IndexOf(row);
        if (cur < 0) return;
        int cat = SlotCategory(row);
        bool ordered = (cur == 0 || SlotCategory(Slots[cur - 1]) <= cat)
                    && (cur == Slots.Count - 1 || SlotCategory(Slots[cur + 1]) >= cat);
        if (ordered) return;
        Slots.RemoveAt(cur);
        InsertSorted(row);
    }

    // ----- manual reorder + priority toggle --------------------------

    // Refresh each row's ▲/▼ enable flags to its position. Called after every
    // structural change to Slots (Load / add / remove / move).
    private void Renumber()
    {
        for (int i = 0; i < Slots.Count; i++)
        {
            Slots[i].CanMoveUp = i > 0;
            Slots[i].CanMoveDown = i < Slots.Count - 1;
        }
    }

    // First manual move: switch to custom layout and bake the CURRENT display order
    // into the stored list so display and DTO are 1:1 from here on (every later move
    // keeps them aligned). No-op once already manual.
    private void EnterManualOrder()
    {
        if (_settings.ManualOrder) return;
        List<BuffSlot> ordered = Slots.Select(r => r.Dto).ToList();
        _settings.Slots.Clear();
        _settings.Slots.AddRange(ordered);
        _settings.ManualOrder = true;
        OnPropertyChanged(nameof(HasManualOrder));
    }

    // Move a row from → to in both the display collection and the stored list (kept
    // 1:1 once manual). Bounds-guarded no-op otherwise.
    private void MoveTo(int from, int to)
    {
        if (from < 0 || from >= Slots.Count) return;
        if (to < 0 || to >= Slots.Count) return;
        if (from == to) return;
        EnterManualOrder();
        Slots.Move(from, to);
        BuffSlot dto = _settings.Slots[from];
        _settings.Slots.RemoveAt(from);
        _settings.Slots.Insert(to, dto);
        Renumber();
        Persist();
    }

    // Drag entry point: move a row to a FINAL insert index (0..Count) — the drop
    // handler computes it from where the insertion line landed. Converts the insert
    // index to a Move target, accounting for the removal shift when moving downward.
    public void MoveRowToIndex(BuffSlotRowViewModel row, int insertIndex)
    {
        int from = Slots.IndexOf(row);
        if (from < 0) return;
        int to = from < insertIndex ? insertIndex - 1 : insertIndex;
        to = System.Math.Clamp(to, 0, Slots.Count - 1);
        MoveTo(from, to);
    }

    [RelayCommand]
    private void MoveBuffUp(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        int i = Slots.IndexOf(row);
        MoveTo(i, i - 1);
    }

    [RelayCommand]
    private void MoveBuffDown(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        int i = Slots.IndexOf(row);
        MoveTo(i, i + 1);
    }

    // Flip the cast-priority mode (Default by-type ⟷ top-to-bottom). Purely a
    // priority choice — the visual row arrangement is untouched.
    [RelayCommand]
    private void TogglePriorityMode()
    {
        _settings.PriorityTopDown = !_settings.PriorityTopDown;
        OnPropertyChanged(nameof(IsPriorityTopDown));
        OnPropertyChanged(nameof(PriorityModeLabel));
        Persist();
    }

    // Return the layout to the automatic category grouping (self → whole-party →
    // item). Priority mode is left as-is.
    [RelayCommand]
    private void ResetBuffOrder()
    {
        _settings.ManualOrder = false;
        List<BuffSlotRowViewModel> rows = Slots.ToList();
        Slots.Clear();
        foreach (BuffSlotRowViewModel row in rows) InsertSorted(row);
        _settings.Slots.Clear();
        _settings.Slots.AddRange(Slots.Select(r => r.Dto));
        Renumber();
        OnPropertyChanged(nameof(HasManualOrder));
        Persist();
    }

    // A full `i` dump changes which cast-items we own, so the owned-item gate on
    // weapon buffs (see CanUseCastItem) needs the pick / Add-all lists rebuilt — an
    // item we just acquired becomes offerable, a dropped one drops out. Marshalled:
    // the inventory parse runs on the line pump and RefreshBuffPicks touches observable
    // state. Per-line pickups aren't followed (that would churn every loot); an `i`
    // dump is the refresh point, same as the rest of the app treats the snapshot.
    private void OnInventoryReloaded()
    {
        if (_disposed) return;
        // Worn +ManaRgn% changes the "mana gained" side of the budget readout, so
        // refresh that too — not just the owned-item pick gate.
        if (Dispatcher.UIThread.CheckAccess()) { RefreshBuffPicks(); RefreshManaUpkeep(); }
        else Dispatcher.UIThread.Post(() => { if (!_disposed) { RefreshBuffPicks(); RefreshManaUpkeep(); } });
    }

    private void OnProfileLoaded(CharacterProfile _) => Load();
    private void OnSpellbookChanged()
    {
        RefreshBuffPicks();
        // A row's "unlearned" chip (see ResolveLearned) can flip live — training the
        // spell mid-session, or a reroll losing it — so every row needs to re-pull it.
        foreach (BuffSlotRowViewModel row in Slots) row.Refresh();
        // Level drives both sides of the budget readout (spell duration, passive
        // regen), and a reseed rides in on the same event.
        RefreshManaUpkeep();
    }
    private void OnMembersChanged(object? _, NotifyCollectionChangedEventArgs __) => RefreshMemberTargets();

    private void Load()
    {
        // Ensure the profile has a PartyBuffs bag so edits persist somewhere.
        _settings = _profile.Current?.PartyBuffs ?? new BuffSettings();
        if (_profile.Current is { } p) p.PartyBuffs = _settings;

        // Drop any slot with no spell. There's no empty-slot workflow — every slot
        // is created with a chosen buff via the Add dialog — so a blank slot is
        // inert junk, and left in place it would force the panel open for a class
        // that has no party-buff spells at all.
        int pruned = _settings.Slots.RemoveAll(s => string.IsNullOrWhiteSpace(s.Spell));

        // A master-disabled row must not retain a checked Solo option: it is inert
        // in the engine and makes the editor look like the spell is still enabled.
        int normalizedSoloOptions = 0;
        foreach (BuffSlot slot in _settings.Slots)
            if (!slot.WholePartyOn && slot.CastSolo)
            {
                slot.CastSolo = false;
                normalizedSoloOptions++;
            }

        Slots.Clear();
        foreach (BuffSlot dto in _settings.Slots)
        {
            BuffSlotRowViewModel row = MakeRow(dto);
            // Manual layout shows the stored order verbatim; default auto-groups.
            if (_settings.ManualOrder) Slots.Add(row); else InsertSorted(row);
        }
        Renumber();

        // Already the stored value, so the change handler saves nothing.
        AllBuffsFloor = _settings.BlessIfAboveMa;
        RefreshFloorUnit();

        RefreshBuffPicks();
        RefreshMemberTargets();
        RefreshOverwriteWarnings();
        OnPropertyChanged(nameof(HasSlots));
        RaiseSetupHint();
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(HasManualOrder));
        OnPropertyChanged(nameof(IsPriorityTopDown));
        OnPropertyChanged(nameof(PriorityModeLabel));

        if (pruned > 0 || normalizedSoloOptions > 0) Persist();
    }

    private BuffSlotRowViewModel MakeRow(BuffSlot dto) =>
        new(dto, ResolveScope, ResolveName, ResolveOverwrite, Persist, OnSelfCastActivated, ResolveLearned);

    // Live mutual exclusion: the moment a row's Self box is CHECKED, turn off any
    // OTHER row's Self box for a spell it mutually removes (or is removed by) via
    // RemovesSpell — e.g. checking "blood ritual" unchecks "zeal", and checking
    // "zeal" back later unchecks "blood ritual" in turn (both directions fall out
    // of only ever reacting to a fresh Self-check, never a Self-uncheck, so there's
    // no feedback loop). Scoped to Self-vs-Self only — party/whole-party targeting
    // already has its own explicit checklist and isn't force-exclusive here.
    private void OnSelfCastActivated(BuffSlotRowViewModel activated)
    {
        if (string.IsNullOrWhiteSpace(activated.Spell)) return;
        if (ResolveSpellOrItem(activated.Spell.Trim()) is not { } activatedSpell) return;
        HashSet<int> removes = Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(activatedSpell.Formula);
        foreach (BuffSlotRowViewModel other in Slots)
        {
            if (ReferenceEquals(other, activated) || !other.CastOnSelf) continue;
            if (string.IsNullOrWhiteSpace(other.Spell)) continue;
            if (ResolveSpellOrItem(other.Spell.Trim()) is not { } otherSpell) continue;
            bool conflict = removes.Contains(otherSpell.Number)
                || Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(otherSpell.Formula).Contains(activatedSpell.Number);
            if (conflict) other.CastOnSelf = false;
        }
    }

    // Looks up dto's cast code in the last-computed conflict pairing (see
    // RefreshOverwriteWarnings) — both directions, since the player needs to see
    // both "this gets removed" and "this removes something else" cause/effect.
    private (string? RemovedBy, string? Removes) ResolveOverwrite(BuffSlot dto) =>
        string.IsNullOrWhiteSpace(dto.Spell)
            ? (null, null)
            : Game.Spells.BuffConflictAnalyzer.Resolve(_overwritePairs, dto.Spell.Trim());

    // Recompute the slot-vs-slot conflict pairing and push it to every row — a
    // conflict is a property of a PAIR, so any add/edit/remove/targeting change
    // anywhere in the panel can change another row's warning, not just its own.
    private void RefreshOverwriteWarnings()
    {
        _overwritePairs = AppServices.Current.BuffSlotOverwritePairs();
        foreach (BuffSlotRowViewModel row in Slots) row.RefreshOverwriteWarning();
    }

    // Resolve a slot's targeting scope live from the active set. A #item-cast slot is
    // whole-party or self-only per the item (both no-target kinds are offered — see
    // EligibleBuffs); a spell splits self-only / others-only / single-target /
    // whole-party by its Targets code. An unresolved code defaults to self-only (a
    // plain "cast on me" row).
    private BuffSlotScope ResolveScope(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return BuffSlotScope.SelfOnly;
        string c = code.Trim();
        if (ItemCastToken.IsToken(c))
            return _spellbook.IsTokenWholeParty(c) ? BuffSlotScope.WholeParty : BuffSlotScope.SelfOnly;
        if (_spellbook.FindByCastCode(c) is not { } s) return BuffSlotScope.SelfOnly;
        if (BuffClassifier.IsWholeParty(s.Targets)) return BuffSlotScope.WholeParty;
        if (BuffClassifier.IsSingleTargetBuff(s.Targets)) return BuffSlotScope.SingleTarget;
        if (BuffClassifier.IsOthersOnlyBuff(s.Targets)) return BuffSlotScope.OthersOnly;
        return BuffSlotScope.SelfOnly;
    }

    // The buff's display name (for the compact row header): a spell's name, an item's
    // name for a #item-cast slot, or the raw code when neither resolves.
    private string ResolveName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "(no spell)";
        string c = code.Trim();
        if (ItemCastToken.ItemName(c) is { } item) return item;
        return _spellbook.FindByCastCode(c) is { } s ? s.Name : c;
    }

    // Whether the character has actually learned this slot's spell — drives the
    // row's "unlearned" chip the way the read-only Buff Watchdog timer bars do.
    // Add all blesses only ever adds LEARNED spells, so a fresh bulk-add row is
    // always learned; this still flags the edge case of a slotted spell that later
    // reads unlearned (a data-set renumber, or a hand-typed unknown code). A
    // #item-cast token always reads learned — a carried item is available by
    // definition, there's no separate "train" step for it.
    private bool ResolveLearned(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        string c = code.Trim();
        if (ItemCastToken.IsToken(c)) return true;
        return _spellbook.FindByCastCode(c) is { } s && _spellbook.IsObtained(s.Number);
    }

    // Every buff the character can slot, de-duplicated by cast code: learned buff
    // spells the character can maintain on themselves, a member, or the whole party
    // (self / single-target / whole-party scopes), plus cast-on-use items whose
    // spell needs no target parameter — whole-party (blankets everyone in one use)
    // and self-only (a wielded item like a bless-casting crozier, which always
    // lands on the wielder). A single-target (Targets 2) item is excluded: `use
    // <item>` can't be aimed at a specific party member. Carries the learn-level so
    // the dropdown can label and order by it. Shared by the pick count (AllBuffPicks
    // → BuffPicks) and the Add-buff dropdown (BuildPickOptions).
    private IEnumerable<(string Code, string Name, int Level)> EligibleBuffs()
    {
        IEnumerable<(string Code, string Name, int Level)> spells = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s) && _spellbook.IsObtained(s.Number))
            .Select(s => (s.Short.Trim(), s.Name, s.ReqLevel));
        IEnumerable<(string Code, string Name, int Level)> items = _spellbook.GetWholePartyCastItems()
            .Concat(_spellbook.GetSelfCastItems())
            .Concat(_spellbook.GetDrawCastItems())
            .Where(CanUseCastItem)
            .Select(ci => (
                ItemCastToken.Format(ci.ItemName),
                string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})",
                ci.MinLevel));
        return spells.Concat(items).DistinctBy(e => e.Code, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<SpellPick> AllBuffPicks() =>
        EligibleBuffs().Select(e => new SpellPick(e.Code, e.Name));

    // A class cast-item (a weapon/staff "on use" buff) is only worth offering if the
    // character can actually use it right now: it must meet the item's level and be in
    // the pack — carried or worn. Each gate is SKIPPED while its source is still unknown
    // (no `stat` parsed yet for level, no `i` dump yet for the pack), so the list
    // degrades to "unfiltered" rather than empty until the data lands (and refreshes on
    // the next full `i` — see OnInventoryReloaded). Spells have no such gate: a learned
    // spell needs no item and its level is already baked into being learned.
    private static bool CanUseCastItem(ClassCastItem ci)
    {
        AppServices svc = AppServices.Current;
        if (svc.Stats.HasParsed && ci.MinLevel > 0 && svc.PlayerStats.Level < ci.MinLevel) return false;
        if (svc.Inventory.IsLoaded && !svc.Inventory.Snapshot.Has(ci.ItemName)) return false;
        return true;
    }

    // The Add-buff dropdown's options: every eligible buff, each labelled with the
    // level it's learned at ("bless (Lvl 2)"). `disabled` are the cast codes already
    // held by another slot — they stay in the list but come back Enabled=false so
    // they read as taken rather than vanishing. Ordered by level then name.
    private List<BuffPickOption> BuildPickOptions(HashSet<string> disabled)
    {
        static string Label(string name, int level) => level > 0 ? $"{name} (Lvl {level})" : name;
        return EligibleBuffs()
            .OrderBy(e => e.Level).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new BuffPickOption(e.Code, Label(e.Name, e.Level), !disabled.Contains(e.Code)))
            .ToList();
    }

    // The cast codes already held by a slot (a spell can't be slotted twice).
    private HashSet<string> SlottedSpells() =>
        new(_settings.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Spell)).Select(s => s.Spell!.Trim()),
            StringComparer.OrdinalIgnoreCase);

    private void RefreshBuffPicks()
    {
        HashSet<string> slotted = SlottedSpells();
        BuffPicks = AllBuffPicks().Where(p => !slotted.Contains(p.Short)).ToList();
        RefreshSelfBlessCandidates(slotted);
    }

    // Every buff the character has actually LEARNED that can land on itself (self-
    // only Targets, or single-target Targets castable on self) and — when we know
    // it — is alignment-eligible for. Not level-gated (a learned buff you've
    // out-levelled still lists), but obtained-gated: only spells in your spellbook,
    // never the class's untrained roster. Reduced to the BuffConflictAnalyzer pick:
    // every learned candidate gets listed, but only the highest-ReqLevel member of
    // each RemovesSpell family (e.g. greater zeal over zeal) comes pre-checked.
    //
    // Alignment matters here specifically because a class's learnable list often
    // carries BOTH sides of a holy/unholy pair (e.g. "holy armour" needs non-evil,
    // "unholy armour" needs evil — Paradigm data, report paradigm-20260906-*): the
    // character can have learned both over their career even though only one is
    // castable right now, and each removes the other, so without this filter the
    // RemovesSpell tie-break has no way to know which of the two is even usable.
    private void RefreshSelfBlessCandidates(HashSet<string> slotted)
    {
        // Our own alignment (AlignmentTracker) — the same source the Equipment
        // Manager trusts. Null until a `who` has shown us; IsAlignmentEligible
        // treats that as "don't know, don't filter".
        Game.RealmType realm = AppServices.Current.GameData.ActiveRealm;
        AlignmentBucket? alignment = ItemEquipFilter.GearBucketForWord(
            AppServices.Current.Alignment.SelfAlignment, realm);
        EvilPointRange? evilPoints = AppServices.Current.Alignment.SelfEvilPoints(realm);

        List<Game.Spells.SelfBlessCandidate> pool = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s)
                && !BuffClassifier.IsWholeParty(s.Targets)
                && _spellbook.IsObtained(s.Number)
                && BuffClassifier.IsAlignmentEligible(s.Formula, alignment, evilPoints)
                && !slotted.Contains(s.Short.Trim()))
            .Select(s => new Game.Spells.SelfBlessCandidate(
                s.Short.Trim(), s.Name, s.Number, s.ReqLevel, s.Formula.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula),
                IsObtained: _spellbook.IsObtained(s.Number)))
            .ToList();

        // Self-cast items (a wielded crozier/staff whose "use" always lands on the
        // wielder) get the same roster treatment as spells — see GetSelfCastItems.
        // Always "obtained": a class-usable cast item has no separate "train" step.
        foreach (Game.Spells.ClassCastItem ci in _spellbook.GetSelfCastItems())
        {
            string code = ItemCastToken.Format(ci.ItemName);
            if (slotted.Contains(code)) continue;
            if (!CanUseCastItem(ci)) continue;   // level + owned-item gate
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) continue;
            if (!BuffClassifier.IsAlignmentEligible(formula, alignment, evilPoints)) continue;
            string name = string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})";
            pool.Add(new Game.Spells.SelfBlessCandidate(
                code, name, ci.SpellNumber, ci.MinLevel, ci.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(formula), IsObtained: true));
        }

        // Whole-party candidates: same "list everything eligible" roster as self,
        // but never auto-checked — see the field doc on _partyBlessCandidates.
        List<Game.Spells.SelfBlessCandidate> partyPool = _spellbook.Available
            .Where(s => BuffClassifier.IsAnyBuff(s)
                && BuffClassifier.IsWholeParty(s.Targets)
                && _spellbook.IsObtained(s.Number)
                && BuffClassifier.IsAlignmentEligible(s.Formula, alignment, evilPoints)
                && !slotted.Contains(s.Short.Trim()))
            .Select(s => new Game.Spells.SelfBlessCandidate(
                s.Short.Trim(), s.Name, s.Number, s.ReqLevel, s.Formula.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(s.Formula),
                IsObtained: _spellbook.IsObtained(s.Number)))
            .ToList();
        foreach (Game.Spells.ClassCastItem ci in _spellbook.GetWholePartyCastItems())
        {
            string code = ItemCastToken.Format(ci.ItemName);
            if (slotted.Contains(code)) continue;
            if (!CanUseCastItem(ci)) continue;   // level + owned-item gate
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) continue;
            if (!BuffClassifier.IsAlignmentEligible(formula, alignment, evilPoints)) continue;
            string name = string.IsNullOrWhiteSpace(ci.SpellName) ? ci.ItemName : $"{ci.ItemName} ({ci.SpellName})";
            partyPool.Add(new Game.Spells.SelfBlessCandidate(
                code, name, ci.SpellNumber, ci.MinLevel, ci.ManaCost,
                Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(formula), IsObtained: true));
        }
        _partyBlessCandidates = partyPool
            .OrderBy(c => c.ReqLevel).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<Game.Spells.ExistingBuffSlot> existing = new();
        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            if (ResolveSpellOrItem(dto.Spell.Trim()) is not { } r) continue;
            Game.Spells.BuffAffectSet affect = Game.Spells.BuffAffectSet.From(
                BuffClassifier.IsWholeParty(r.Targets), dto.WholePartyOn, dto.CastOnSelf, dto.AllMembers, dto.Targets);
            existing.Add(new Game.Spells.ExistingBuffSlot(
                r.Number, affect, Game.Spells.BuffConflictAnalyzer.RemovedSpellNumbers(r.Formula)));
        }

        _selfBlessCandidates = Game.Spells.BuffConflictAnalyzer.SelectSelfBlessCandidates(pool, existing);
        OnPropertyChanged(nameof(CanAddAllBlesses));
        OnPropertyChanged(nameof(CanReportUnlearned));
        RaiseSetupHint();
    }

    // Resolve a slot's cast code to its underlying spell identity — a learnable
    // spell by cast code, or a #item-cast token's underlying cast spell by number
    // — so a slot's scope, mana cost, and RemovesSpell conflicts are all read the
    // same way whether it's a trained spell or a wielded item (a configured
    // item-cast slot was previously invisible to all three, so a self-bless pick
    // could get recommended even though it'd conflict with the item already
    // active, and the item's own mana draw never counted toward the upkeep total).
    // Item Targets isn't directly exposed by ClassCastItem, so it's inferred from
    // IsTokenWholeParty (13 = whole-party) else 0 (self-only) — matching
    // ResolveScope's existing fallback: a single-target item is never offered as a
    // slot in the first place (see AllBuffPicks), so it never reaches here.
    private (int Number, Game.Spells.SpellFormulaInput Formula, int Targets)? ResolveSpellOrItem(string code)
    {
        if (ItemCastToken.IsToken(code))
        {
            if (!ItemCastToken.TryResolve(code, _spellbook.GetCastItems(), out Game.Spells.ClassCastItem ci)) return null;
            if (_spellbook.GetFormulaByNumber(ci.SpellNumber) is not { } formula) return null;
            return (ci.SpellNumber, formula, _spellbook.IsTokenWholeParty(code) ? 13 : 0);
        }
        if (_spellbook.FindByCastCode(code) is not { } s) return null;
        return (s.Number, s.Formula, s.Targets);
    }

    // Rebuild every row's member checklist — and the shared column headers — from
    // the current (non-self) roster.
    private void RefreshMemberTargets()
    {
        var members = CurrentMembers();
        Members.Clear();
        foreach ((string _, string given) in members) Members.Add(Capitalise(given));
        ShowPartyColumns = members.Count > 0;
        foreach (BuffSlotRowViewModel row in Slots)
            row.RebuildMemberTargets(members);
        RefreshManaUpkeep();   // a single-target slot's cast count follows the roster
    }

    // Sum every currently-active slot's mana-per-second cost (see
    // BuffManaUpkeepCalculator) into the live "required to maintain" readout. A
    // #item-cast slot counts too when its underlying spell actually costs mana
    // (ClassCastItem.ManaCost / CostsMana — most charge wands are free, but a
    // weapon like a bless-casting crozier draws from the same pool as a spell).
    private void RefreshManaUpkeep()
    {
        List<(string Display, string Given)> members = CurrentMembers();
        bool hasParty = members.Count > 0;
        List<Game.Spells.BuffManaUpkeepCalculator.SlotUpkeep> upkeep = new();

        // A Paradigm-suppressed loser is never maintained (cost 0); a STOCK collision-
        // order loser is re-cast on its remover's cadence, so its effective duration is
        // capped at the remover's. Both are empty off their respective realm.
        IReadOnlyDictionary<string, string> suppressed = AppServices.Current.SuppressedBuffCoverage();
        IReadOnlyDictionary<string, string> collisionOrder = AppServices.Current.CollisionOrderConstraints();
        double DurationSecondsOf(string c) =>
            ResolveSpellOrItem(c) is { } rr
                ? Game.Spells.SpellCalculator.Duration(rr.Formula, _spellbook.Level)
                    * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock
                : 0;

        foreach (BuffSlot dto in _settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(dto.Spell)) continue;
            string code = dto.Spell.Trim();
            if (ResolveSpellOrItem(code) is not { } r) continue;

            // Suppressed (Paradigm): the winner keeps stripping it, so we never cast it —
            // it adds nothing to the mana budget.
            if (suppressed.ContainsKey(code)) continue;

            long manaCost = Game.Spells.SpellCalculator.ManaCost(r.Formula);
            double durationSeconds = Game.Spells.SpellCalculator.Duration(r.Formula, _spellbook.Level)
                * Game.Spells.SpellCalculator.SpellRoundSecondsWallClock;

            // Stock collision-order loser: each remover refresh strips and re-casts it, so
            // budget it at the shorter of its own and the remover's duration.
            if (collisionOrder.TryGetValue(code, out string? removerCode))
                durationSeconds = Game.Spells.BuffManaUpkeepCalculator.EffectiveMaintenanceSeconds(
                    durationSeconds, DurationSecondsOf(removerCode));

            int casts;
            if (BuffClassifier.IsWholeParty(r.Targets))
                casts = dto.WholePartyOn && (hasParty || dto.CastSolo) ? 1 : 0;
            else if (BuffClassifier.IsSingleTargetBuff(r.Targets) || BuffClassifier.IsOthersOnlyBuff(r.Targets))
            {
                int memberCasts = dto.AllMembers
                    ? members.Count
                    : dto.Targets.Count(t => members.Any(m => string.Equals(m.Given, t, StringComparison.OrdinalIgnoreCase)));
                casts = (dto.CastOnSelf && BuffClassifier.IsSingleTargetBuff(r.Targets) ? 1 : 0) + memberCasts;
            }
            else
                casts = dto.CastOnSelf ? 1 : 0;

            if (casts == 0) continue;
            upkeep.Add(new Game.Spells.BuffManaUpkeepCalculator.SlotUpkeep(manaCost, durationSeconds, casts));
        }

        double perSecond = Game.Spells.BuffManaUpkeepCalculator.TotalManaPerSecond(upkeep);
        RequiredManaPerTick = perSecond * ManaRegenBreakpointCalculator.PassiveTickSeconds;
        ManaGainedPerTick = AppServices.Current.PassiveManaRegenTick() ?? 0;
    }

    private List<(string Display, string Given)> CurrentMembers() =>
        _party.Members
            .Where(m => !m.IsSelf)
            .Select(m => (Display: m.Name, Given: GivenLower(m.Name)))
            .ToList();

    private static string GivenLower(string name) =>
        (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();

    private static string Capitalise(string given) =>
        given.Length == 0 ? given : char.ToUpperInvariant(given[0]) + given[1..];

    // True when a cast code resolves to a spell that provides light (offers the "only
    // when dark" condition) / is a mana-regen roll spell (offers the reroll config).
    private static bool IsLightSpell(string? code) => AppServices.Current.RoomLightSpell.IsLightCastCode(code);

    private bool IsRollSpell(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && _spellbook.FindByCastCode(code!.Trim()) is { } s
        && Game.Spells.ManaRegenReroller.IsRollSpell(s.Formula);

    // Stock realm has no `abil 145`, so reroll quality is judged from the observed
    // mana tick — drives the dialog's realm-aware reroll wording.
    private static bool IsStockRealm =>
        AppServices.Current.GameData.ActiveRealm != Game.RealmType.ParaMud;

    // Apply the dialog's result onto a slot DTO (shared by add + edit).
    internal static void ApplyResult(BuffSlot dto, AddBuffResult r)
    {
        // A mark says who negated THIS spell; another spell starts with none.
        if (!string.Equals(dto.Spell?.Trim(), r.Spell?.Trim(), StringComparison.OrdinalIgnoreCase))
            dto.NegatedTargets.Clear();
        dto.Spell = r.Spell;
        dto.RecastMarginSec = r.RecastMarginSec;
        dto.OnlyWhenHpFull = r.OnlyWhenHpFull;
        dto.OnlyWhenMaFull = r.OnlyWhenMaFull;
        dto.OnlyWhenDark = r.OnlyWhenDark;
        dto.CastBeforeRestingForMana = r.CastBeforeRestingForMana;
        dto.RerollCount = r.RerollCount;
        dto.RerollThreshold = r.RerollThreshold;
        dto.RerollThresholdIsRoll = true;   // the dialog edits the rolled value on both realms
        dto.RerollInfinite = r.RerollInfinite;
        dto.RejectedOutcomes = r.RejectedOutcomes?.ToList() ?? new List<int>();
        dto.BlessIfAboveMa = r.BlessIfAboveMa;
        dto.BlessWhileResting = r.BlessWhileResting;
        dto.BlessDuringCombat = r.BlessDuringCombat;
        dto.PriorityBuff = r.PriorityBuff;
    }

    // The buffs a draw item (a deck of cards) can deal, for the dialog's tick boxes.
    // None for an item that can't redraw: there is nothing to pick when every card
    // it deals has to be kept.
    private IReadOnlyList<CastOutcome> OutcomesOf(string? code) =>
        ItemCastToken.TryResolve(code, _spellbook.GetCastItems(), out ClassCastItem item)
        && item is { CanRedraw: true, Outcomes: { } outcomes }
            ? outcomes
            : Array.Empty<CastOutcome>();

    private bool IsNoRedrawDraw(string? code) =>
        ItemCastToken.TryResolve(code, _spellbook.GetCastItems(), out ClassCastItem item)
        && item is { IsDraw: true, CanRedraw: false };

    // A slot's reroll aggression as (threshold, cap), so an edit can tell whether it
    // LOOSENED (higher threshold or bigger cap → re-evaluate an already-active roll). A
    // null threshold (rerolling off) sorts below everything; infinite sorts above any
    // finite cap.
    private static (int Threshold, int Cap) RerollAggression(BuffSlot s) =>
        (s.RerollThreshold ?? int.MinValue, s.RerollInfinite ? int.MaxValue : s.RerollCount);

    // Open the Add-buff dialog (spell + recast + conditions). On OK, add the slot;
    // targeting (self / all-members / member checklist) is then chosen in the row.
    [RelayCommand]
    private async System.Threading.Tasks.Task AddBuff()
    {
        AddBuffDialogViewModel dlg = new(BuildPickOptions(SlottedSpells()), IsLightSpell, IsRollSpell,
            IsStockRealm, AppServices.Current.ManaRegenTickSteps,
            rollRange: AppServices.Current.ManaRegenRollRange, outcomesOf: OutcomesOf,
            isNoRedrawDraw: IsNoRedrawDraw, manaFloorIsAbsolute: AppServices.Current.ManaThresholdsAreAbsolute,
            maxMana: AppServices.Current.PlayerState.MaxMa)
        {
            BlessIfAboveMa = _settings.BlessIfAboveMa,
        };
        AddBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddBuffDialogViewModel, AddBuffResult>(dlg);
        if (result is not { } r) return;

        BuffSlot dto = new();
        ApplyResult(dto, r);
        // A self-only buff, a room-light "only when dark" buff, and a mana-regen
        // "cast before resting" buff all act on us — default them to cast-on-self so
        // the fresh slot isn't inert until the user ticks "self".
        dto.CastOnSelf = ResolveScope(r.Spell) == BuffSlotScope.SelfOnly
                         || r.OnlyWhenDark || r.CastBeforeRestingForMana;
        _settings.Slots.Add(dto);
        GuideActions.Report(GuideActions.BuffAdded);
        BuffSlotRowViewModel row = MakeRow(dto);
        // Manual layout appends new buffs at the bottom; default sorts into type.
        if (_settings.ManualOrder) Slots.Add(row); else InsertSorted(row);
        row.RebuildMemberTargets(CurrentMembers());
        Renumber();
        RefreshBuffPicks();   // the just-slotted spell drops out of the picker
        OnPropertyChanged(nameof(HasSlots));
        RaiseSetupHint();
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
        // A freshly-added roll spell with rerolling on can re-evaluate a roll already up
        // (same intent as the edit path).
        if (IsRollSpell(dto.Spell) && (dto.RerollInfinite || dto.RerollCount > 0))
            AppServices.Current.ReconsiderManaRegenRerollAfterConfigChange();
    }

    // Bulk-add EVERY bless candidate — self and whole-party alike (see
    // RefreshSelfBlessCandidates) — as a new slot in one shot, no dialog, merged
    // and inserted in one level-sorted order. Every learned, alignment-eligible
    // buff gets a row so the whole roster is browsable. Self picks: only the
    // Recommended one per RemovesSpell family comes pre-checked (Self ticked) —
    // the rest are added unchecked so the player can swap a different family
    // member in by hand (unchecking one and checking another triggers the same
    // live mutual-exclusion as any other row — see OnSelfCastActivated). Party
    // picks are ALWAYS added unchecked (Party off) — a whole-party cast
    // affects other players, so the player opts into each one explicitly.
    [RelayCommand]
    private void AddAllBlesses()
    {
        if (_selfBlessCandidates.Count == 0 && _partyBlessCandidates.Count == 0) return;
        List<(string Display, string Given)> members = CurrentMembers();

        List<(BuffSlot Dto, int ReqLevel, string Name)> toAdd = new();
        // A buff only another player can take is added with self off: its members
        // are ticked in the row.
        foreach (Game.Spells.SelfBlessPick pick in _selfBlessCandidates)
            toAdd.Add((new BuffSlot
                {
                    Spell = pick.Candidate.CastCode,
                    CastOnSelf = pick.Recommended && ResolveScope(pick.Candidate.CastCode) != BuffSlotScope.OthersOnly,
                    BlessIfAboveMa = _settings.BlessIfAboveMa,
                },
                pick.Candidate.ReqLevel, pick.Candidate.Name));
        foreach (Game.Spells.SelfBlessCandidate cand in _partyBlessCandidates)
            toAdd.Add((new BuffSlot { Spell = cand.CastCode, WholePartyOn = false, CastSolo = false, BlessIfAboveMa = _settings.BlessIfAboveMa },
                cand.ReqLevel, cand.Name));

        foreach ((BuffSlot dto, _, _) in toAdd.OrderBy(x => x.ReqLevel).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Slots.Add(dto);
            BuffSlotRowViewModel row = MakeRow(dto);
            if (_settings.ManualOrder) Slots.Add(row); else InsertSorted(row);
            row.RebuildMemberTargets(members);
        }
        Renumber();
        RefreshBuffPicks();   // drops the just-slotted spells from both pickers
        OnPropertyChanged(nameof(HasSlots));
        RaiseSetupHint();
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // "Unlearned spells" — dumps into the terminal (yellow "[…]" notices, the same
    // cadence the quest-availability announcer uses) every class spell the character
    // hasn't learned yet that's within reach: castable at the current level, plus
    // everything up to five levels ahead. Each line reads
    // "[<spell> - Unlearned, Requires Level XX]", low levels first. Read-only — it
    // reports the spellbook, never touches config. Reports on the FULL class roster
    // (all spells, not just buffs), so it reads Available directly.
    [RelayCommand]
    private void ReportUnlearnedSpells()
    {
        AppServices svc = AppServices.Current;
        // No stat screen yet → no level to gate the 5-level look-ahead against.
        if (!svc.Stats.HasParsed)
        {
            svc.WriteTerminalNotice("[Unlearned spells: read your stats first so I know your level]");
            return;
        }
        int ceiling = svc.PlayerStats.Level + 5;

        List<Game.Spells.KnownSpell> unlearned = _spellbook.Available
            .Where(s => !_spellbook.IsObtained(s.Number) && s.ReqLevel <= ceiling)
            .OrderBy(s => s.ReqLevel)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        svc.Log.Info("Spells",
            $"Unlearned-spells report: {unlearned.Count} within reach (level {svc.PlayerStats.Level}, ceiling {ceiling}).");
        if (unlearned.Count == 0)
        {
            svc.WriteTerminalNotice("[No unlearned spells within 5 levels]");
            return;
        }
        foreach (Game.Spells.KnownSpell s in unlearned)
            svc.WriteTerminalNotice($"[{s.Name} - Unlearned, Requires Level {s.ReqLevel}]");
    }

    // Edit an existing slot — reopens the dialog pre-filled, so the buff / recast /
    // conditions can change without a delete + re-add. The picker offers this slot's
    // own spell plus any not held by another slot.
    [RelayCommand]
    private async System.Threading.Tasks.Task EditBuff(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        HashSet<string> others = SlottedSpells();
        others.Remove((row.Spell ?? string.Empty).Trim());
        var options = BuildPickOptions(others);
        BuffSlot d = row.Dto;
        // A Stock threshold saved as a mana tick shows as the roll it stands for.
        AppServices.Current.ConvertLegacyStockRerollThreshold(d);
        AddBuffResult initial = new(
            d.Spell ?? string.Empty, d.RecastMarginSec, d.OnlyWhenHpFull, d.OnlyWhenMaFull,
            d.OnlyWhenDark, d.CastBeforeRestingForMana, d.RerollCount, d.RerollThreshold, d.RerollInfinite,
            d.RejectedOutcomes.ToList(), d.BlessIfAboveMa, d.BlessWhileResting, d.BlessDuringCombat,
            d.PriorityBuff);
        AddBuffDialogViewModel dlg = new(
            options, IsLightSpell, IsRollSpell,
            IsStockRealm, AppServices.Current.ManaRegenTickSteps, initial,
            rollRange: AppServices.Current.ManaRegenRollRange, outcomesOf: OutcomesOf,
            isNoRedrawDraw: IsNoRedrawDraw, manaFloorIsAbsolute: AppServices.Current.ManaThresholdsAreAbsolute,
            maxMana: AppServices.Current.PlayerState.MaxMa);
        AddBuffResult? result = await AppServices.Current.Dialogs
            .OpenWindowAsync<AddBuffDialogViewModel, AddBuffResult>(dlg);
        if (result is not { } r) return;

        (int Threshold, int Cap) beforeReroll = RerollAggression(row.Dto);
        ApplyResult(row.Dto, r);
        RefreshBuffPicks();   // a changed spell frees/consumes picker entries
        row.Refresh();   // re-derive header + whole-party/single-target after a spell change
        row.RebuildMemberTargets(CurrentMembers());
        // Manual layout keeps the user's position; default re-sorts by the (possibly
        // changed) category.
        if (!_settings.ManualOrder) { ResortRow(row); Renumber(); }
        Persist();
        // If the edit LOOSENED rerolling for a roll spell (higher threshold / bigger cap /
        // infinite on), re-evaluate a roll that's already up so the change acts on it now
        // instead of only the next cast (report paradigm-20260909-113655).
        (int Threshold, int Cap) afterReroll = RerollAggression(row.Dto);
        if (IsRollSpell(row.Dto.Spell)
            && (afterReroll.Cap > beforeReroll.Cap || afterReroll.Threshold > beforeReroll.Threshold))
            AppServices.Current.ReconsiderManaRegenRerollAfterConfigChange();
    }

    [RelayCommand]
    private void RemoveBuff(BuffSlotRowViewModel? row)
    {
        if (row is null) return;
        _settings.Slots.Remove(row.Dto);
        Slots.Remove(row);
        Renumber();
        RefreshBuffPicks();   // the freed spell returns to the picker
        OnPropertyChanged(nameof(HasSlots));
        RaiseSetupHint();
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    // Wipe every configured slot in one shot — self, party, and whole-party alike.
    // Routed through the same ConfirmDeleteAsync gate every other list-row delete in
    // the app uses (Settings → confirm-deletes), since this is a much bigger blast
    // radius than removing one row.
    [RelayCommand]
    private async System.Threading.Tasks.Task RemoveAllBuffs()
    {
        if (Slots.Count == 0) return;
        string what = Slots.Count == 1 ? "your 1 configured buff" : $"all {Slots.Count} configured buffs";
        bool ok = await AppServices.Current.Confirm.ConfirmDeleteAsync(what);
        if (!ok) return;

        _settings.Slots.Clear();
        Slots.Clear();
        RefreshBuffPicks();   // every freed spell returns to both pickers
        OnPropertyChanged(nameof(HasSlots));
        RaiseSetupHint();
        OnPropertyChanged(nameof(ShowPanel));
        Persist();
    }

    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        using PerformanceMonitor.Scope? scope = AppServices.CurrentOrNull?.Performance.Measure("Buff Watchdog save");
        p.PartyBuffs = _settings;
        _profile.Save();
        // Re-evaluate the caster now so a just-checked member's buff queues right away
        // (assume-uncast → due) instead of waiting for the next idle heartbeat. No-op
        // in combat, where the combat tick owns the cadence.
        AppServices.Current.CastDirector.OnIdleHeartbeat();
        RefreshOverwriteWarnings();
        RefreshManaUpkeep();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.Members.CollectionChanged -= OnMembersChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _spellbook.Changed -= OnSpellbookChanged;
        AppServices.Current.Inventory.FullInventoryParsed -= OnInventoryReloaded;
        AppServices.Current.CastDirector.BuffNegated -= OnBuffNegated;
        AppServices.Current.CombatProfiles.Changed -= OnCombatProfilesChanged;
    }
}
