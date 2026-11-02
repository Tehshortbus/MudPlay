using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A trigger is kept on the character, with the realm (every character playing it) or
// with the BBS (every character on it). Of triggers sharing a name the nearest fires.
// Each client holds a copy of the shared lists, takes in another client's change on
// the heartbeat, and makes its own changes to what is on disk.
public sealed class TriggerLevelTests : IDisposable
{
    private const string Bell = "A bell rings.";
    private readonly string _board = "test-triglevel-" + Guid.NewGuid().ToString("N")[..12];

    public TriggerLevelTests() => ScratchRoot.Require();

    public void Dispose()
    {
        try { if (Directory.Exists(AppPaths.BbsFolder(_board))) Directory.Delete(AppPaths.BbsFolder(_board), recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    // One client: a character saved on the board, its engine fed and sending as the
    // client's is, and its realm's and BBS's trigger files where AppServices puts them.
    private sealed class Client
    {
        public ProfileService Profile { get; } = TestProfiles.Loaded();
        public ServerFeed Feed { get; }
        public TriggerEngine Engine { get; }
        public List<string> Sent { get; } = new();
        public string Realm { get; set; }

        public Client(string board, string name, string realm = "Main")
        {
            Realm = realm;
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            Feed = new ServerFeed(router);
            Engine = new TriggerEngine(Profile, new ChatRouter(router), new LogService(), onUiThread: act => act());
            Engine.AttachLineExtractor(Feed.Lines);
            Engine.SetSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
            Profile.SaveAs(board, name);
            Engine.ShareWith(() => AppPaths.RealmTriggersFile(AppPaths.RealmFolder(board, Realm)),
                () => AppPaths.BbsTriggersFile(board));
        }

        public void Hear(string line)
        {
            Sent.Clear();
            Feed.Line(line);
        }
    }

    private static Trigger On(string pattern, string response, TriggerLevel level, string name = "greet", bool enabled = true) =>
        new(name, enabled, TriggerScope.GameMessages, TriggerMatchType.Literal, pattern, response) { Level = level };

    private static IEnumerable<string> Named(TriggerEngine engine, TriggerLevel level) =>
        engine.Triggers.Where(t => t.Level == level).Select(t => t.Name).Order();

    [Theory]
    [InlineData(true, true, true, "say mine")]
    [InlineData(false, true, true, "say realm")]
    [InlineData(false, false, true, "say board")]
    public void OfTriggersSharingAName_TheNearestFires(bool character, bool realm, bool bbs, string fired)
    {
        Client alpha = new(_board, "Alpha");
        if (bbs) Assert.True(alpha.Engine.Add(On(Bell, "say board", TriggerLevel.Bbs)));
        if (realm) Assert.True(alpha.Engine.Add(On(Bell, "say realm", TriggerLevel.Realm)));
        if (character) Assert.True(alpha.Engine.Add(On(Bell, "say mine", TriggerLevel.Character)));

        alpha.Hear(Bell);

        Assert.Equal(new[] { fired }, alpha.Sent);
    }

    // A character's own copy, switched off, silences the shared trigger for it alone.
    [Fact]
    public void AnOwnCopySwitchedOff_SilencesTheSharedTrigger_ForThatCharacterOnly()
    {
        Client alpha = new(_board, "Alpha");
        Client beta = new(_board, "Beta");
        alpha.Engine.Add(On(Bell, "say realm", TriggerLevel.Realm));
        alpha.Engine.Add(On(Bell, "say mine", TriggerLevel.Character, enabled: false));
        beta.Engine.TakeInOutsideChanges();

        alpha.Hear(Bell);
        beta.Hear(Bell);

        Assert.Empty(alpha.Sent);
        Assert.Equal(new[] { "say realm" }, beta.Sent);
    }

    // Another client's realm trigger works here at the next heartbeat; a BBS trigger
    // reaches a character on another realm of the board, a realm trigger doesn't.
    [Fact]
    public void SharedTriggers_ReachTheOtherClients_TheirRealmsOnly()
    {
        Client alpha = new(_board, "Alpha");
        Client beta = new(_board, "Beta");
        Client gamma = new(_board, "Gamma", realm: "Other");
        alpha.Engine.Add(On(Bell, "say realm", TriggerLevel.Realm, name: "realm"));
        alpha.Engine.Add(On(Bell, "say board", TriggerLevel.Bbs, name: "board"));

        beta.Engine.TakeInOutsideChanges();
        gamma.Engine.TakeInOutsideChanges();
        beta.Hear(Bell);
        gamma.Hear(Bell);

        Assert.Equal(new[] { "say realm", "say board" }, beta.Sent);
        Assert.Equal(new[] { "say board" }, gamma.Sent);
    }

    // Two clients adding before either has taken in the other's: both triggers stay.
    [Fact]
    public void TwoClientsChangingTheSharedList_KeepBothChanges()
    {
        Client alpha = new(_board, "Alpha");
        Client beta = new(_board, "Beta");

        alpha.Engine.Add(On(Bell, "say one", TriggerLevel.Realm, name: "one"));
        beta.Engine.Add(On("A gong sounds.", "say two", TriggerLevel.Realm, name: "two"));
        alpha.Engine.TakeInOutsideChanges();

        Assert.Equal(new[] { "one", "two" }, Named(alpha.Engine, TriggerLevel.Realm));
        Assert.Equal(new[] { "one", "two" }, Named(beta.Engine, TriggerLevel.Realm));
    }

    // A trigger moved to the realm leaves the character's profile and reaches the
    // other characters on the realm.
    [Fact]
    public void ATriggerMovedToTheRealm_LeavesTheProfile_AndReachesTheOthers()
    {
        Client alpha = new(_board, "Alpha");
        Client beta = new(_board, "Beta");
        Trigger mine = On(Bell, "say hi", TriggerLevel.Character);
        alpha.Engine.Add(mine);

        Assert.True(alpha.Engine.Replace(mine, mine with { Level = TriggerLevel.Realm }));
        alpha.Profile.Save();
        beta.Engine.TakeInOutsideChanges();
        beta.Hear(Bell);

        CharacterProfile saved = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_board, "Alpha"))!;
        Assert.DoesNotContain(saved.Triggers ?? new(), t => t.Name == "greet");
        Assert.Equal(new[] { "say hi" }, beta.Sent);
    }

    // Loading a character on another realm shows that realm's triggers, not the last.
    [Fact]
    public void ACharacterLoaded_OnAnotherRealm_HasThatRealmsTriggers()
    {
        Client alpha = new(_board, "Alpha");
        alpha.Engine.Add(On(Bell, "say main", TriggerLevel.Realm));
        Client gamma = new(_board, "Gamma", realm: "Other");
        gamma.Engine.Add(On(Bell, "say other", TriggerLevel.Realm));

        alpha.Realm = "Other";
        alpha.Profile.Load(_board, "Gamma");
        alpha.Hear(Bell);

        Assert.Equal(new[] { "say other" }, alpha.Sent);
    }

    // A shared file that can't be read isn't written over, so it can still be put
    // right by hand; the change isn't made, and says so.
    [Fact]
    public void ADamagedSharedFile_IsNotWrittenOver()
    {
        string path = AppPaths.RealmTriggersFile(AppPaths.RealmFolder(_board, "Main"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Client alpha = new(_board, "Alpha");

        Assert.False(alpha.Engine.Add(On(Bell, "say hi", TriggerLevel.Realm)));

        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.DoesNotContain(alpha.Engine.Triggers, t => t.Level == TriggerLevel.Realm);
    }

    // With no realm or BBS to share with, only the character's own list is offered.
    [Fact]
    public void WithNoRealmOrBbs_OnlyTheCharactersOwnListIsOffered()
    {
        Client alpha = new(_board, "Alpha");
        alpha.Engine.ShareWith(() => null, () => null);

        Assert.Equal(new[] { TriggerLevel.Character }, alpha.Engine.Levels);
        Assert.False(alpha.Engine.Add(On(Bell, "say hi", TriggerLevel.Realm)));
    }

    // A shared file put together by hand can leave a trigger's name out, which reads
    // back null; the list is keyed by name, so the character's load threw. The trigger
    // loads blank-named and still fires; a null in the list is passed over.
    [Fact]
    public void ASharedTriggerWithNoName_LoadsAndFires()
    {
        string path = AppPaths.RealmTriggersFile(AppPaths.RealmFolder(_board, "Main"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            [ { "Enabled": true, "Scope": "GameMessages", "MatchType": "Literal", "Pattern": "{{Bell}}", "Response": "say hi" }, null ]
            """);
        Client alpha = new(_board, "Alpha");

        Assert.True(alpha.Engine.Add(On("A gong sounds.", "say gong", TriggerLevel.Character, name: "gong")));
        alpha.Hear(Bell);

        Assert.Equal(new[] { "say hi" }, alpha.Sent);
        Trigger shared = Assert.Single(alpha.Engine.Triggers, t => t.Level == TriggerLevel.Realm);
        Assert.Equal("", shared.Name);
        Assert.False(alpha.Engine.IsOverridden(shared));
    }
}
