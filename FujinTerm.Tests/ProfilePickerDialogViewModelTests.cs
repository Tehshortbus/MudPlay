using System.Collections.Generic;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class ProfilePickerDialogViewModelTests
{
    [Fact]
    public void Profiles_SortByBbsThenName_CaseInsensitive()
    {
        // Deliberately unsorted, mixed case, and with two BBSes sharing a
        // character name so the secondary (name) key actually matters.
        var refs = new List<ProfileRef>
        {
            new("Zebra BBS", "alice"),
            new("apex BBS", "Zoe"),
            new("apex BBS", "bob"),
            new("Mystic", "Yara"),
            new("apex BBS", "alice"),
        };

        var vm = new ProfilePickerDialogViewModel(refs);

        Assert.Equal(new[]
        {
            new ProfileRef("apex BBS", "alice"),
            new ProfileRef("apex BBS", "bob"),
            new ProfileRef("apex BBS", "Zoe"),
            new ProfileRef("Mystic", "Yara"),
            new ProfileRef("Zebra BBS", "alice"),
        }, vm.Profiles.ToArray());
    }
}
