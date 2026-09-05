using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// Linking a spell number whose record already has text pulls it into the dialog:
// empty slots fill silently, a differing slot the dialog already holds surfaces a
// per-field collision the user resolves.
public sealed class MessageEditLinkFillTests
{
    private static MessageRecord ExistingForSpell5() => new(
        Id: "existing", Name: "rose book confuse", Flags: MessageFlags.Confused, RawFlagsHex: 2,
        CasterMessage: "Record caster", TargetMessage: string.Empty, WitnessMessage: "Record witness",
        AppliedMessage: string.Empty, AppliedEndsWith: string.Empty,
        Links: new[] { new GameDataLink("Spells", 5) });

    // A fresh candidate commit: the unrecognized line seeded into CasterMessage, blank name.
    private static MessageEditDialogViewModel SeedVm(IReadOnlyList<MessageRecord> existing) => new(
        new MessageRecord(
            Id: string.Empty, Name: string.Empty, Flags: MessageFlags.None, RawFlagsHex: 0,
            CasterMessage: "Unrecognized caster", TargetMessage: string.Empty, WitnessMessage: string.Empty,
            AppliedMessage: string.Empty, AppliedEndsWith: string.Empty, Links: Array.Empty<GameDataLink>()),
        SettingsTier.Defaults, existing, isNew: true, cache: null);

    [Fact]
    public void LinkingExistingSpell_FillsEmptySlots_AndSurfacesConflicts()
    {
        var vm = SeedVm(new[] { ExistingForSpell5() });

        vm.AddLinkNumber = "5";
        vm.AddLinkCommand.Execute(null);

        // Empty witness/name filled silently from the record; flags adopted.
        Assert.Equal("Record witness", vm.WitnessMessage);
        Assert.Equal("rose book confuse", vm.Name);
        Assert.True(vm.FlagConfused);

        // Caster differs → not overwritten; a single collision is surfaced.
        Assert.Equal("Unrecognized caster", vm.CasterMessage);
        Assert.True(vm.HasLinkFillConflicts);
        LinkFillConflict c = Assert.Single(vm.LinkFillConflicts);
        Assert.Equal("Caster", c.FieldLabel);
        Assert.Equal("Record caster", c.RecordValue);
        Assert.Equal("Unrecognized caster", c.UnrecognizedValue);
    }

    [Fact]
    public void ApplyLinkFill_KeepRecord_WritesRecordValue()
    {
        var vm = SeedVm(new[] { ExistingForSpell5() });
        vm.AddLinkNumber = "5";
        vm.AddLinkCommand.Execute(null);

        vm.LinkFillConflicts[0].UseRecord = true;   // keep record
        vm.ApplyLinkFillCommand.Execute(null);

        Assert.Equal("Record caster", vm.CasterMessage);
        Assert.False(vm.HasLinkFillConflicts);
    }

    [Fact]
    public void ApplyLinkFill_UseUnrecognized_KeepsDialogValue()
    {
        var vm = SeedVm(new[] { ExistingForSpell5() });
        vm.AddLinkNumber = "5";
        vm.AddLinkCommand.Execute(null);

        vm.LinkFillConflicts[0].UseRecord = false;  // use the unrecognized line
        vm.ApplyLinkFillCommand.Execute(null);

        Assert.Equal("Unrecognized caster", vm.CasterMessage);
        Assert.False(vm.HasLinkFillConflicts);
    }

    [Fact]
    public void LinkingSpellWithNoRecord_DoesNothing()
    {
        var vm = SeedVm(Array.Empty<MessageRecord>());
        vm.AddLinkNumber = "999";
        vm.AddLinkCommand.Execute(null);

        Assert.False(vm.HasLinkFillConflicts);
        Assert.Equal("Unrecognized caster", vm.CasterMessage);
    }
}
