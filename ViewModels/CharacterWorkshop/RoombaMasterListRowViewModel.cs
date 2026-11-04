using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One row of the Roomba Master List: an item Roomba has seen, the room it was
// seen in, and where it can be sold/bought (non-gang-house shops only).
public sealed partial class RoombaMasterListRowViewModel : ObservableObject
{
    private readonly Lazy<string> _market;
    private readonly Action<string, bool> _setIgnored;

    // Whether a sweep leaves this item where it lies. Every row of the same item
    // shows the same value: the ignore list is by item, not by room.
    [ObservableProperty] private bool _isIgnored;

    public string ItemName { get; }
    public int Quantity { get; }
    public string SeenIn { get; }

    // MDB item number, or -1 when the sighting's name didn't resolve to a record.
    // Drives the double-click "open item record".
    public int ItemNumber { get; }

    // The outside-market cross-reference is EXPENSIVE — an Obtained-From shop
    // resolution per item — so it's computed lazily the first time this cell is
    // realized. With the DataGrid virtualized, opening a huge log only prices the
    // few visible rows instead of every item up front.
    public string Market => _market.Value;

    public RoombaMasterListRowViewModel(
        string itemName, int quantity, string seenIn, int itemNumber, Lazy<string> market,
        bool isIgnored, Action<string, bool> setIgnored)
    {
        ItemName = itemName;
        Quantity = quantity;
        SeenIn = seenIn;
        ItemNumber = itemNumber;
        _market = market;
        _isIgnored = isIgnored;
        _setIgnored = setIgnored;
    }

    partial void OnIsIgnoredChanged(bool value) => _setIgnored(ItemName, value);
}
