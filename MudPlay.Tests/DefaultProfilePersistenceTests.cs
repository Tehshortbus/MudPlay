using System.IO;
using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins what the default profile is: the template in the Global folder that a new
// character starts from, and never something a session writes. A session on it
// (LoadDefaultProfile) is an unnamed draft whose Save writes nothing; naming it is
// what keeps it.
//
// Each test has a template folder of its own, never the Global one: every profile
// created in the run starts from that, and a template written here was handed to
// the characters other classes create at the same moment.
public sealed class DefaultProfilePersistenceTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mudplay-defprof-" + Path.GetRandomFileName());
    private string Template => Path.Combine(_folder, "default-profile.json");
    private string Marker => Path.Combine(_folder, ".default-profile-reset");
    private readonly List<string> _boards = new();

    private ProfileService Service() => new(Template, Marker);

    private string NewBoard()
    {
        string board = "defprof-test-" + Path.GetRandomFileName();
        _boards.Add(board);
        return board;
    }

    public void Dispose()
    {
        foreach (string folder in _boards.Select(AppPaths.BbsFolder).Append(_folder))
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    // A player who never named a character used to have every save go into the
    // template, so their setup became what each new character started from and
    // they had no profile of their own (user, 2026-10-12). The template is not
    // written from a session any more; that a save was wanted is remembered, so
    // the client can ask before the draft is lost.
    [Fact]
    public void LoadDefaultProfile_Save_WritesNothing_AndRemembersThereIsSomethingToKeep()
    {
        string board = NewBoard();
        ProfileService session = Service();
        session.LoadDefaultProfile();
        Assert.False(session.UnnamedDraftHasChanges);       // nothing done yet

        session.Current!.Settings ??= new();
        session.Current.Settings["Probe"] = JsonSerializer.SerializeToElement(new { Flag = true });
        session.Save();

        Assert.False(File.Exists(Template));
        Assert.True(session.UnnamedDraftHasChanges);

        // Named, it is a character holding what was set up, and nothing is owed.
        session.SaveAs(board, "Named");
        Assert.False(session.UnnamedDraftHasChanges);
        Assert.True(JsonStore.Load<Models.Profile.CharacterProfile>(
            AppPaths.CharacterProfileFile(board, "Named"))!.Settings!.ContainsKey("Probe"));

        // The next draft starts from the template as it was, not from that session.
        ProfileService next = Service();
        next.LoadDefaultProfile();
        Assert.False(next.Current!.Settings?.ContainsKey("Probe") == true);
    }

    // Profile Management's Add on the unnamed profile names what is loaded and puts
    // it on the selected realm (user, 2026-10-14): the loaded profile is then that
    // character, on disk with its realm, and nothing is owed.
    [Fact]
    public void NamingTheDraft_ThenAssigningARealm_LeavesTheLoadedCharacterOnThatRealm()
    {
        string board = NewBoard();
        ProfileService session = Service();
        session.LoadDefaultProfile();
        session.Save();                                   // something set up on it

        session.SaveAs(board, "Newcomer");
        session.AssignRealm(new Models.Profile.ProfileRef(board, "Newcomer"), "PVP");

        Assert.Equal("Newcomer", session.CurrentProfileName);
        Assert.False(session.UnnamedDraftHasChanges);
        Assert.Equal("PVP", JsonStore.Load<Models.Profile.CharacterProfile>(
            AppPaths.CharacterProfileFile(board, "Newcomer"))!.Realm);
    }

    // Discarding is the user saying the draft need not be kept: nothing asks again.
    [Fact]
    public void DiscardingTheDraftsChanges_LeavesNothingToAskAbout()
    {
        ProfileService session = Service();
        session.LoadDefaultProfile();
        session.Save();
        Assert.True(session.UnnamedDraftHasChanges);

        session.DiscardUnnamedDraftChanges();

        Assert.False(session.UnnamedDraftHasChanges);
    }

    // The startup-animation preference is install-global: written to the Global
    // default profile and read back from it regardless of which profile is loaded.
    // A reader with no profile loaded takes the SAME ReadDefaultProfileFile() branch
    // an auto-loaded NAMED profile would, so this pins the "always the Global default,
    // never the loaded character" contract that the fix hinges on.
    [Fact]
    public void StartupAnimation_WriteThenRead_RoundTripsThroughGlobalDefaultFile()
    {
        ProfileService writer = Service();
        writer.LoadDefaultProfile();                       // default is Current
        writer.WriteDefaultProfileStartupAnimation(false); // edits Current, and writes the Global file itself
        Assert.False(writer.ReadDefaultProfileStartupAnimation()); // Current branch

        // A separate service with NO profile loaded reads the Global file — the
        // same branch a loaded named profile would take.
        ProfileService reader = Service();
        Assert.False(reader.ReadDefaultProfileStartupAnimation());
    }

    [Fact]
    public void StartupAnimation_DefaultsToOn_WhenNoGlobalDefaultFile()
    {
        ProfileService svc = Service();
        // Absent default file → installed default (animation on).
        Assert.True(svc.ReadDefaultProfileStartupAnimation());
    }

    // The default profile is the template a new character is made from. A list of
    // what some character held, left on it, must not be handed to the new one.
    [Fact]
    public void CreateProfile_FromATemplateCarryingAHeldList_StartsWithNone()
    {
        string board = NewBoard();
        // A template from when a session on the default profile was saved into it.
        JsonStore.Save(Template, new Models.Profile.CharacterProfile
        {
            HeldAtDisconnect = new Models.Profile.HeldAtDisconnect
            {
                Items = { new Models.Profile.HeldItem("silver chalice", 1) },
            },
        });

        Service().CreateProfile(board, "Newcomer");

        Assert.Null(JsonStore.Load<Models.Profile.CharacterProfile>(
            AppPaths.CharacterProfileFile(board, "Newcomer"))!.HeldAtDisconnect);
    }

    // New profiles started with no GOTO favourites for a while, and a session on the
    // default profile saved that empty list into the template. The template can't be
    // changed now, so an empty list on it reads as "not taken": the draft, and a
    // character made from it, take the starters. A list with something in it is kept.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplateWithAnEmptyFavouritesList_LeavesTheListForTheStartersToFill(bool templateHasFavourites)
    {
        string board = NewBoard();
        JsonStore.Save(Template, new Models.Profile.CharacterProfile
        {
            Favorites = templateHasFavourites
                ? new() { new Models.Profile.FavoriteRoom { Map = 1, Room = 45, Label = "Bank" } }
                : new(),
            FavoriteFolders = new(),
        });

        ProfileService draft = Service();
        draft.LoadDefaultProfile();
        Service().CreateProfile(board, "Newcomer");
        Models.Profile.CharacterProfile made = JsonStore.Load<Models.Profile.CharacterProfile>(
            AppPaths.CharacterProfileFile(board, "Newcomer"))!;

        if (templateHasFavourites)
        {
            Assert.Single(draft.Current!.Favorites!);
            Assert.Single(made.Favorites!);
        }
        else
        {
            Assert.Null(draft.Current!.Favorites);
            Assert.Null(made.Favorites);
        }
    }

    // The update that stops the default profile being written puts it back to the
    // installed defaults, once (user, 2026-10-12). What it held is handed back, to
    // be loaded as an unnamed draft that asks to be named; no copy is left on disk.
    [Fact]
    public void ResetDefaultProfileOnce_PutsItBackToInstalledDefaults_HandsBackWhatItHeld_AndRunsOnce()
    {
        Models.Profile.CharacterProfile played = new()
        {
            Settings = new() { ["Probe"] = JsonSerializer.SerializeToElement(new { Flag = true }) },
        };
        JsonStore.Save(Template, played);

        ProfileService updated = Service();
        Models.Profile.CharacterProfile? held = updated.ResetDefaultProfileOnce();

        Assert.NotNull(held);
        Assert.True(held!.Settings!.ContainsKey("Probe"));
        Assert.False(File.Exists(Template));               // installed defaults: no file
        Assert.True(File.Exists(Marker));
        Assert.Equal(new[] { Path.GetFileName(Marker) },
            Directory.GetFiles(Path.GetDirectoryName(Template)!, "*default-profile*")
                .Select(Path.GetFileName));                                   // and no copy beside it

        // Loaded as the unnamed draft: the setup is there, and it asks to be kept.
        updated.LoadDraftFromBeforeTheReset(held);
        Assert.Null(updated.CurrentProfileName);
        Assert.True(updated.Current!.Settings!.ContainsKey("Probe"));
        Assert.True(updated.UnnamedDraftHasChanges);

        // A new draft is the installed defaults, and no later launch resets again.
        ProfileService later = Service();
        later.LoadDefaultProfile();
        Assert.False(later.Current!.Settings?.ContainsKey("Probe") == true);
        JsonStore.Save(Template, played);                  // (a hand edit, say)
        Assert.Null(later.ResetDefaultProfileOnce());
        Assert.True(File.Exists(Template));
    }

    // The startup animation switch is the one install-wide setting kept in this file.
    [Fact]
    public void ResetDefaultProfileOnce_KeepsTheStartupAnimationSetting()
    {
        ProfileService before = Service();
        before.WriteDefaultProfileStartupAnimation(false);

        Assert.NotNull(Service().ResetDefaultProfileOnce());

        Assert.False(Service().ReadDefaultProfileStartupAnimation());
    }

    // A fresh install has nothing to reset; it is marked done all the same.
    [Fact]
    public void ResetDefaultProfileOnce_OnAFreshInstall_ResetsNothing()
    {
        Assert.Null(Service().ResetDefaultProfileOnce());
        Assert.True(File.Exists(Marker));
    }
}
