using System.Collections.Generic;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// The Items tab's edit dialog offers "Installed defaults" as a Use option (the
// reset target) and reports EqualsInstalledDefaults so the applier clears a
// redundant override instead of writing it. Mirrors MonsterEditDialogViewModelTests.
public sealed class ItemEditDialogViewModelTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoInfo =
        Array.Empty<KeyValuePair<string, string>>();
    private static readonly IReadOnlyList<ShopSaleRow> NoShops = Array.Empty<ShopSaleRow>();

    private static ItemEditResult Save(ItemEditDialogViewModel vm)
    {
        ItemEditResult? captured = null;
        vm.CloseRequested += r => captured = r;
        vm.SaveCommand.Execute(null);
        Assert.NotNull(captured);
        return captured!;
    }

    private static ItemEditDialogViewModel MakeVm(
        ItemOverlay? existing, ItemOverlay? installedDefaults,
        SettingsTier currentTier = SettingsTier.Character)
        => new(
            wccNoStr: "1", mdbName: "torch", existing: existing,
            currentTier: currentTier, mdbInfo: NoInfo, shops: NoShops,
            writableTiers: [SettingsTier.Character, SettingsTier.Global],
            installedDefaults: installedDefaults);

    [Fact]
    public void Picker_OffersInstalledDefaults_ButDefaultsToWritableTier()
    {
        ItemEditDialogViewModel vm = MakeVm(new ItemOverlay(), new ItemOverlay(),
                                            currentTier: SettingsTier.Defaults);
        Assert.Contains(SettingsTier.Defaults, vm.AvailableTiers);
        Assert.Equal(SettingsTier.Character, vm.UseTier);
    }

    [Fact]
    public void EqualsInstalledDefaults_UnchangedFromSeed_IsTrue()
    {
        ItemOverlay seed = new() { AutoCollect = true };
        Assert.True(Save(MakeVm(seed, seed)).EqualsInstalledDefaults);
    }

    [Fact]
    public void EqualsInstalledDefaults_ChangedFromSeed_IsFalse()
    {
        ItemOverlay seed = new() { AutoCollect = true };
        ItemEditDialogViewModel vm = MakeVm(seed, seed);
        vm.AutoCollect = false;
        Assert.False(Save(vm).EqualsInstalledDefaults);
    }

    [Fact]
    public void EqualsInstalledDefaults_EditedBackToSeed_IsTrueAgain()
    {
        ItemOverlay seed = new();                             // seed: no flags set
        ItemOverlay existing = new() { AutoStash = true };    // user override
        ItemEditDialogViewModel vm = MakeVm(existing, seed);
        Assert.True(vm.AutoStash);

        vm.AutoStash = false;                                 // dragged back to the seed
        Assert.True(Save(vm).EqualsInstalledDefaults);
    }
}
