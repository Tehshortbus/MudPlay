using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// @auto-get <item>: a player with Alter settings reports, or sets for this
// character, one item's Auto-collect and Max to get (user, 2026-10-17). The change
// is saved at the character tier the Game Data item editor writes, and the
// auto-get engine collects by it from the next survey. A lone on / off stays the
// engine toggle.
public sealed class AutoGetItemRemoteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
    private const string Friend = "Friend";
    private const string Stranger = "Stranger";

    private readonly string _root;
    private readonly string _set = "autoget-remote-" + Path.GetRandomFileName();
    private readonly string _bbs = "autoget-remote-" + Path.GetRandomFileName();

    public AutoGetItemRemoteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-autoget-remote-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, _set));
        File.WriteAllText(Path.Combine(_root, _set, "Items.json"), ItemsJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
        try { File.Delete(AppPaths.OverrideFile(SettingsTier.Global, null, "Items", _set)); }
        catch { /* best-effort */ }
    }

    // Two records share the name "small shield": they count as one item. "rune on
    // stone" holds the word "on", "maxim scroll" the letters "max".
    private const string ItemsJson = """
        [
          { "Number": 101, "Name": "large shield", "Encum": 10, "ItemType": 2 },
          { "Number": 102, "Name": "large sword", "Encum": 10, "ItemType": 1 },
          { "Number": 103, "Name": "small shield", "Encum": 5, "ItemType": 2 },
          { "Number": 113, "Name": "small shield", "Encum": 5, "ItemType": 2 },
          { "Number": 104, "Name": "rune on stone", "Encum": 1, "ItemType": 0 },
          { "Number": 105, "Name": "maxim scroll", "Encum": 1, "ItemType": 0 },
          { "Number": 106, "Name": "king's crown", "Encum": 2, "ItemType": 2 },
          { "Number": 107, "Name": "stone wall", "Encum": 0, "ItemType": 0 },
          { "Number": 172, "Name": "black star key", "Encum": 1, "ItemType": 7 }
        ]
        """;

    private sealed class Rig : IDisposable
    {
        public required MessageRouter Router { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        public required ProfileService Profile { get; init; }
        public required SettingsResolver Resolver { get; init; }
        public required AutoModeController Controller { get; init; }
        public required AutoModeRemoteHandler Handler { get; init; }
        public required AutoGetItemsManager Items { get; init; }
        public required LogService Log { get; init; }
        public required ItemOverlaySeedStore Seed { get; init; }
        // Copies held, by item Number: what the engine's Max to get counts.
        public required Dictionary<int, int> Held { get; init; }
        public List<byte[]> Replies { get; } = new();
        public List<string> Game { get; } = new();

        public List<string> Sent => Replies.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Ask(string message, string sender = Friend, ChatChannel channel = ChatChannel.TelepathIncoming) =>
            ChatLines.Hear(Router, new ChatLine(channel, sender, message));

        public string Ask1(string message)
        {
            Replies.Clear();
            Ask(message);
            return Assert.Single(Sent);
        }

        public void Notice(string list)
        {
            Game.Clear();
            new ServerFeed(Router).Line($"You notice {list} here.");
        }

        public ItemOverlay Overlay(int number) =>
            Resolver.ResolveGameData("Items", number.ToString(), Seed.GetOverlay(number));

        public SettingsTier TierOf(int number) => Resolver.GetGameDataSourceTier("Items", number.ToString());

        public void Dispose()
        {
            Items.Dispose();
            Handler.Dispose();
            Engine.Dispose();
        }
    }

    // A named character on its own BBS and realm (the character and realm tiers
    // need them), unless named is false: then the unnamed default draft, which has
    // no tier of its own. Seeded, the installed defaults are the stock seed the
    // client ships; otherwise every item's are blank.
    private Rig Build(bool named = true, bool seeded = false)
    {
        GameDataCache cache = new(_root);
        cache.SwitchSet(_set);
        ItemNameStore names = new(cache);
        names.OnActiveSetChanged(_set);

        ProfileService profile = TestProfiles.Service();
        BbsProfileStore boards = new();
        if (named)
        {
            boards.Save(new BbsProfile { Name = _bbs, Host = "h", Realms = { new RealmProfile { Name = "home" } } });
            profile.CreateProfile(_bbs, "Tester");
            profile.Load(_bbs, "Tester");
        }
        else
        {
            profile.LoadDefaultProfile();
        }
        SettingsResolver resolver = new(new SettingsService(), boards, profile, () => cache.ActiveSet);
        ItemOverlaySeedStore seed = new();
        if (seeded)
        {
            InstallStockSeed();
            seed.Load(_set);
        }

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation(Friend, null, null, null, null, null, null, Now);
        players.EditCustomization(Friend, new PlayerCustomization(RemoteControls: PlayerRemoteControls.AlterSettings));
        players.RecordObservation(Stranger, null, null, null, null, null, null, Now);
        players.EditCustomization(Stranger, new PlayerCustomization(
            RemoteControls: PlayerRemoteControls.All & ~PlayerRemoteControls.AlterSettings));
        AutoModeController controller = new(profile);
        LogService log = new();
        RemoteCommandManager engine = new(new ChatRouter(router), party, players)
        {
            BlockedByMasterSwitch = what => controller.Blocks("Remote commands", what),
        };
        AutoModeRemoteHandler handler = new(engine, profile, controller, log);
        handler.SetItemRules(new AutoGetItemRules(resolver, names, seed));

        Dictionary<int, int> held = new();
        AutoGetItemsManager items = new(router,
            resolve: entry =>
            {
                // How the client resolves a floor entry: the name to its Number, then
                // the item's rules through every tier.
                if (names.FindByName(entry) is not int number) return null;
                ItemOverlay o = resolver.ResolveGameData("Items", number.ToString(), seed.GetOverlay(number));
                return new AutoGetItemsManager.ResolvedItem(number, names.GetName(number)!,
                    o.AutoCollect ?? false, o.CannotBeTaken ?? false,
                    ItemOverlay.ParseCount(o.MaxToGet, int.MaxValue), 0);
            },
            isEnabled: () => true,
            collectAfterCombatFinished: () => false,
            hasEngageableHostiles: () => false,
            heldCount: n => held.GetValueOrDefault(n));

        Rig rig = new()
        {
            Router = router, Engine = engine, Profile = profile, Resolver = resolver,
            Controller = controller, Handler = handler, Items = items, Log = log, Held = held, Seed = seed,
        };
        items.SetWireSender(b => rig.Game.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        engine.SetWireSender(rig.Replies.Add);
        return rig;
    }

    private static string Reply(string text) => $"/{Friend} {{{text}}}";

    // The stock item seed where the client keeps it, as its startup puts it there:
    // copied from the seed the client ships, never changed.
    private void InstallStockSeed()
    {
        string target = AppPaths.ItemOverlaySeedFile("stock");
        if (File.Exists(target)) return;
        string shipped = Path.Combine(_root, "shipped");
        AppPaths.ExtractEmbeddedSeeds(shipped);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try { File.Copy(Path.Combine(shipped, "ItemOverlay.stock.seed.json"), target); }
        catch (IOException) when (File.Exists(target)) { /* another test put the same copy there first */ }
    }

    // ----- Report ------------------------------------------------------------------

    [Fact]
    public void AnItem_IsReportedWithItsRules_AndNothingIsWritten()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
    }

    // The report reads what the engine reads: a rule from a lower tier, and a hard
    // never-pick-up flag.
    [Fact]
    public void AReport_ReadsEveryTier_AndSaysWhenTheItemCannotBeTaken()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "107",
            new ItemOverlay { AutoCollect = true, MaxToGet = "3", CannotBeTaken = true });

        Assert.Equal(Reply("stone wall: auto-get on, max 3 (cannot be taken)"), r.Ask1("@auto-get stone wall"));
    }

    // The engine stops at one for a Carry only one item whatever Max to get says, so
    // the report says so rather than leave "max none" to read as every copy.
    [Fact]
    public void AReport_SaysWhenTheItemIsCarryOnlyOne()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "107",
            new ItemOverlay { AutoCollect = true, CarryOnlyOne = true });

        Assert.Equal(Reply("stone wall: auto-get on, max none (carry only one)"), r.Ask1("@auto-get stone wall"));
    }

    // ----- Changes -----------------------------------------------------------------

    [Fact]
    public void On_IsSavedForThisCharacter_AndTheEngineCollectsTheItem()
    {
        using Rig r = Build();
        r.Notice("a large shield");
        Assert.Empty(r.Game);

        Assert.Equal(Reply("large shield: auto-get on, max none"), r.Ask1("@auto-get large shield on"));

        Assert.True(r.Overlay(101).AutoCollect);
        Assert.Equal(SettingsTier.Character, r.TierOf(101));
        Assert.False(File.Exists(AppPaths.OverrideFile(SettingsTier.Global, null, "Items", _set)));
        r.Notice("a large shield");
        Assert.Equal(new[] { "get large shield" }, r.Game);
    }

    [Fact]
    public void Off_IsSavedAsOff_OverAGlobalTick_AndTheEngineLeavesTheItem()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new ItemOverlay { AutoCollect = true });
        r.Notice("a large shield");
        Assert.Equal(new[] { "get large shield" }, r.Game);

        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield off"));

        Assert.False(r.Overlay(101).AutoCollect);
        Assert.Equal(SettingsTier.Character, r.TierOf(101));
        r.Notice("a large shield");
        Assert.Empty(r.Game);
    }

    // Max to get counts the copies held (carried, worn, on the key ring): the engine
    // collects until that many are held.
    [Fact]
    public void Max_SetsHowManyTheEngineHoldsAtMost()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get on, max 2"), r.Ask1("@auto-get large shield on max 2"));
        Assert.Equal("2", r.Overlay(101).MaxToGet);

        r.Held[101] = 1;
        r.Notice("3 large shield");
        Assert.Equal(new[] { "get large shield" }, r.Game);

        r.Held[101] = 2;
        r.Notice("a large shield");
        Assert.Empty(r.Game);
    }

    [Fact]
    public void MaxZero_IsKept_AndTheEngineTakesNone()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get on, max 0"), r.Ask1("@auto-get large shield on max 0"));

        r.Notice("a large shield");
        Assert.Empty(r.Game);
    }

    [Fact]
    public void TheSwitchAndTheMax_ReadEitherWayRound()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get on, max 4"), r.Ask1("@auto-get large shield max 4 on"));
        Assert.Equal(Reply("large shield: auto-get off, max 1"), r.Ask1("@auto-get large shield off max 1"));
    }

    // A max alone leaves Auto-collect as it was, and a switch alone leaves the max.
    [Fact]
    public void MaxAlone_And_SwitchAlone_LeaveTheOtherRule()
    {
        using Rig r = Build();
        r.Ask("@auto-get large shield on");

        Assert.Equal(Reply("large shield: auto-get on, max 2"), r.Ask1("@auto-get large shield max 2"));
        Assert.Equal(Reply("large shield: auto-get off, max 2"), r.Ask1("@auto-get large shield off"));
        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield max none"));
        Assert.Null(r.Overlay(101).MaxToGet);
    }

    // As the editor's save does: a record brought back to its installed defaults
    // loses its character override.
    [Fact]
    public void MaxNone_BackToTheDefaults_ClearsTheCharactersOverride()
    {
        using Rig r = Build();
        r.Ask("@auto-get large shield max 2");
        Assert.Equal(SettingsTier.Character, r.TierOf(101));

        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield max none"));

        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
    }

    // Clearing the override would let a Global max show through: the change asked
    // for this character holds for it all the same.
    [Fact]
    public void MaxNone_OverAGlobalMax_IsKeptForThisCharacter()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new ItemOverlay { MaxToGet = "5" });

        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield max none"));

        Assert.Null(r.Overlay(101).MaxToGet);
        Assert.Equal(SettingsTier.Character, r.TierOf(101));
    }

    [Fact]
    public void AChange_IsLoggedWithTheSendersName()
    {
        using Rig r = Build();

        r.Ask("@auto-get large shield on max 2");

        Assert.Contains(r.Log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message == "@auto-get from Friend: 'large shield' (#101) for this character: auto-get off -> on, max none -> 2");
    }

    // The command changes the fields it names and nothing else: a rule set for all
    // characters after the last change is not uncovered by the next.
    [Fact]
    public void AChange_LeavesEveryOtherFieldAsItResolved()
    {
        using Rig r = Build();
        r.Ask("@auto-get large shield max 2");
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101",
            new ItemOverlay { MinToKeep = "3", AutoSell = true });
        ItemOverlay before = r.Overlay(101);

        r.Ask("@auto-get large shield max none");

        Assert.Equal(before with { MaxToGet = null }, r.Overlay(101));
    }

    // Only the fields the command names are written at the character tier, so a
    // later Global change to any other still shows through.
    [Fact]
    public void FieldsNotNamed_StayUnwritten()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new Dictionary<string, object> { ["AutoSell"] = true });

        r.Ask("@auto-get large shield on");

        Assert.Equal(new[] { "AutoCollect" },
            r.Resolver.ReadGameDataAt(SettingsTier.Character, "Items", "101")!.Select(p => p.Key));
        r.Resolver.ClearGameDataAt(SettingsTier.Global, "Items", "101");
        Assert.False(r.Overlay(101).AutoSell ?? false);
    }

    // What the character tier already holds for another field stays.
    [Fact]
    public void AHeldValueOnAnotherField_Survives()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new Dictionary<string, object> { ["AutoSell"] = true });
        r.Resolver.WriteGameDataAt(SettingsTier.Character, "Items", "101", new Dictionary<string, object> { ["AutoSell"] = false });

        r.Ask("@auto-get large shield max 2");

        Assert.False(r.Overlay(101).AutoSell);
        Assert.Equal("2", r.Overlay(101).MaxToGet);
    }

    // A change is this character's own: it holds when the lower tier's rule it
    // matched goes away.
    [Fact]
    public void On_OverAGlobalTick_IsPinnedForThisCharacter()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new ItemOverlay { AutoCollect = true, MaxToGet = "4" });

        r.Ask("@auto-get large shield on max 4");
        r.Resolver.ClearGameDataAt(SettingsTier.Global, "Items", "101");

        Assert.True(r.Overlay(101).AutoCollect);
        Assert.Equal("4", r.Overlay(101).MaxToGet);
        Assert.Equal(SettingsTier.Character, r.TierOf(101));
    }

    [Fact]
    public void ARealmRule_DoesNotShowThroughAChange()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Bbs, "Items", "101", new ItemOverlay { AutoCollect = true, MaxToGet = "5" });

        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield off max none"));

        Assert.Equal(SettingsTier.Character, r.TierOf(101));
        r.Notice("a large shield");
        Assert.Empty(r.Game);
    }

    // An item the client ships flagged: its installed defaults are the seed's, and
    // setting it back to them leaves no override.
    [Fact]
    public void ASeededItem_ReadsItsSeed_AndBackToItShiftsToTheInstalledDefaults()
    {
        using Rig r = Build(seeded: true);

        Assert.Equal(Reply("black star key: auto-get on, max 2"), r.Ask1("@auto-get black star key"));
        Assert.Equal(Reply("black star key: auto-get off, max 5"), r.Ask1("@auto-get black star key off max 5"));
        Assert.Equal(SettingsTier.Character, r.TierOf(172));

        Assert.Equal(Reply("black star key: auto-get on, max 2"), r.Ask1("@auto-get black star key on max 2"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(172));
    }

    // ----- An item editor open meanwhile ------------------------------------------

    // The Game Data item editor as the Items tab opens it on the shield.
    private static ItemEditDialogViewModel Editor(Rig r) => new(
        wccNoStr: "101", mdbName: "large shield", existing: r.Overlay(101),
        currentTier: r.TierOf(101), mdbInfo: Array.Empty<KeyValuePair<string, string>>(),
        shops: Array.Empty<ShopSaleRow>(),
        writableTiers: [SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global]);

    private static async Task Ok(Rig r, ItemEditDialogViewModel vm)
    {
        ItemEditResult? result = null;
        vm.CloseRequested += x => result = x;
        vm.SaveCommand.Execute(null);
        await GameDataOverrideApplier.ApplyItemAsync(r.Resolver, new ConfirmService(new DialogService()),
            result!.WccNoStr, result.Tier, result.Overlay, r.Seed.GetOverlay(101), result.Changed, result.MovedFrom);
    }

    // A box changed in the editor and another changed by @auto-get while it was
    // open: OK keeps both (user, 2026-10-17).
    [Fact]
    public async Task AnOpenEditorsOk_KeepsARemoteChangeMadeWhileItWasOpen()
    {
        using Rig r = Build();
        ItemEditDialogViewModel vm = Editor(r);
        vm.AutoStash = true;

        r.Ask("@auto-get large shield on max 2");
        await Ok(r, vm);

        ItemOverlay now = r.Overlay(101);
        Assert.True(now.AutoStash);
        Assert.True(now.AutoCollect);
        Assert.Equal("2", now.MaxToGet);
    }

    // The same with Use moved to the character tier: the remote change isn't
    // copied over with the stale value the window shows.
    [Fact]
    public async Task AnOpenEditorMovedToAnotherTier_KeepsARemoteChangeMadeWhileItWasOpen()
    {
        using Rig r = Build();
        r.Resolver.WriteGameDataAt(SettingsTier.Global, "Items", "101", new Dictionary<string, object> { ["AutoStash"] = true });
        ItemEditDialogViewModel vm = Editor(r);
        Assert.Equal(SettingsTier.Global, vm.UseTier);
        vm.UseTier = SettingsTier.Character;

        r.Ask("@auto-get large shield max 2");
        await Ok(r, vm);

        Assert.Equal("2", r.Overlay(101).MaxToGet);
        Assert.True(r.Overlay(101).AutoStash);
    }

    // The same box changed in both: the editor's OK wins.
    [Fact]
    public async Task TheSameBoxChangedInBoth_TheEditorWins()
    {
        using Rig r = Build();
        ItemEditDialogViewModel vm = Editor(r);
        vm.MaxToGet = "5";

        r.Ask("@auto-get large shield max 2");
        await Ok(r, vm);

        Assert.Equal("5", r.Overlay(101).MaxToGet);
    }

    // ----- Names -------------------------------------------------------------------

    [Fact]
    public void ANameHoldingOn_IsStillAnItem()
    {
        using Rig r = Build();

        Assert.Equal(Reply("rune on stone: auto-get off, max none"), r.Ask1("@auto-get rune on stone"));
        Assert.Equal(Reply("rune on stone: auto-get on, max none"), r.Ask1("@auto-get rune on stone on"));
        Assert.True(r.Overlay(104).AutoCollect);
    }

    [Fact]
    public void ANameHoldingMax_IsStillAnItem()
    {
        using Rig r = Build();

        Assert.Equal(Reply("maxim scroll: auto-get on, max 3"), r.Ask1("@auto-get maxim scroll on max 3"));
    }

    [Fact]
    public void TheWordsMayComeInAnyOrder_AndApostrophesMayBeLeftOut()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get on, max none"), r.Ask1("@auto-get shield large on"));
        Assert.Equal(Reply("king's crown: auto-get on, max none"), r.Ask1("@auto-get kings crown on"));
        Assert.True(r.Overlay(106).AutoCollect);
    }

    // An article and a plural still name the item exactly, and two records of one
    // name are one item: the one the engine resolves the name to.
    [Fact]
    public void AnExactName_WinsOutright_AndASharedNameIsOneItem()
    {
        using Rig r = Build();

        Assert.Equal(Reply("large shield: auto-get on, max none"), r.Ask1("@auto-get a large shield on"));
        Assert.Equal(Reply("small shield: auto-get on, max none"), r.Ask1("@auto-get small on"));
        Assert.True(r.Overlay(103).AutoCollect);
        r.Notice("a small shield");
        Assert.Equal(new[] { "get small shield" }, r.Game);
    }

    [Fact]
    public void ANameThatFitsSeveralItems_IsRefused_AndNothingIsWritten()
    {
        using Rig r = Build();

        Assert.Equal(Reply("item name 'shield' matches 2: 'large shield', 'small shield'"), r.Ask1("@auto-get shield on"));
        Assert.Equal(Reply("item name 'large' matches 2: 'large shield', 'large sword'"), r.Ask1("@auto-get large"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(103));
    }

    // ----- The engine toggle -------------------------------------------------------

    [Fact]
    public void ALoneOnOrOff_AndNoWordAtAll_AreStillTheEngineToggle()
    {
        using Rig r = Build();
        bool Engine() => r.Profile.Current!.Settings is { } s && s.TryGetValue("General", out var json)
            && System.Text.Json.JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())!.AutoMode.AutoGetItems;

        Assert.Equal(Reply("@auto-get: off"), r.Ask1("@auto-get off"));
        Assert.False(Engine());
        Assert.Equal(Reply("@auto-get: on"), r.Ask1("@auto-get ON"));
        Assert.True(Engine());
        Assert.Equal(Reply("@auto-get: off"), r.Ask1("@auto-get"));
        Assert.False(Engine());

        // And an item change leaves the toggle alone.
        r.Ask("@auto-get large shield on");
        Assert.False(Engine());
        Assert.True(r.Overlay(101).AutoCollect);
    }

    // A first word of on / off is the engine toggle, and what follows is ignored
    // (user, 2026-10-17).
    [Fact]
    public void OnOrOffFirst_TogglesTheEngine_WhateverFollows()
    {
        using Rig r = Build();

        Assert.Equal(Reply("@auto-get: off"), r.Ask1("@auto-get off please"));
        Assert.Equal(Reply("@auto-get: on"), r.Ask1("@auto-get on please"));
        Assert.Equal(Reply("@auto-get: off"), r.Ask1("@auto-get off large shield max 2"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
    }

    // ----- Refusals ----------------------------------------------------------------

    public static TheoryData<string, string> Refusals => new()
    {
        { "@auto-get banana on", "no item named 'banana'" },
        { "@auto-get banana", "no item named 'banana'" },
        { "@auto-get large shield max lots", "bad max 'lots': give a number or none" },
        { "@auto-get large shield max -1", "bad max '-1': give a number or none" },
        { "@auto-get large shield on max 99999999999", "bad max '99999999999': give a number or none" },
        { "@auto-get large shield max 10000", "bad max '10000': give a number or none" },
        { "@auto-get large shield max", "max needs a number or none" },
        { "@auto-get max 2", "usage: @auto-get [on|off] or @auto-get <item> [on|off] [max <n>|none]" },
        { "@auto-get shield off", "item name 'shield' matches 2: 'large shield', 'small shield'" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AFailure_IsRefusedWithItsReason_AndNothingIsWritten(string command, string reason)
    {
        using Rig r = Build();

        Assert.Equal(Reply(reason), r.Ask1(command));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(103));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void WarnOff_AFailure_IsAnsweredWithNothing(string command, string reason)
    {
        _ = reason;
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        r.Ask(command);

        Assert.Empty(r.Replies);
    }

    [Fact]
    public void WarnOff_ASuccess_IsStillAnswered()
    {
        using Rig r = Build();
        r.Engine.WarnOnDenial = false;

        Assert.Equal(Reply("large shield: auto-get on, max none"), r.Ask1("@auto-get large shield on"));
    }

    // The unnamed draft has no character tier: a change is refused, a report is not.
    [Fact]
    public void NoCharacterLoaded_AChangeIsRefused_AReportIsAnswered()
    {
        using Rig r = Build(named: false);

        Assert.Equal(Reply("no character loaded"), r.Ask1("@auto-get large shield on"));
        Assert.Equal(Reply("no character loaded"), r.Ask1("@auto-get large shield max 2"));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
        Assert.Equal(Reply("large shield: auto-get off, max none"), r.Ask1("@auto-get large shield"));
    }

    [Fact]
    public void WithoutAlterSettings_TheFailureMessage_AndNothingIsWritten()
    {
        using Rig r = Build();

        r.Ask("@auto-get large shield on", Stranger);

        Assert.Equal($"/{Stranger} {{{r.Engine.FailureMessage}}}", Assert.Single(r.Sent));
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
    }

    [Fact]
    public void TheReply_GoesBackOnTheChannelTheCommandCameIn()
    {
        using Rig r = Build();

        r.Ask("@auto-get large shield on", channel: ChatChannel.Gangpath);
        r.Ask("@auto-get banana", channel: ChatChannel.Gangpath);

        Assert.Equal(new[] { "bg {large shield: auto-get on, max none}", "bg {no item named 'banana'}" }, r.Sent);
    }

    [Fact]
    public void MasterSwitchOff_IsIgnored_WithNoAnswer()
    {
        using Rig r = Build();
        r.Controller.TurnOff("test");

        r.Ask("@auto-get large shield on max 2");
        r.Ask("@auto-get large shield");

        Assert.Empty(r.Replies);
        Assert.Equal(SettingsTier.Defaults, r.TierOf(101));
    }
}
