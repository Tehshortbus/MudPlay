using System.Collections.Generic;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.ViewModels.Settings;
using Xunit;

namespace FujinTerm.Tests;

// Coverage for MenuNavImportOption.Build — the candidate-selection logic behind
// the BBS section's "import logon steps from another character" picker: which
// (character, BBS) sequences are offered, which are excluded, and their order.
public sealed class MenuNavImportOptionTests
{
    private static CharacterProfile Profile(params (string bbs, int stepCount)[] entries)
    {
        var creds = new Dictionary<string, BbsCredentials>();
        foreach ((string bbs, int stepCount) in entries)
        {
            var steps = new List<MenuStep>();
            for (int i = 0; i < stepCount; i++)
                steps.Add(new MenuStep { WaitForPattern = $"p{i}", Send = $"s{i}" });
            creds[bbs] = new BbsCredentials { MenuNavSteps = steps };
        }
        return new CharacterProfile { BbsCredentials = creds };
    }

    [Fact]
    public void Build_ExcludesExactEditingPair_KeepsSameCharacterOtherBbs()
    {
        // "Alice" on ParaBBS is the character being edited. Her ParaBBS steps are
        // the editing target (excluded), but her OtherBBS steps stay importable —
        // handy when a new BBS mirrors one she already set up.
        var profiles = new[]
        {
            ("ParaBBS", "Alice", Profile(("ParaBBS", 3), ("OtherBBS", 2))),
        };

        IReadOnlyList<MenuNavImportOption> options = MenuNavImportOption.Build(
            profiles, editingBbs: "ParaBBS", currentBbs: "ParaBBS", currentName: "Alice");

        MenuNavImportOption only = Assert.Single(options);
        Assert.Equal("Alice", only.ProfileName);
        Assert.Equal("OtherBBS", only.BbsName);
        Assert.Equal(2, only.Steps.Count);
    }

    [Fact]
    public void Build_DropsEmptySequences()
    {
        var profiles = new[]
        {
            ("ParaBBS", "Bob", Profile(("ParaBBS", 0))),
        };

        IReadOnlyList<MenuNavImportOption> options = MenuNavImportOption.Build(
            profiles, editingBbs: "ParaBBS", currentBbs: "ParaBBS", currentName: "Alice");

        Assert.Empty(options);
    }

    [Fact]
    public void Build_ListsAllCharacters_SameBbsFirstThenByBbsThenName()
    {
        // Editing Alice @ ParaBBS. Candidates span two BBSes; same-BBS ones lead,
        // then alphabetical by BBS then character.
        var profiles = new[]
        {
            ("ParaBBS", "Alice", Profile(("ParaBBS", 1))),   // editing target -> excluded
            ("ParaBBS", "Zed",   Profile(("ParaBBS", 4))),   // same BBS
            ("ParaBBS", "Bob",   Profile(("ParaBBS", 2))),   // same BBS
            ("WestMud",  "Carol", Profile(("WestMud", 3))),  // other BBS
        };

        IReadOnlyList<MenuNavImportOption> options = MenuNavImportOption.Build(
            profiles, editingBbs: "ParaBBS", currentBbs: "ParaBBS", currentName: "Alice");

        Assert.Equal(
            new[] { ("Bob", "ParaBBS"), ("Zed", "ParaBBS"), ("Carol", "WestMud") },
            options.Select(o => (o.ProfileName, o.BbsName)).ToArray());
    }

    [Fact]
    public void ToString_NamesBbsAndPluralisesSteps()
    {
        var one = new MenuNavImportOption("Alice", "ParaBBS", new[] { new MenuStep() });
        var many = new MenuNavImportOption("Bob", "WestMud", new[] { new MenuStep(), new MenuStep() });

        Assert.Equal("Alice @ ParaBBS (1 step)", one.ToString());
        Assert.Equal("Bob @ WestMud (2 steps)", many.ToString());
    }
}
