using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

public class MonsterEditDialogViewModelTests
{
    // report paradigm-20260813-131658: setting the Override Attack spell by
    // typing its cast-code ("agon") resolved and saved correctly, but
    // reopening the dialog showed the internal Spells.Number ("22") instead
    // of the cast-code the user typed.
    [Fact]
    public void AttackOverride_ShowsCastCode_WhenResolverMapsSpellNumberBack()
    {
        MonsterOverlay existing = new() { OverrideAttackSpellId = 22 };

        MonsterEditDialogViewModel vm = new(
            wccNoStr: "100",
            mdbName: "test monster",
            existing: existing,
            currentTier: SettingsTier.Character,
            mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            resolveSpellNumber: n => n == 22 ? "agon" : null);

        Assert.Equal("agon", vm.AttackOverride);
    }

    [Fact]
    public void AttackOverride_FallsBackToNumber_WhenNoResolverProvided()
    {
        MonsterOverlay existing = new() { OverrideAttackSpellId = 22 };

        MonsterEditDialogViewModel vm = new(
            wccNoStr: "100",
            mdbName: "test monster",
            existing: existing,
            currentTier: SettingsTier.Character,
            mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character]);

        Assert.Equal("22", vm.AttackOverride);
    }
}
