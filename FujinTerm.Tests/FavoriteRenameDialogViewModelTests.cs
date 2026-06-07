using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

public sealed class FavoriteRenameDialogViewModelTests
{
    [Fact]
    public void Save_EmitsCloseRequestedWithTrimmedLabel()
    {
        FavoriteRenameDialogViewModel vm = new("  Bank of Godfrey  ", "1/45");
        string? captured = "(unchanged)";
        bool fired = false;
        vm.CloseRequested += s => { captured = s; fired = true; };

        vm.SaveCommand.Execute(null);

        Assert.True(fired);
        Assert.Equal("Bank of Godfrey", captured);
    }

    [Fact]
    public void Save_EmptyLabel_EmitsEmptyStringNotNull()
    {
        // Caller (NavigationViewModel.RenameFavoriteAsync) maps the
        // empty result to a null label so the favourite falls back to
        // the graph display name. null is reserved for Cancel.
        FavoriteRenameDialogViewModel vm = new("anything", "1/45")
        {
            Label = "",
        };
        string? captured = "(unchanged)";
        vm.CloseRequested += s => captured = s;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured);
    }

    [Fact]
    public void Cancel_EmitsNull()
    {
        FavoriteRenameDialogViewModel vm = new("Bank", "1/45");
        string? captured = "(unchanged)";
        vm.CloseRequested += s => captured = s;

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }

    [Fact]
    public void Constructor_PrefillsLabelAndCoordTag()
    {
        FavoriteRenameDialogViewModel vm = new("Bank of Godfrey", "1/45");
        Assert.Equal("Bank of Godfrey", vm.Label);
        Assert.Equal("1/45", vm.CoordTag);
    }
}
