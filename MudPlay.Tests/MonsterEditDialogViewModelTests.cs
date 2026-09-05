using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The "Override Attack" box disambiguation (<see cref="MonsterEditDialogViewModel.ParseAttackOverride"/>):
/// a positive integer is a Spell.Number (routed through the mana-gated attack-spell
/// rung). Text that resolves to a known spell's cast-code (via the injected
/// resolver) ALSO lands on that rung, via its resolved Number — someone typing
/// the code they'd actually cast in-game (report paradigm-20260813-070249)
/// shouldn't silently lose mana/cap gating for it. Only text matching no known
/// spell (or no resolver supplied) is a raw command sent as-is; blank is no
/// override. This is also what lets "attack" persist (report
/// paradigm-20260809-131642 — it used to be silently dropped by an int-only parse).
/// </summary>
public sealed class MonsterEditDialogViewModelTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("  42  ")]   // trimmed
    public void ParseAttackOverride_PositiveInteger_IsSpellId(string text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Equal(42, spellId);
        Assert.Null(command);
    }

    [Theory]
    [InlineData("attack", "attack")]
    [InlineData("  harm  ", "harm")]   // trimmed, kept as a command — no resolver supplied
    [InlineData("bash", "bash")]
    [InlineData("0", "0")]             // non-positive int is not a spell id → command
    [InlineData("-3", "-3")]
    public void ParseAttackOverride_NonNumericText_NoResolver_IsCommand(string text, string expected)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Equal(expected, command);
    }

    [Fact]
    public void ParseAttackOverride_CastCodeMatchesKnownSpell_ResolvesToSpellId()
    {
        // Report paradigm-20260813-070249: typing "turn" (the cast-code you'd
        // actually type in-game) must land on the mana-gated spell rung, same
        // as typing its Spell.Number directly — not silently become an
        // ungated raw command just because it's text, not digits.
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "turn", code => code == "turn" ? 18 : null);

        Assert.Equal(18, spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_CastCodeMatch_IsCaseAndWhitespaceInsensitive()
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "  TuRn  ", code => string.Equals(code, "turn", StringComparison.OrdinalIgnoreCase) ? 18 : null);

        Assert.Equal(18, spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_ResolverSupplied_NoMatch_FallsBackToCommand()
    {
        // A resolver is wired, but this text isn't a spell anyone knows —
        // still a legitimate raw command (e.g. "bash").
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "bash", _ => null);

        Assert.Null(spellId);
        Assert.Equal("bash", command);
    }

    [Fact]
    public void ParseAttackOverride_NumericText_ResolverNeverConsulted()
    {
        // A positive integer is always read as a literal Spell.Number —
        // the resolver (cast-code → number) isn't relevant here and must not
        // be invoked.
        bool resolverCalled = false;
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(
            "18", _ => { resolverCalled = true; return 999; });

        Assert.Equal(18, spellId);
        Assert.Null(command);
        Assert.False(resolverCalled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAttackOverride_Blank_IsNoOverride(string? text)
    {
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride(text);
        Assert.Null(spellId);
        Assert.Null(command);
    }

    [Fact]
    public void ParseAttackOverride_SetsExactlyOneOfThePair()
    {
        // The two backing fields are mutually exclusive — a species never carries
        // both a spell id and a command.
        (int? spellId, string? command) = MonsterEditDialogViewModel.ParseAttackOverride("attack");
        Assert.True(spellId is null ^ command is null || (spellId is null && command is null));
        Assert.Null(spellId);
        Assert.NotNull(command);
    }

    // ----- Display round-trip (Spell.Number → cast-code on reopen) -----------

    // report paradigm-20260813-131658: setting the Override Attack spell by typing
    // its cast-code ("agon") resolved and saved correctly, but reopening the dialog
    // showed the internal Spells.Number ("22") instead of the code the user typed.
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

    // ----- Per-monster override mana floors (the Settings → Combat spell-slot parity) ----

    [Fact]
    public void ManaFloors_LoadFromOverlay()
    {
        MonsterOverlay existing = new() { OverridePreAttackMinMana = 30, OverrideAttackMinMana = 40 };
        MonsterEditDialogViewModel vm = new(
            wccNoStr: "1", mdbName: "rat", existing: existing,
            currentTier: SettingsTier.Character, mdbInfo: Array.Empty<MdbInfoRow>(),
            writableTiers: [SettingsTier.Character]);

        Assert.Equal(30, vm.PreAttackMinMana);
        Assert.Equal(40, vm.AttackMinMana);
    }

    [Fact]
    public void ManaFloors_SaveIntoOverlay()
    {
        MonsterEditDialogViewModel vm = MakeVm(existing: null, installedDefaults: null);
        vm.PreAttackSpellId = "22";
        vm.PreAttackCount   = 3;
        vm.PreAttackMinMana = 30;
        vm.AttackOverride   = "18";
        vm.AttackMinMana    = 40;

        MonsterOverlay o = Save(vm).Overlay;
        Assert.Equal(3,  o.OverridePreAttackCount);
        Assert.Equal(30, o.OverridePreAttackMinMana);
        Assert.Equal(40, o.OverrideAttackMinMana);
    }

    [Fact]
    public void ManaFloors_BlankStaysNull()
    {
        MonsterEditDialogViewModel vm = MakeVm(existing: null, installedDefaults: null);
        vm.PreAttackSpellId = "22";   // spell set, but no mana floor typed

        MonsterOverlay o = Save(vm).Overlay;
        Assert.Null(o.OverridePreAttackMinMana);
        Assert.Null(o.OverrideAttackMinMana);
    }
}
