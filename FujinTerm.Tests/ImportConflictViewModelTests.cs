using System.Collections.Generic;
using System.Linq;
using FujinTerm.Models.Import;
using FujinTerm.ViewModels.Import;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ImportConflictViewModelTests
{
    private static ImportConflict MakeConflict(string id,
        IReadOnlyDictionary<string, string?>? existing = null,
        IReadOnlyDictionary<string, string?>? incoming = null,
        string category = "Monsters")
        => new(
            category,
            id,
            existing ?? new Dictionary<string, string?> { ["Name"] = "Goblin", ["HP"] = "10" },
            incoming ?? new Dictionary<string, string?> { ["Name"] = "Goblin", ["HP"] = "12" });

    [Fact]
    public void Ctor_RequiresAtLeastOneConflict()
    {
        Assert.Throws<ArgumentException>(() =>
            new ImportConflictViewModel("title", "summary", Array.Empty<ImportConflict>()));
    }

    [Fact]
    public void Ctor_DefaultsEveryRowToSkip_AndSelectsFirst()
    {
        ImportConflictViewModel vm = new("title", "summary",
            new[] { MakeConflict("a"), MakeConflict("b") });

        Assert.All(vm.Rows, r => Assert.Equal(ImportAction.Skip, r.Action));
        Assert.Same(vm.Rows[0], vm.SelectedRow);
    }

    [Fact]
    public void RowDiff_DetectsChangedAndUnchangedFields()
    {
        var conflict = MakeConflict("a",
            existing: new Dictionary<string, string?> { ["Name"] = "Goblin", ["HP"] = "10", ["MP"] = null },
            incoming: new Dictionary<string, string?> { ["Name"] = "Goblin", ["HP"] = "12", ["MP"] = "5" });
        ImportConflictRowViewModel row = new(conflict);

        var byName = row.FieldDiffs.ToDictionary(d => d.FieldName);

        Assert.False(byName["Name"].Changed);
        Assert.True(byName["HP"].Changed);
        Assert.True(byName["MP"].Changed);
    }

    [Fact]
    public void RowDiff_IncludesFieldsPresentInOnlyOneSide()
    {
        var conflict = MakeConflict("a",
            existing: new Dictionary<string, string?> { ["A"] = "1" },
            incoming: new Dictionary<string, string?> { ["B"] = "2" });
        ImportConflictRowViewModel row = new(conflict);

        Assert.Contains(row.FieldDiffs, d => d.FieldName == "A" && d.Changed);
        Assert.Contains(row.FieldDiffs, d => d.FieldName == "B" && d.Changed);
    }

    [Fact]
    public void ApplyToAll_ChangesEveryRow_RegardlessOfPriorPick()
    {
        ImportConflictViewModel vm = new("t", "s",
            new[] { MakeConflict("a"), MakeConflict("b"), MakeConflict("c") });
        vm.Rows[1].Action = ImportAction.Rename;

        vm.OverwriteAllCommand.Execute(null);

        Assert.All(vm.Rows, r => Assert.Equal(ImportAction.Overwrite, r.Action));
    }

    [Fact]
    public void CanCommit_BlocksOk_WhenAnyRenameTargetIsEmpty()
    {
        ImportConflictViewModel vm = new("t", "s",
            new[] { MakeConflict("a"), MakeConflict("b") });
        vm.Rows[0].Action = ImportAction.Rename;
        vm.Rows[0].RenameTo = string.Empty;

        Assert.False(vm.CanCommit);
        Assert.False(vm.OkCommand.CanExecute(null));

        vm.Rows[0].RenameTo = "Goblin-2";

        Assert.True(vm.CanCommit);
    }

    [Fact]
    public void Ok_RaisesCloseRequested_WithResolutionsInInputOrder()
    {
        ImportConflictViewModel vm = new("t", "s",
            new[] { MakeConflict("a"), MakeConflict("b") });
        vm.Rows[0].Action = ImportAction.Overwrite;
        vm.Rows[1].Action = ImportAction.Rename;
        vm.Rows[1].RenameTo = "  renamed-b  ";  // trimmed on resolution emit

        ImportConflictResult? captured = null;
        vm.CloseRequested += r => captured = r;

        vm.OkCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Resolutions.Count);
        Assert.Equal(ImportAction.Overwrite, captured.Resolutions[0].Action);
        Assert.Null(captured.Resolutions[0].RenameTo);
        Assert.Equal(ImportAction.Rename, captured.Resolutions[1].Action);
        Assert.Equal("renamed-b", captured.Resolutions[1].RenameTo);
    }

    [Fact]
    public void Cancel_RaisesCloseRequested_WithNull()
    {
        ImportConflictViewModel vm = new("t", "s", new[] { MakeConflict("a") });

        ImportConflictResult? captured = new ImportConflictResult(Array.Empty<ImportResolution>());
        bool fired = false;
        vm.CloseRequested += r => { captured = r; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(captured);
    }

    [Fact]
    public void IsRenameSelected_TogglesWithAction()
    {
        ImportConflictRowViewModel row = new(MakeConflict("a"));
        Assert.False(row.IsRenameSelected);
        row.Action = ImportAction.Rename;
        Assert.True(row.IsRenameSelected);
        row.Action = ImportAction.Skip;
        Assert.False(row.IsRenameSelected);
    }
}
