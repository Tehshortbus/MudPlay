using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Anomaly's gang satchel: `i` lists only the satchel, so the house keys, emblems and
// keyrings in it are read off a look at it and join the inventory snapshot. Without
// them a door that needs the house key reads as locked to us.
public sealed class GangSatchelTests
{
    private sealed class Harness : IDisposable
    {
        public ServerFeed Server { get; } = new();
        public InventoryManager Inv { get; }
        public GangSatchel Satchel { get; }
        public List<string> Sent { get; } = new();
        public bool Held { get; set; }

        public Harness()
        {
            Inv = new InventoryManager();
            Inv.AttachLineExtractor(Server.Lines);
            Satchel = new GangSatchel(() => Held, Sent.Add);
            Satchel.AttachLineExtractor(Server.Lines);
            Satchel.Follow(Inv);
        }

        public void Inventory(string items = "4 copper farthings, sapphire emblem (Worn), gang satchel, lantern")
        {
            Server.Line($"You are carrying {items}");
            Server.Line("You have the following keys: 2 black star key.");
            Server.Line("Wealth: 4 copper farthings");
            Server.Line("Encumbrance: 1251/7068 - Light [17%]");
            Server.Prompt();
        }

        public void Look()
        {
            Server.Line("A satchel of dark leather, stitched with a ring of tiny keys. Everything of");
            Server.Line("your gang's houses rides in it.");
            Server.Line("Your gang satchel holds:");
            Server.Line("Arlysia Silver House: silver emblem, silver key, silver keyring");
            Server.Line("Fellgate Sapphire Tent: blue key, blue keyring, sapphire emblem (worn)");
            Server.Line("Silvermere Diamond House: white keyring, diamond emblem, white key, 2 white");
            Server.Line("deed");
            Server.Prompt();
        }

        public void Dispose() => Inv.Dispose();
    }

    [Fact]
    public void AnInventoryListingTheSatchel_LooksInItOnce_AndItsContentsAreHeld()
    {
        using Harness h = new();
        h.Inventory();
        h.Inventory();
        Assert.Equal(new[] { GangSatchel.LookCommand }, h.Sent);

        h.Look();

        InventorySnapshot snap = h.Inv.Snapshot;
        Assert.Equal(new[]
        {
            "silver emblem", "silver key", "silver keyring", "blue key", "blue keyring",
            "white keyring", "diamond emblem", "white key", "2 white deed",
        }, snap.Satchel);
        Assert.True(snap.Has("blue key"));
        Assert.Equal(3, h.Satchel.Houses.Count);
        Assert.Equal("Fellgate Sapphire Tent", h.Satchel.Houses[1].Name);

        h.Inventory();
        Assert.Single(h.Sent);
    }

    [Fact]
    public void ALookOwedWhileSendingIsHeld_GoesOutOnRetry()
    {
        using Harness h = new() { Held = true };
        h.Inventory();
        Assert.Empty(h.Sent);
        Assert.True(h.Satchel.LookOwed);

        h.Held = false;
        h.Satchel.Retry();
        Assert.Equal(new[] { GangSatchel.LookCommand }, h.Sent);
    }

    [Fact]
    public void APickUpTheNextInventoryDoesNotList_LooksAgain_AndADropLeavesTheSatchel()
    {
        using Harness h = new();
        h.Inventory();
        h.Look();

        h.Server.Line("You took lantern.");
        h.Server.Line("You took bronze key.");
        h.Inventory();
        Assert.Equal(new[] { GangSatchel.LookCommand, GangSatchel.LookCommand }, h.Sent);

        h.Server.Line("You dropped silver key.");
        Assert.DoesNotContain("silver key", h.Inv.Snapshot.Satchel!);
        Assert.Contains("silver keyring", h.Inv.Snapshot.Satchel!);
    }

    [Fact]
    public void AnInventoryWithoutTheSatchel_LetsItsContentsGo()
    {
        using Harness h = new();
        h.Inventory();
        h.Look();

        h.Inventory("4 copper farthings, lantern");
        Assert.Null(h.Inv.Snapshot.Satchel);
        Assert.False(h.Inv.Snapshot.Has("silver key"));
    }
}
