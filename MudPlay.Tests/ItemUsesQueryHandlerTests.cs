using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only @uses handler + the shared CarriedChargeReadout it reads: a bare
// @uses lists every carried charged item (unread → "?"), a named @uses best-matches a
// shorthand, an infinite / non-charged item is reported as such, and a no-match is
// named back. Runs on a Paradigm set (look-derived counts).
public sealed class ItemUsesQueryHandlerTests : IDisposable
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private const string ItemsJson = """
        [
          { "Number": 10, "Name": "gnarled wand",  "UseCount": 10, "Retain After Uses": 0 },
          { "Number": 20, "Name": "peasant cloak", "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 30, "Name": "nexus spear",   "UseCount": -1, "Retain After Uses": 0 },
          { "Number": 50, "Name": "healing potion","UseCount": 0,  "Retain After Uses": 0 }
        ]
        """;
    private const string InfoParadigm = """[ { "Legit": 2 } ]""";

    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly ProfileService _profile;
    private readonly List<string> _carried = new();
    private readonly ItemChargeTracker _para;
    private readonly ItemUseCountTracker _stock;
    private readonly CarriedChargeReadout _readout;

    public ItemUsesQueryHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-uses-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "para"));
        File.WriteAllText(Path.Combine(_root, "para", "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(_root, "para", "Info.json"), InfoParadigm);
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("para");
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile();

        _para = new ItemChargeTracker(
            gameData: _cache, profile: _profile, heldItems: () => _carried, itemNumberOf: Number,
            onParadigm: () => _cache.ActiveRealm == RealmType.ParaMud,
            cleanupConfig: () => null, useConfirmLine: _ => null,
            sendLook: _ => { }, schedule: (_, _) => { }, now: () => Now, log: null);
        _stock = new ItemUseCountTracker(
            gameData: _cache, heldItems: () => _carried, itemNumberOf: Number,
            onStock: () => _cache.ActiveRealm != RealmType.ParaMud,
            cleanupConfig: () => null, profile: _profile,
            useConfirmLine: _ => null, schedule: (_, _) => { }, now: () => Now, log: null);
        _readout = new CarriedChargeReadout(_cache, _para, _stock, Number, () => _carried);
    }

    public void Dispose()
    {
        _readout.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static int Number(string name) => name switch
    {
        "gnarled wand" => 10,
        "peasant cloak" => 20,
        "nexus spear" => 30,
        "healing potion" => 50,
        _ => 0,
    };

    // Seed a known count for a carried item as if a look had landed.
    private void Seen(string lookArg, int uses)
    {
        _para.ObserveOutbound(Encoding.Latin1.GetBytes($"look {lookArg}\r"));
        _para.HandleLine($"Uses remaining: {uses}");
    }

    private (RemoteCommandManager engine, PlayerDatabase players) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        _ = new ItemUsesQueryHandler(engine, _readout);
        return (engine, players);
    }

    private static void Seed(PlayerDatabase db, string name)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryInventory));
    }

    private string Reply()
        => StripWire(Encoding.Latin1.GetString(Assert.Single(_replies)));

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{'); int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    // ----- @uses <item> ---------------------------------------------------

    [Fact]
    public void NamedShorthand_ReportsRemaining()
    {
        _carried.Add("gnarled wand");
        Seen("gnarled", 6);
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses gnarl"));   // shorthand best-match

        Assert.Equal("gnarled wand: 6 use(s) remaining", Reply());
    }

    [Fact]
    public void NamedChargedButUnread_SaysNotReadYet()
    {
        _carried.Add("peasant cloak");
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses cloak"));

        Assert.Equal("peasant cloak: charges not read yet", Reply());
    }

    [Fact]
    public void NamedInfinite_IsNotLimitedUse()
    {
        _carried.Add("nexus spear");
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses nexus"));

        Assert.Equal("nexus spear isn't a limited-use item", Reply());
    }

    [Fact]
    public void InfiniteUseItem_WithStrayRecordedCount_ReportsNoCharges()
    {
        // Regression: a mis-attributed "Uses remaining: 9998" once recorded against an
        // infinite-use item (nexus spear, UseCount -1) must not surface as charges — the
        // game-data limited-use flag wins over any stored value ("magical rune - 9998
        // Charges" bug).
        _carried.Add("nexus spear");
        Seen("nexus spear", 9998);   // stray look reply, recorded against its number

        Assert.Null(_readout.RemainingForName("nexus spear"));

        var (engine, players) = Engine();
        Seed(players, "Bob");
        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses nexus"));
        Assert.Equal("nexus spear isn't a limited-use item", Reply());
    }

    [Fact]
    public void NamedNonCharged_IsNotLimitedUse()
    {
        _carried.Add("healing potion");
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses potion"));

        Assert.Equal("healing potion isn't a limited-use item", Reply());
    }

    [Fact]
    public void NamedNoMatch_IsNamedBack()
    {
        _carried.Add("gnarled wand");
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses longsword"));

        Assert.Equal("no carried item matches 'longsword'", Reply());
    }

    // ----- bare @uses -----------------------------------------------------

    [Fact]
    public void Bare_ListsChargedItems_UnknownAsQuestionMark()
    {
        _carried.Add("gnarled wand");     // known 6
        _carried.Add("peasant cloak");    // charged, unread → ?
        _carried.Add("nexus spear");      // infinite — omitted
        _carried.Add("healing potion");   // not charged — omitted
        Seen("gnarled", 6);
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses"));

        Assert.Equal("item uses - gnarled wand: 6, peasant cloak: ?", Reply());
    }

    [Fact]
    public void Bare_NoChargedItems_SaysNone()
    {
        _carried.Add("nexus spear");
        _carried.Add("healing potion");
        var (engine, players) = Engine();
        Seed(players, "Bob");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses"));

        Assert.Equal("no limited-use items carried", Reply());
    }

    // Holding none is a result, not a failure: it is answered with denials
    // silenced too.
    [Fact]
    public void Bare_NoChargedItems_IsAnsweredWithDenialsSilenced()
    {
        var (engine, players) = Engine();
        Seed(players, "Bob");
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@uses"));

        Assert.Equal("no limited-use items carried", Reply());
    }

    // ----- readout best-match ---------------------------------------------

    [Fact]
    public void Readout_ResolvesExactOverSubstring()
    {
        _carried.Add("wand of fire");
        _carried.Add("wand");
        Assert.Equal("wand", _readout.ResolveCarried("wand"));         // exact wins
        Assert.Equal("wand of fire", _readout.ResolveCarried("wand of"));
    }

    [Fact]
    public void Readout_Gating_UnauthorisedSenderGetsNoReply()
    {
        _carried.Add("gnarled wand");
        Seen("gnarled", 6);
        var (engine, players) = Engine();
        engine.WarnOnDenial = false;
        players.RecordObservation("Stranger", null, null, null, null, null, null, Now);
        players.EditCustomization("Stranger",
            new PlayerCustomization(RemoteControls: PlayerRemoteControls.All & ~PlayerRemoteControls.QueryInventory));

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@uses"));

        Assert.Empty(_replies);
    }
}
