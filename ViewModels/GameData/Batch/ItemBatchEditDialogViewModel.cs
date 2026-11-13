using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Batch;

// The set of item-override changes a batch commits, and the pure fold onto one
// record's existing overlay. Only opted-in fields are touched.
public sealed class ItemBatchChanges
{
    public BatchToggle AutoCollect { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoDiscard { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoOpen { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoBuy { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoSell { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoStash { get; init; } = BatchToggle.Leave;
    public BatchToggle CannotBeTaken { get; init; } = BatchToggle.Leave;
    public BatchToggle CarryOnlyOne { get; init; } = BatchToggle.Leave;
    public BatchToggle MustHaveMinimum { get; init; } = BatchToggle.Leave;
    public BatchToggle LoyalItem { get; init; } = BatchToggle.Leave;
    public BatchToggle AutoObtainForPath { get; init; } = BatchToggle.Leave;

    public bool ChangeMinToKeep { get; init; }
    public string MinToKeep { get; init; } = string.Empty;
    public bool ChangeMaxToGet { get; init; }
    public string MaxToGet { get; init; } = string.Empty;

    public bool AnyFieldChosen =>
        AutoCollect != BatchToggle.Leave || AutoDiscard != BatchToggle.Leave
        || AutoOpen != BatchToggle.Leave || AutoBuy != BatchToggle.Leave
        || AutoSell != BatchToggle.Leave || AutoStash != BatchToggle.Leave
        || CannotBeTaken != BatchToggle.Leave || CarryOnlyOne != BatchToggle.Leave
        || MustHaveMinimum != BatchToggle.Leave
        || LoyalItem != BatchToggle.Leave || AutoObtainForPath != BatchToggle.Leave
        || ChangeMinToKeep || ChangeMaxToGet;

    public ItemOverlay ApplyTo(ItemOverlay existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ItemOverlay o = existing;
        if (AutoCollect != BatchToggle.Leave) o = o with { AutoCollect = AutoCollect.Apply(o.AutoCollect) };
        if (AutoDiscard != BatchToggle.Leave) o = o with { AutoDiscard = AutoDiscard.Apply(o.AutoDiscard) };
        if (AutoOpen != BatchToggle.Leave) o = o with { AutoOpen = AutoOpen.Apply(o.AutoOpen) };
        if (AutoBuy != BatchToggle.Leave) o = o with { AutoBuy = AutoBuy.Apply(o.AutoBuy) };
        if (AutoSell != BatchToggle.Leave) o = o with { AutoSell = AutoSell.Apply(o.AutoSell) };
        // A sell detour needs Auto-sell, so turning Auto-sell off turns it off too.
        if (AutoSell == BatchToggle.Off) o = o with { SellDetour = false, SellDetourAbove = null };
        if (AutoStash != BatchToggle.Leave) o = o with { AutoStash = AutoStash.Apply(o.AutoStash) };
        if (CannotBeTaken != BatchToggle.Leave) o = o with { CannotBeTaken = CannotBeTaken.Apply(o.CannotBeTaken) };
        if (CarryOnlyOne != BatchToggle.Leave) o = o with { CarryOnlyOne = CarryOnlyOne.Apply(o.CarryOnlyOne) };
        if (MustHaveMinimum != BatchToggle.Leave) o = o with { MustHaveMinimum = MustHaveMinimum.Apply(o.MustHaveMinimum) };
        if (LoyalItem != BatchToggle.Leave) o = o with { LoyalItem = LoyalItem.Apply(o.LoyalItem) };
        if (AutoObtainForPath != BatchToggle.Leave) o = o with { AutoObtainForPath = AutoObtainForPath.Apply(o.AutoObtainForPath) };
        if (ChangeMinToKeep) o = o with { MinToKeep = Blank(MinToKeep) };
        if (ChangeMaxToGet) o = o with { MaxToGet = Blank(MaxToGet) };
        return o;
    }

    // The fields the batch sets, the only ones saved: a field left at Leave keeps
    // whatever each item's tier holds for it.
    public IReadOnlyList<string> ChosenFields
    {
        get
        {
            List<string> fields = new();
            void Add(BatchToggle t, string name) { if (t != BatchToggle.Leave) fields.Add(name); }
            Add(AutoCollect, nameof(ItemOverlay.AutoCollect));
            Add(AutoDiscard, nameof(ItemOverlay.AutoDiscard));
            Add(AutoOpen, nameof(ItemOverlay.AutoOpen));
            Add(AutoBuy, nameof(ItemOverlay.AutoBuy));
            Add(AutoSell, nameof(ItemOverlay.AutoSell));
            if (AutoSell == BatchToggle.Off)
            {
                fields.Add(nameof(ItemOverlay.SellDetour));
                fields.Add(nameof(ItemOverlay.SellDetourAbove));
            }
            Add(AutoStash, nameof(ItemOverlay.AutoStash));
            Add(CannotBeTaken, nameof(ItemOverlay.CannotBeTaken));
            Add(CarryOnlyOne, nameof(ItemOverlay.CarryOnlyOne));
            Add(MustHaveMinimum, nameof(ItemOverlay.MustHaveMinimum));
            Add(LoyalItem, nameof(ItemOverlay.LoyalItem));
            Add(AutoObtainForPath, nameof(ItemOverlay.AutoObtainForPath));
            if (ChangeMinToKeep) fields.Add(nameof(ItemOverlay.MinToKeep));
            if (ChangeMaxToGet) fields.Add(nameof(ItemOverlay.MaxToGet));
            return fields;
        }
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record ItemBatchResult(SettingsTier Tier, ItemBatchChanges Changes);

// Batch editor for the Items table. Flags are tri-state (Leave / On / Off);
// Min-to-keep and Max-to-get have a "Change" opt-in. The Defaults tier resets
// the selected items instead of setting fields.
public sealed partial class ItemBatchEditDialogViewModel
    : ObservableObject, IDialogViewModel<ItemBatchResult>
{
    public event Action<ItemBatchResult?>? CloseRequested;

    public int Count { get; }
    public string Heading { get; }
    public IReadOnlyList<SettingsTier> AvailableTiers { get; }
    public IReadOnlyList<BatchToggle> ToggleOptions { get; } = new[] { BatchToggle.Leave, BatchToggle.On, BatchToggle.Off };

    [ObservableProperty] private SettingsTier _useTier;

    [ObservableProperty] private BatchToggle _autoCollect = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoDiscard = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoOpen = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoBuy = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoSell = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoStash = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _cannotBeTaken = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _carryOnlyOne = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _mustHaveMinimum = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _loyalItem = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _autoObtainForPath = BatchToggle.Leave;

    [ObservableProperty] private bool _changeMinToKeep;
    [ObservableProperty] private string _minToKeep = string.Empty;
    [ObservableProperty] private bool _changeMaxToGet;
    [ObservableProperty] private string _maxToGet = string.Empty;

    public bool IsResetTier => UseTier == SettingsTier.Defaults;

    public ItemBatchEditDialogViewModel(int count, IReadOnlyList<SettingsTier>? writableTiers = null)
    {
        Count = count;
        Heading = $"Batch edit {count} items";
        IReadOnlyList<SettingsTier> writable = writableTiers is { Count: > 0 }
            ? writableTiers
            : new[] { SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global };
        AvailableTiers = writable.Append(SettingsTier.Defaults).ToArray();
        UseTier = writable[0];
    }

    partial void OnUseTierChanged(SettingsTier value) => OnPropertyChanged(nameof(IsResetTier));

    private ItemBatchChanges BuildChanges() => new()
    {
        AutoCollect = AutoCollect, AutoDiscard = AutoDiscard, AutoOpen = AutoOpen,
        AutoBuy = AutoBuy, AutoSell = AutoSell, AutoStash = AutoStash,
        CannotBeTaken = CannotBeTaken, CarryOnlyOne = CarryOnlyOne, MustHaveMinimum = MustHaveMinimum,
        LoyalItem = LoyalItem, AutoObtainForPath = AutoObtainForPath,
        ChangeMinToKeep = ChangeMinToKeep, MinToKeep = MinToKeep,
        ChangeMaxToGet = ChangeMaxToGet, MaxToGet = MaxToGet,
    };

    [RelayCommand]
    private void Apply() => CloseRequested?.Invoke(new ItemBatchResult(UseTier, BuildChanges()));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
