using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// The three spell-override boxes (Debuff / Normal / Alternate) are SPELL-ONLY
// (MonsterEditDialogViewModel.ResolveSpellOverride): a positive integer is a
// Spell.Number; other text resolves via the injected cast-code resolver (someone
// typing the code they'd actually cast in-game, e.g. "turn", report
// paradigm-20260813-070249); text that matches no known spell yields null — raw
// attack verbs belong in the separate Physical attack box, not a spell rung.
// Blank is no override.
public sealed class MonsterEditDialogViewModelTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("  42  ")]   // trimmed
    public void ResolveSpellOverride_PositiveInteger_IsSpellId(string text)
    {
        Assert.Equal(42, MonsterEditDialogViewModel.ResolveSpellOverride(text));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void ResolveSpellOverride_NonPositiveInteger_IsNull(string text)
    {
        Assert.Null(MonsterEditDialogViewModel.ResolveSpellOverride(text));
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("  bash  ")]
    public void ResolveSpellOverride_NonSpellText_NoResolver_IsNull(string text)
    {
        // Spell-only: a raw verb with no resolver is NOT a spell → null (it belongs
        // in the Physical attack box).
        Assert.Null(MonsterEditDialogViewModel.ResolveSpellOverride(text));
    }

    [Fact]
    public void ResolveSpellOverride_CastCodeMatchesKnownSpell_ResolvesToSpellId()
    {
        // Typing "turn" (the cast-code you'd type in-game) resolves to its
        // Spell.Number, same as typing the number directly.
        Assert.Equal(18, MonsterEditDialogViewModel.ResolveSpellOverride(
            "turn", code => code == "turn" ? 18 : null));
    }

    [Fact]
    public void ResolveSpellOverride_CastCodeMatch_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(18, MonsterEditDialogViewModel.ResolveSpellOverride(
            "  TuRn  ", code => string.Equals(code, "turn", StringComparison.OrdinalIgnoreCase) ? 18 : null));
    }

    [Fact]
    public void ResolveSpellOverride_ResolverSupplied_NoMatch_IsNull()
    {
        // A resolver is wired, but this text isn't a spell anyone knows — spell-only
        // means it drops to null (not a command).
        Assert.Null(MonsterEditDialogViewModel.ResolveSpellOverride("bash", _ => null));
    }

    [Fact]
    public void ResolveSpellOverride_NumericText_ResolverNeverConsulted()
    {
        // A positive integer is always a literal Spell.Number — the cast-code
        // resolver isn't relevant and must not be invoked.
        bool resolverCalled = false;
        int? id = MonsterEditDialogViewModel.ResolveSpellOverride(
            "18", _ => { resolverCalled = true; return 999; });
        Assert.Equal(18, id);
        Assert.False(resolverCalled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveSpellOverride_Blank_IsNull(string? text)
    {
        Assert.Null(MonsterEditDialogViewModel.ResolveSpellOverride(text));
    }

    // ----- Display round-trip (Spell.Number → cast-code on reopen) -----------

    // report paradigm-20260813-131658: setting a spell override by typing its
    // cast-code ("agon") saved correctly, but reopening showed the internal
    // Spells.Number ("22") instead of the code — all three spell boxes round-trip
    // the number back to a code via the resolver.
    [Fact]
    public void NormalSpell_ShowsCastCode_WhenResolverMapsSpellNumberBack()
    {
        MonsterOverlay existing = new() { OverrideAttackSpellId = 22 };
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "100", mdbName: "test monster", existing: existing,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            resolveSpellNumber: n => n == 22 ? "agon" : null);

        Assert.Equal("agon", vm.NormalSpellId);
    }

    [Fact]
    public void NormalSpell_FallsBackToNumber_WhenNoResolverProvided()
    {
        MonsterOverlay existing = new() { OverrideAttackSpellId = 22 };
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "100", mdbName: "test monster", existing: existing,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character]);

        Assert.Equal("22", vm.NormalSpellId);
    }

    [Fact]
    public void AllSpellRungs_ShowCastCodes_OnReopen()
    {
        MonsterOverlay existing = new()
        {
            OverridePreAttackSpellId = 7,
            OverrideAttackSpellId = 22,
            OverrideAltAttackSpellId = 30,
            OverridePhysicalCommand = "bash",
        };
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: existing,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            resolveSpellNumber: n => n switch { 7 => "curse", 22 => "agon", 30 => "flame", _ => null });

        Assert.Equal("curse", vm.PreAttackSpellId);
        Assert.Equal("agon",  vm.NormalSpellId);
        Assert.Equal("flame", vm.AltSpellId);
        Assert.Equal("bash",  vm.PhysicalCommand);   // legacy OverrideAttackCommand key round-trips here
    }

    // ----- Installed-defaults tier + equality (the reset / auto-cleanup wiring) ----

    private static MonsterEditResult Save(MonsterEditDialogViewModel vm)
    {
        MonsterEditResult? captured = null;
        vm.CloseRequested += r => captured = r;
        vm.SaveCommand.Execute(null);
        Assert.NotNull(captured);
        return captured!;
    }

    private static MonsterEditDialogViewModel MakeVm(
        MonsterOverlay? existing, MonsterOverlay? installedDefaults,
        SettingsTier currentTier = SettingsTier.Character)
        => new(
            wccNoStr: "1", mdbName: "rat", existing: existing,
            currentTier: currentTier, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character, SettingsTier.Global],
            installedDefaults: installedDefaults);

    [Fact]
    public void Picker_OffersInstalledDefaults_ButDefaultsToWritableTier()
    {
        // A Def record (currentTier = Defaults) still opens on a writable tier, so a
        // plain edit lands as an override; Installed defaults is only reached by picking it.
        MonsterEditDialogViewModel vm = MakeVm(new MonsterOverlay(), new MonsterOverlay(),
                                               currentTier: SettingsTier.Defaults);
        Assert.Contains(SettingsTier.Defaults, vm.AvailableTiers);
        Assert.NotEqual(SettingsTier.Defaults, vm.UseTier);
        Assert.Equal(SettingsTier.Character, vm.UseTier);
    }

    [Fact]
    public void EqualsInstalledDefaults_UnchangedFromSeed_IsTrue()
    {
        // Seeded default = Friend (a ganghouse guardian). Opened unchanged → saving is a
        // no-op vs the seed, so the applier clears the tier instead of writing.
        MonsterOverlay seed = new() { Relationship = MonsterRelationship.Friend };
        MonsterEditDialogViewModel vm = MakeVm(existing: seed, installedDefaults: seed);

        Assert.True(Save(vm).EqualsInstalledDefaults);
    }

    [Fact]
    public void EqualsInstalledDefaults_ChangedFromSeed_IsFalse()
    {
        MonsterOverlay seed = new() { Relationship = MonsterRelationship.Friend };
        MonsterEditDialogViewModel vm = MakeVm(existing: seed, installedDefaults: seed);
        vm.Relationship = MonsterRelationship.Enemy;

        Assert.False(Save(vm).EqualsInstalledDefaults);
    }

    [Fact]
    public void EqualsInstalledDefaults_EditedBackToSeed_IsTrueAgain()
    {
        // Seed says Friend; a Character override made it Enemy. Dragging it back to
        // Friend matches the seed again → the redundant override should be cleared.
        MonsterOverlay seed = new() { Relationship = MonsterRelationship.Friend };
        MonsterOverlay existing = new() { Relationship = MonsterRelationship.Enemy };
        MonsterEditDialogViewModel vm = MakeVm(existing: existing, installedDefaults: seed);
        Assert.Equal(MonsterRelationship.Enemy, vm.Relationship);   // shows the override

        vm.Relationship = MonsterRelationship.Friend;               // back to the seed
        Assert.True(Save(vm).EqualsInstalledDefaults);
    }

    // ----- Per-monster override save round-trip (the four single-target rungs) ----

    [Fact]
    public void AllFourRungs_SaveIntoOverlay()
    {
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: null,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            resolveSpellShort: code => code switch { "curse" => 7, "agon" => 22, "flame" => 30, _ => (int?)null });

        vm.PreAttackSpellId = "curse"; vm.PreAttackCount = 2; vm.PreAttackMinMana = 30;
        vm.NormalSpellId    = "agon";  vm.NormalCount    = 3; vm.NormalMinMana    = 40;
        vm.AltSpellId       = "flame"; vm.AltCount       = 1; vm.AltMinMana       = 25;
        vm.PhysicalCommand  = "bash";

        MonsterOverlay o = Save(vm).Overlay;
        Assert.Equal(7,  o.OverridePreAttackSpellId);
        Assert.Equal(2,  o.OverridePreAttackCount);
        Assert.Equal(30, o.OverridePreAttackMinMana);
        Assert.Equal(22, o.OverrideAttackSpellId);
        Assert.Equal(3,  o.OverrideAttackCount);
        Assert.Equal(40, o.OverrideAttackMinMana);
        Assert.Equal(30, o.OverrideAltAttackSpellId);
        Assert.Equal(1,  o.OverrideAltAttackCount);
        Assert.Equal(25, o.OverrideAltAttackMinMana);
        Assert.Equal("bash", o.OverridePhysicalCommand);
    }

    [Fact]
    public void SpellBox_RawVerb_IsNotSavedAsSpellOrCommand()
    {
        // A verb that resolves to no spell drops out of a spell box (spell-only);
        // it does NOT silently migrate into the physical command.
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: null,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            resolveSpellShort: _ => null);
        vm.NormalSpellId = "attack";

        MonsterOverlay o = Save(vm).Overlay;
        Assert.Null(o.OverrideAttackSpellId);
        Assert.Null(o.OverridePhysicalCommand);
    }

    [Fact]
    public void ManaFloors_LoadFromOverlay()
    {
        MonsterOverlay existing = new()
        {
            OverridePreAttackMinMana = 30,
            OverrideAttackMinMana = 40,
            OverrideAltAttackMinMana = 25,
        };
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: existing,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character]);

        Assert.Equal(30, vm.PreAttackMinMana);
        Assert.Equal(40, vm.NormalMinMana);
        Assert.Equal(25, vm.AltMinMana);
    }

    [Fact]
    public void ManaFloors_BlankStaysNull()
    {
        MonsterEditDialogViewModel vm = MakeVm(existing: null, installedDefaults: null);
        vm.NormalSpellId = "22";   // spell set, but no mana floor typed

        MonsterOverlay o = Save(vm).Overlay;
        Assert.Null(o.OverridePreAttackMinMana);
        Assert.Null(o.OverrideAttackMinMana);
        Assert.Null(o.OverrideAltAttackMinMana);
    }

    // ----- Location boxes (Landmass / Region / Area) ----

    private static readonly MonsterOverlay SeededLocation = new()
    {
        Landmass = "Mainland", Region = "Volcano", Area = "Infernal Cavern",
    };

    [Fact]
    public void Location_LoadsFromExistingOverlay_BlankWhenUnset()
    {
        MonsterEditDialogViewModel set = MakeVm(existing: SeededLocation, installedDefaults: SeededLocation);
        Assert.Equal("Mainland", set.Landmass);
        Assert.Equal("Volcano", set.Region);
        Assert.Equal("Infernal Cavern", set.Area);

        MonsterEditDialogViewModel unset = MakeVm(existing: new MonsterOverlay(), installedDefaults: new MonsterOverlay());
        Assert.Equal(string.Empty, unset.Landmass);
        Assert.Equal(string.Empty, unset.Region);
        Assert.Equal(string.Empty, unset.Area);
    }

    [Fact]
    public void Location_FilledFromBlank_SavesTrimmedOverride()
    {
        // The unset case the boxes exist for: nothing in the seed, user types a place.
        MonsterEditDialogViewModel vm = MakeVm(existing: new MonsterOverlay(), installedDefaults: new MonsterOverlay());
        vm.Landmass = "  Mainland ";
        vm.Region = "Hidden Vale";
        vm.Area = "Old Well";

        MonsterEditResult r = Save(vm);
        Assert.Equal("Mainland", r.Overlay.Landmass);
        Assert.Equal("Hidden Vale", r.Overlay.Region);
        Assert.Equal("Old Well", r.Overlay.Area);
        Assert.False(r.EqualsInstalledDefaults);
    }

    [Fact]
    public void Location_LeftAsSeed_StoresNothing_AndEqualsInstalledDefaults()
    {
        // A box still showing the seed's label must not copy it into the tier, or a
        // corrected seed in a later update would never reach this monster.
        MonsterEditDialogViewModel vm = MakeVm(existing: SeededLocation, installedDefaults: SeededLocation);

        MonsterEditResult r = Save(vm);
        Assert.Null(r.Overlay.Landmass);
        Assert.Null(r.Overlay.Region);
        Assert.Null(r.Overlay.Area);
        Assert.True(r.EqualsInstalledDefaults);
    }

    [Fact]
    public void Location_ChangedFromSeed_SavesOnlyTheChangedLabel()
    {
        MonsterEditDialogViewModel vm = MakeVm(existing: SeededLocation, installedDefaults: SeededLocation);
        vm.Area = "Lava Pit";

        MonsterEditResult r = Save(vm);
        Assert.Null(r.Overlay.Landmass);
        Assert.Null(r.Overlay.Region);
        Assert.Equal("Lava Pit", r.Overlay.Area);
        Assert.False(r.EqualsInstalledDefaults);
    }

    [Fact]
    public void Location_SeedLabelMatchIsCaseInsensitive()
    {
        MonsterEditDialogViewModel vm = MakeVm(existing: SeededLocation, installedDefaults: SeededLocation);
        vm.Region = "volcano";

        Assert.Null(Save(vm).Overlay.Region);
    }

    [Fact]
    public void Location_EditedBackToSeed_IsEqualToInstalledDefaultsAgain()
    {
        MonsterOverlay existing = new() { Landmass = "Mainland", Region = "Volcano", Area = "Lava Pit" };
        MonsterEditDialogViewModel vm = MakeVm(existing: existing, installedDefaults: SeededLocation);
        Assert.Equal("Lava Pit", vm.Area);   // shows the override

        vm.Area = "Infernal Cavern";
        Assert.True(Save(vm).EqualsInstalledDefaults);
    }

    [Fact]
    public void Location_Suggestions_DefaultEmpty_AndPassThrough()
    {
        Assert.Empty(MakeVm(null, null).LocationSuggestions.Regions);

        MonsterLocationSuggestions s = new(["Mainland"], ["Volcano"], ["Lava Pit"]);
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: null,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character],
            locationSuggestions: s);
        Assert.Same(s, vm.LocationSuggestions);
    }
}
