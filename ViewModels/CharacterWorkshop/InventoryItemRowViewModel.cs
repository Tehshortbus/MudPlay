using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One row in the Equipment Manager's Inventory view: a carried item, its per-unit
// carry weight, and the three loot-automation toggles (auto-stash / auto-collect /
// auto-discard) that map onto the item's game-data ItemOverlay. Flipping a toggle
// invokes the supplied callback so the section writes the overlay through the
// settings resolver; finer carry policy is left to the Game Data browser.
public sealed partial class InventoryItemRowViewModel : ObservableObject
{
    private readonly Action<InventoryItemRowViewModel> _onEdited;

    // Item Number (MDB / WCC No) — the overlay write key. Zero when the name
    // couldn't be resolved against the active item table.
    public int Number { get; }

    // Item display name, e.g. "a torch".
    public string Name { get; }

    // How many of this item the pack holds — identical names are grouped into one
    // row (the overlay is keyed by item type, not instance).
    public int Count { get; }

    // Multiplicity badge — "×3" when stacked, empty for a single.
    public string CountText => Count > 1 ? $"×{Count}" : string.Empty;

    // Per-unit carry weight (MDB Encum), or "—" when unknown.
    public string Weight { get; }

    // Whether the item is known to the active game data — an unresolved name
    // (Number == 0) can't target an overlay, so its toggles stay disabled.
    public bool CanToggle => Number > 0;

    [ObservableProperty] private bool _autoStash;
    [ObservableProperty] private bool _autoCollect;
    [ObservableProperty] private bool _autoDiscard;

    public InventoryItemRowViewModel(
        int number, string name, int count, string weight,
        bool autoStash, bool autoCollect, bool autoDiscard,
        Action<InventoryItemRowViewModel> onEdited)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(weight);
        ArgumentNullException.ThrowIfNull(onEdited);
        Number = number;
        Name = name;
        Count = count;
        Weight = weight;
        // Ctor-set backing fields don't fire the generated setter, so seeding the
        // toggles here never triggers a spurious overlay write.
        _autoStash = autoStash;
        _autoCollect = autoCollect;
        _autoDiscard = autoDiscard;
        _onEdited = onEdited;
    }

    partial void OnAutoStashChanged(bool value) => _onEdited(this);
    partial void OnAutoCollectChanged(bool value) => _onEdited(this);
    partial void OnAutoDiscardChanged(bool value) => _onEdited(this);
}
