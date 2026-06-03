using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Tables;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Players-tab self-filter: the loaded character's own row is hidden
/// from the table (granting yourself permissions is nonsense). Filter
/// matches on given name (first whitespace-delimited token) so it
/// still catches the row whether the BBS rendered it with or without
/// the family suffix this session. Drafts (no profile loaded) show
/// every row.
/// </summary>
public sealed class PlayersSectionViewModelTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static PlayerDatabase BuildDb(params string[] names)
    {
        PlayerDatabase db = new();
        foreach (string n in names)
            db.RecordObservation(n, null, null, null, null, null, null, Now);
        return db;
    }

    private static ProfileService BuildProfileWithName(string name)
    {
        ProfileService profile = new();
        profile.LoadBlank();
        profile.Current!.Name = name;
        return profile;
    }

    [Fact]
    public void NoProfileLoaded_ShowsEveryRow()
    {
        // Defensive: sectionVM with no ProfileService should still
        // render the table — drafts and tests both hit this path.
        PlayerDatabase db = BuildDb("Fujin WuzHere", "Raijin", "Helper");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: null);

        Assert.Equal(3, vm.AllRows.Count);
    }

    [Fact]
    public void ProfileLoaded_HidesOwnRow_MatchedByGivenName()
    {
        // Profile name "Fujin WuzHere" → given "Fujin" → the Fujin
        // observation row is hidden, the others remain.
        PlayerDatabase db = BuildDb("Fujin WuzHere", "Raijin", "Helper");
        ProfileService profile = BuildProfileWithName("Fujin WuzHere");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);

        Assert.Equal(2, vm.AllRows.Count);
        Assert.DoesNotContain(vm.AllRows, r => r.Get("Given Name") == "Fujin");
        Assert.Contains(vm.AllRows, r => r.Get("Given Name") == "Raijin");
        Assert.Contains(vm.AllRows, r => r.Get("Given Name") == "Helper");
    }

    [Fact]
    public void ProfileLoaded_GivenOnly_FiltersByGivenName()
    {
        // Profile.Name might be just the given name (no family). The
        // filter should still hide the matching given-name row.
        PlayerDatabase db = BuildDb("Fujin Wuz", "Raijin");
        ProfileService profile = BuildProfileWithName("Fujin");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);

        Assert.Single(vm.AllRows);
        Assert.Equal("Raijin", vm.AllRows[0].Get("Given Name"));
    }

    [Fact]
    public void FilterIsCaseInsensitive()
    {
        // Match logic must not be sensitive to the case the BBS
        // happens to render with (some BBSes title-case, some don't).
        PlayerDatabase db = BuildDb("fujin");
        ProfileService profile = BuildProfileWithName("FUJIN");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);

        Assert.Empty(vm.AllRows);
    }

    [Fact]
    public void BlankProfileName_ShowsEveryRow()
    {
        // Draft profile with no name → no filter.
        PlayerDatabase db = BuildDb("Fujin", "Raijin");
        ProfileService profile = new();
        profile.LoadBlank();
        // Don't set Name — stays empty.
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);

        Assert.Equal(2, vm.AllRows.Count);
    }

    [Fact]
    public void ProfileSwap_RefreshesFilter()
    {
        // Swap from Fujin → Raijin via ProfileLoaded event:
        // Fujin's row reappears, Raijin's row hides.
        PlayerDatabase db = BuildDb("Fujin", "Raijin");
        ProfileService profile = BuildProfileWithName("Fujin");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);
        Assert.Single(vm.AllRows);
        Assert.Equal("Raijin", vm.AllRows[0].Get("Given Name"));

        // Swap to Raijin. LoadBlank creates a fresh draft with empty
        // Name; we set Name then call NotifyMutated so the section
        // VM's ProfileMutated subscription re-runs the filter with
        // the new value.
        profile.LoadBlank();
        profile.Current!.Name = "Raijin";
        profile.NotifyMutated();

        Assert.Single(vm.AllRows);
        Assert.Equal("Fujin", vm.AllRows[0].Get("Given Name"));
    }

    [Fact]
    public void ProfileClosed_ShowsEveryRow()
    {
        // Closing the profile (no character loaded) drops the filter.
        // LoadBlank itself triggers ProfileClosed for the outgoing
        // profile + ProfileLoaded for the new blank, so the AllRows
        // count reflects the fresh draft (no name → no filter).
        PlayerDatabase db = BuildDb("Fujin", "Raijin");
        ProfileService profile = BuildProfileWithName("Fujin");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);
        Assert.Single(vm.AllRows);

        profile.LoadBlank();    // closes "Fujin" draft, opens new empty draft

        Assert.Equal(2, vm.AllRows.Count);
    }

    [Fact]
    public void Dispose_DetachesProfileSubscriptions()
    {
        // After dispose, profile events must not cause the section to
        // reload (the underlying PlayerDatabase singleton outlives
        // every browser open; leaking subscriptions = leaking VMs).
        PlayerDatabase db = BuildDb("Fujin", "Raijin");
        ProfileService profile = BuildProfileWithName("Fujin");
        PlayersSectionViewModel vm = new(db, dialogs: null, profile: profile);
        int rowsAtDispose = vm.AllRows.Count;

        vm.Dispose();
        // Trigger every event the section was subscribed to.
        profile.LoadBlank();
        profile.Current!.Name = "Raijin";
        profile.NotifyMutated();

        // AllRows must not have changed since the section is no longer
        // reloading.
        Assert.Equal(rowsAtDispose, vm.AllRows.Count);
    }
}
