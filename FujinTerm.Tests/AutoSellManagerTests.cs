using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

// AutoSellManager: on a shop `list` header it sells each carried item flagged
// AutoSell down to its keep floor — one `sell` per copy, advancing off the live
// "You sold ..." / "You cannot sell ... here." result.
public sealed class AutoSellManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoSellManager Sell { get; }
        public List<byte[]> Sent { get; } = new();
        public List<string> Carried { get; } = new();
        public bool Enabled { get; set; } = true;

        // name -> (Number, Sell, KeepCount)
        private readonly Dictionary<string, (int Number, bool Sell, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Sell = new AutoSellManager(Router,
                carriedItems: () => Carried,
                resolve: Resolve,
                isEnabled: () => Enabled,
                log: Log);
            Sell.SetWireSender(b => Sent.Add(b));
        }

        public void Map(string name, int number, bool sell, int keep = 0)
            => _map[name] = (number, sell, keep);

        private AutoSellManager.ResolvedSell? Resolve(string entry)
            => _map.TryGetValue(entry.Trim(), out (int Number, bool Sell, int Keep) v)
                ? new AutoSellManager.ResolvedSell(v.Number, entry.Trim(), v.Sell, v.Keep)
                : null;

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void ShopHeader() => Feed("The following items are for sale here:");

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Sell.Dispose();
    }

    [Fact]
    public void FlaggedItem_SellsEveryCopy_OnePerResult()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.ShopHeader();
        Assert.Equal(new[] { "sell dagger" }, h.SentText);   // one at a time

        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");

        Assert.Equal(3, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("sell dagger", s));
    }

    [Fact]
    public void KeepFloor_LeavesMinimum()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true, keep: 1);
        h.Carried.AddRange(new[] { "dagger", "dagger", "dagger" });

        h.ShopHeader();
        h.Feed("You sold dagger for 5 gold crowns.");
        h.Feed("You sold dagger for 5 gold crowns.");
        // Would-be third result never comes because only two were queued.

        Assert.Equal(2, h.SentText.Count);   // 3 carried − 1 keep
    }

    [Fact]
    public void UnflaggedItem_NoSell()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: false);
        h.Carried.Add("dagger");

        h.ShopHeader();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ShopRefuses_AbandonsThatItem()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.ShopHeader();
        Assert.Single(h.Sent);                       // first sell attempt

        h.Feed("You cannot sell dagger here.");       // this shop won't buy it

        Assert.Single(h.Sent);                       // no further attempts
    }

    [Fact]
    public void DisabledMaster_NoSell()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, sell: true);
        h.Carried.Add("dagger");

        h.ShopHeader();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void FreshList_ResetsPump()
    {
        using Harness h = new();
        h.Map("dagger", 1, sell: true);
        h.Carried.AddRange(new[] { "dagger", "dagger" });

        h.ShopHeader();                               // sends sell #1
        h.ShopHeader();                               // fresh list supersedes

        // Both headers each start a pump from the current carried pack; the
        // second replaces the first rather than stacking a second queue.
        Assert.Equal(new[] { "sell dagger", "sell dagger" }, h.SentText);
    }
}
