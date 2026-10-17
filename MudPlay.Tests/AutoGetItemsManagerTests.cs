using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 9.L — <see cref="AutoGetItemsManager"/> resolves each room
/// "You notice ... here." entry against game data and sends
/// <c>get &lt;name&gt;</c> only for items the user flagged
/// AutoCollect, gated by the AutoGetItems master toggle and the
/// collect-after-combat timing choice.
/// </summary>
public sealed class AutoGetItemsManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoGetItemsManager Items { get; }
        public List<byte[]> Sent { get; } = new();

        // canonical name (lower-cased, article-stripped) -> AutoCollect.
        // An entry absent from the map resolves to null (not an item).
        public Dictionary<string, bool> Flags { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // canonical name -> CannotBeTaken. Absent means not flagged.
        public Dictionary<string, bool> NoTake { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // canonical name -> MaxToGet cap. Absent means unbounded (int.MaxValue).
        public Dictionary<string, int> Caps { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // canonical name -> assigned item Number, so the held-count map can key
        // off the same identity the resolver emits.
        public Dictionary<string, int> Numbers { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // item Number -> currently-held copies (carried + worn + key ring).
        public Dictionary<int, int> Held { get; } = new();

        // canonical name -> carry weight (MDB Encum). Absent means 0 (ungated).
        public Dictionary<string, int> Weights { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Enabled { get; set; } = true;
        public bool CollectAfterCombat { get; set; }
        public bool HasHostiles { get; set; }
        public bool PeekSuppressed { get; set; }
        public bool Paradigm { get; set; }

        // Encumbrance reading + item bracket gates the manager reads each survey.
        // Default Empty (MaxWeight 0) disables the gate — the pre-existing tests
        // set no weights and expect ungated collection.
        public EncumbranceReading Enc { get; set; } = EncumbranceReading.Empty;
        // canonical names Auto-sell would sell, and what the coin drop answers when
        // asked to free weight for one (each ask is recorded).
        public HashSet<string> AutoSell { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool CoinDropFrees { get; set; }
        public List<(long Weight, string Item)> CoinDropAsks { get; } = new();
        public bool GateLight { get; set; }
        public bool GateMedium { get; set; }
        public bool GateHeavy { get; set; }
        public bool Gate90 { get; set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Items = new AutoGetItemsManager(Router,
                resolve: Resolve,
                isEnabled: () => Enabled,
                collectAfterCombatFinished: () => CollectAfterCombat,
                hasEngageableHostiles: () => HasHostiles,
                isPeekSuppressed: () => PeekSuppressed,
                heldCount: id => Held.GetValueOrDefault(id),
                encumbrance: () => Enc,
                itemEncGates: () => (GateLight, GateMedium, GateHeavy, Gate90),
                log: Log,
                isParadigm: () => Paradigm,
                dropCoinForWeight: (weight, item) => { CoinDropAsks.Add((weight, item)); return CoinDropFrees; });
            Items.SetWireSender(b => Sent.Add(b));
        }

        // Give a name a stable item Number for cap tests. Non-cap tests never
        // call this — everything resolves to Number 0 (fine, they set no cap).
        public int NumberFor(string name)
        {
            string key = Strip(name);
            if (!Numbers.TryGetValue(key, out int n))
            {
                n = Numbers.Count + 1;
                Numbers[key] = n;
            }
            return n;
        }

        private AutoGetItemsManager.ResolvedItem? Resolve(string entry)
        {
            string key = Strip(entry);
            if (!Flags.TryGetValue(key, out bool auto)) return null;
            bool noTake = NoTake.GetValueOrDefault(key);
            int number = Numbers.GetValueOrDefault(key);
            int cap = Caps.TryGetValue(key, out int c) ? c : int.MaxValue;
            int weight = Weights.GetValueOrDefault(key);
            return new AutoGetItemsManager.ResolvedItem(number, key, auto, noTake, cap, weight, AutoSell.Contains(key));
        }

        private static string Strip(string raw)
        {
            string s = raw.Trim().ToLowerInvariant();
            foreach (string a in new[] { "the ", "an ", "a ", "some " })
            {
                if (s.StartsWith(a, StringComparison.Ordinal))
                {
                    s = s[a.Length..];
                    break;
                }
            }
            // Mirror ItemNameStore.Normalize: also drop a leading count token so a
            // stacked pile ("5 piece of amber") resolves to the same item as the
            // singular ("piece of amber").
            int sp = s.IndexOf(' ');
            if (sp > 0)
            {
                string first = s[..sp];
                bool digits = first.Length > 0 && first.All(char.IsDigit);
                bool spelled = first is "one" or "two" or "three" or "four" or "five"
                    or "six" or "seven" or "eight" or "nine" or "ten";
                if (digits || spelled) s = s[(sp + 1)..].TrimStart();
            }
            return s.Trim();
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();

        public void Dispose() => Items.Dispose();
    }

    [Fact]
    public void FlaggedItem_SendsGet()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");

        Assert.Single(h.Sent);
        Assert.Equal("get long sword", h.SentText[0]);
    }

    [Fact]
    public void UnflaggedItem_NoSend()
    {
        using Harness h = new();
        h.Flags["long sword"] = false;

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DuringRoombaSweep_NoSend_EvenWhenFlagged()
    {
        // A Roomba sweep is sorting the house: auto-collect is held off so it can't
        // eat the carry headroom Roomba budgets for its moves. Resumes when the
        // sweep ends.
        using Harness h = new();
        h.Flags["long sword"] = true;
        h.Items.SuppressDuringSweep = () => true;

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CannotBeTaken_NeverSends_EvenWhenAutoCollect()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;      // user wants auto-collect...
        h.NoTake["long sword"] = true;     // ...but the item is never-take.

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void UnknownEntry_NoSend()
    {
        using Harness h = new();
        // No flag entry — e.g. a cash line that isn't an item in Items.json.

        h.Feed("You notice 50 gold sovereigns here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DisabledMaster_NoSend()
    {
        using Harness h = new() { Enabled = false };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MultipleItems_SendsEachFlagged()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;
        h.Flags["torch"] = false;
        h.Flags["shield"] = true;

        h.Feed("You notice a long sword, a torch and a shield here.");

        Assert.Equal(new[] { "get long sword", "get shield" }, h.SentText);
    }

    [Fact]
    public void CollectAfterCombat_DefersUntilRoomClears()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");
        Assert.Empty(h.Sent);                 // deferred — still fighting

        // Combat ends: no engageable hostiles remain, room re-observed.
        h.HasHostiles = false;
        h.Items.OnRoomObserved();

        Assert.Single(h.Sent);
        Assert.Equal("get long sword", h.SentText[0]);
    }

    [Fact]
    public void CollectAfterCombat_StillFighting_StaysDeferred()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");
        h.Items.OnRoomObserved();             // hostiles still present

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CollectAfterCombat_NoHostiles_SendsImmediately()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = false };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");

        Assert.Single(h.Sent);
        Assert.Equal("get long sword", h.SentText[0]);
    }

    [Fact]
    public void RoomChanged_DiscardsDeferredQueue()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");   // deferred
        h.Items.OnRoomChanged();                   // left the room

        h.HasHostiles = false;
        h.Items.OnRoomObserved();                  // nothing to flush

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void PeekSuppressed_NoSend()
    {
        // A look-direction peek renders a full "You notice" survey for the
        // adjacent room; getting from a room we never entered is the bug.
        using Harness h = new() { PeekSuppressed = true };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void PeekCleared_RealEntry_Sends()
    {
        using Harness h = new() { PeekSuppressed = true };
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");    // peeked — dropped
        Assert.Empty(h.Sent);

        h.PeekSuppressed = false;                    // walked in for real
        h.Feed("You notice a long sword here.");

        Assert.Single(h.Sent);
        Assert.Equal("get long sword", h.SentText[0]);
    }

    [Fact]
    public void MultiLineWrap_StitchesAndSends()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;
        h.Flags["shield"] = true;

        ServerFeed feed = new();
        h.Items.AttachLineExtractor(feed.Lines);

        feed.Line("You notice a long sword, a torch and a");
        feed.Line("shield here.");

        Assert.Equal(new[] { "get long sword", "get shield" }, h.SentText);
    }

    [Fact]
    public void MaxToGet_BelowCap_Collects()
    {
        using Harness h = new();
        int n = h.NumberFor("black star key");
        h.Flags["black star key"] = true;
        h.Caps["black star key"] = 2;
        h.Held[n] = 1;                          // hold one, cap is two

        h.Feed("You notice black star key here.");

        Assert.Single(h.Sent);
        Assert.Equal("get black star key", h.SentText[0]);
    }

    [Fact]
    public void MaxToGet_AtCap_Skips()
    {
        using Harness h = new();
        int n = h.NumberFor("black star key");
        h.Flags["black star key"] = true;
        h.Caps["black star key"] = 2;
        h.Held[n] = 2;                          // already at the cap (e.g. via key ring)

        h.Feed("You notice black star key here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MaxToGet_SameSurveyTwice_CapsWithinPass()
    {
        // One survey lists the item twice; the held snapshot won't move until
        // the gets echo back, so the in-pass tally must enforce the cap.
        using Harness h = new();
        h.NumberFor("black star key");
        h.Flags["black star key"] = true;
        h.Caps["black star key"] = 1;           // want at most one

        h.Feed("You notice black star key and black star key here.");

        Assert.Single(h.Sent);
        Assert.Equal("get black star key", h.SentText[0]);
    }

    [Fact]
    public void MaxToGet_Unbounded_CollectsRegardlessOfHeld()
    {
        using Harness h = new();
        int n = h.NumberFor("long sword");
        h.Flags["long sword"] = true;           // no cap set → int.MaxValue
        h.Held[n] = 99;

        h.Feed("You notice a long sword here.");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void MaxToGet_CapReached_DefersNothing()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        int n = h.NumberFor("black star key");
        h.Flags["black star key"] = true;
        h.Caps["black star key"] = 2;
        h.Held[n] = 2;                          // at cap — nothing to defer

        h.Feed("You notice black star key here.");
        h.HasHostiles = false;
        h.Items.OnRoomObserved();               // combat clears — flush

        Assert.Empty(h.Sent);
    }

    // ----- Stacked ground pile (leading count) ---------------------

    [Fact]
    public void StackedPile_SendsOneGetPerCountedUnit()
    {
        // "You notice 5 piece of amber here." — an unlimited-get stackable; each
        // get grabs one unit, so all five must be requested this pass.
        using Harness h = new();
        h.Flags["piece of amber"] = true;

        h.Feed("You notice 5 piece of amber here.");

        Assert.Equal(
            new[] { "get piece of amber", "get piece of amber", "get piece of amber",
                    "get piece of amber", "get piece of amber" },
            h.SentText);
    }

    [Fact]
    public void Paradigm_StackedPile_SendsOneCountedGet()
    {
        using Harness h = new() { Paradigm = true };
        h.Flags["piece of amber"] = true;

        h.Feed("You notice 5 piece of amber here.");

        Assert.Equal(new[] { "get 5 piece of amber" }, h.SentText);
    }

    [Fact]
    public void Paradigm_CountedConfirmation_ClearsInFlightByCount()
    {
        using Harness h = new() { Paradigm = true };
        h.Flags["orc-head"] = true;

        h.Feed("You notice 3 orc-head here.");   // one "get 3 orc-head"
        Assert.Single(h.Sent);

        h.Feed("You took 3 orc-head.");           // batched confirm — all 3 off the floor
        h.Feed("You notice 3 orc-head here.");   // a fresh pile dropped

        Assert.Equal(2, h.Sent.Count);            // fresh pile collected again
        Assert.Equal("get 3 orc-head", h.SentText[1]);
    }

    [Fact]
    public void StackedPile_SpelledCount_SendsPerUnit()
    {
        using Harness h = new();
        h.Flags["emerald"] = true;

        h.Feed("You notice three emerald here.");

        Assert.Equal(new[] { "get emerald", "get emerald", "get emerald" }, h.SentText);
    }

    [Fact]
    public void StackedPile_MaxToGetCap_StopsPartway()
    {
        // Ground pile of five, cap of two, holding none → grab exactly two.
        using Harness h = new();
        h.NumberFor("piece of amber");
        h.Flags["piece of amber"] = true;
        h.Caps["piece of amber"] = 2;

        h.Feed("You notice 5 piece of amber here.");

        Assert.Equal(new[] { "get piece of amber", "get piece of amber" }, h.SentText);
    }

    [Fact]
    public void StackedPile_Encumbrance_StopsPartway()
    {
        // Five units at weight 30; 40/100 carried leaves room for two before the
        // hard capacity cap refuses the third (40+30+30=100, +30=130 > 100).
        using Harness h = new();
        h.Flags["ingot"] = true;
        h.Weights["ingot"] = 30;
        h.Enc = Reading(40, 100);

        h.Feed("You notice 5 ingot here.");

        Assert.Equal(new[] { "get ingot", "get ingot" }, h.SentText);
    }

    [Fact]
    public void StackedPile_DefersEachUnit_UntilCombatClears()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["piece of amber"] = true;

        h.Feed("You notice 3 piece of amber here.");
        Assert.Empty(h.Sent);                 // deferred — still fighting

        h.HasHostiles = false;
        h.Items.OnRoomObserved();             // combat clears — flush all three

        Assert.Equal(
            new[] { "get piece of amber", "get piece of amber", "get piece of amber" },
            h.SentText);
    }

    [Theory]
    [InlineData("black star key", 1, "black star key")]
    [InlineData("3 black star key", 3, "black star key")]
    [InlineData("12 brass key", 12, "brass key")]
    [InlineData(" 2 runic key ", 2, "runic key")]      // surrounding whitespace trimmed
    [InlineData("keyring", 1, "keyring")]              // single word, no count
    public void ParseKeyEntry_SplitsLeadingCount(string entry, int expectQty, string expectName)
    {
        (int qty, string name) = InventorySnapshot.ParseKeyEntry(entry);
        Assert.Equal(expectQty, qty);
        Assert.Equal(expectName, name);
    }

    // ----- Encumbrance gate ----------------------------------------

    // Category doesn't feed the gate (only current/max weight do), so any
    // bracket label works; percentage is derived for completeness.
    private static EncumbranceReading Reading(int current, int max)
        => new(current, max, max > 0 ? current * 100 / max : 0, EncumbranceLevel.None);

    [Fact]
    public void HardCap_SkipsItemThatWouldExceedCapacity()
    {
        using Harness h = new();
        h.Flags["anvil"] = true;
        h.Weights["anvil"] = 20;
        h.Enc = Reading(90, 100);           // 10 headroom, no gate flags

        h.Feed("You notice an anvil here.");

        Assert.Empty(h.Sent);               // 90+20 > 100 capacity
    }

    [Fact]
    public void HardCap_CollectsItemThatFits()
    {
        using Harness h = new();
        h.Flags["dagger"] = true;
        h.Weights["dagger"] = 5;
        h.Enc = Reading(90, 100);

        h.Feed("You notice a dagger here.");

        Assert.Single(h.Sent);
        Assert.Equal("get dagger", h.SentText[0]);
    }

    // An Auto-sell item over the weight cap asks for exactly the overshoot in coin
    // weight, and is taken once the coin is down.
    [Fact]
    public void OverCap_AutoSellItem_TakenOnceCoinIsDroppedForIt()
    {
        using Harness h = new() { CoinDropFrees = true };
        h.Flags["dagger"] = true;
        h.AutoSell.Add("dagger");
        h.Weights["dagger"] = 40;
        h.Enc = Reading(90, 100);

        h.Feed("You notice a dagger here.");

        Assert.Equal(new[] { (30L, "dagger") }, h.CoinDropAsks);
        Assert.Equal(new[] { "get dagger" }, h.SentText);
    }

    [Fact]
    public void OverCap_AutoSellItem_LeftWhenNoCoinCanBeDropped()
    {
        using Harness h = new();
        h.Flags["dagger"] = true;
        h.AutoSell.Add("dagger");
        h.Weights["dagger"] = 40;
        h.Enc = Reading(90, 100);

        h.Feed("You notice a dagger here.");

        Assert.Single(h.CoinDropAsks);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void OverCap_ItemNotForSale_NeverCostsCoin()
    {
        using Harness h = new() { CoinDropFrees = true };
        h.Flags["anvil"] = true;
        h.Weights["anvil"] = 40;
        h.Enc = Reading(90, 100);

        h.Feed("You notice an anvil here.");

        Assert.Empty(h.CoinDropAsks);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void UnknownEncumbrance_CollectsUngated()
    {
        using Harness h = new();
        h.Flags["anvil"] = true;
        h.Weights["anvil"] = 9999;
        // Enc stays Empty (MaxWeight 0) — capacity unknown, so no gate.

        h.Feed("You notice an anvil here.");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void ZeroWeightItem_NeverGated()
    {
        using Harness h = new();
        h.Flags["feather"] = true;          // weight absent → 0
        h.Enc = Reading(100, 100);          // already at capacity

        h.Feed("You notice a feather here.");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void BracketGate_Light_SkipsPickupThatCrossesBracket()
    {
        using Harness h = new() { GateLight = true };
        h.Flags["shield"] = true;
        h.Weights["shield"] = 10;
        h.Enc = Reading(10, 100);           // Light starts at 17% → cap 16

        h.Feed("You notice a shield here.");

        Assert.Empty(h.Sent);               // 10+10=20 > 16 cap
    }

    [Fact]
    public void BracketGate_Light_AllowsPickupUnderBracket()
    {
        using Harness h = new() { GateLight = true };
        h.Flags["ring"] = true;
        h.Weights["ring"] = 5;
        h.Enc = Reading(10, 100);           // cap 16 → 10+5=15 fits

        h.Feed("You notice a ring here.");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void Projection_ChargesEarlierPickupWithinSameSurvey()
    {
        using Harness h = new();
        h.Flags["dagger"] = true;  h.Weights["dagger"] = 5;
        h.Flags["anvil"]  = true;  h.Weights["anvil"]  = 10;
        h.Enc = Reading(90, 100);           // 10 headroom

        h.Feed("You notice a dagger and an anvil here.");

        // dagger fits (90+5=95); anvil then overflows (95+10=105 > 100).
        Assert.Equal(new[] { "get dagger" }, h.SentText);
    }

    // ----- Floor dedup across re-renders ---------------------------

    [Fact]
    public void ReRenderSameFloor_WithinWindow_NoDuplicateGet()
    {
        // The Cash engine re-displays the room to grab ground coin and the
        // post-kill re-look fires an Enter; both re-render the same "You notice"
        // survey before the get lands. The same floor pile must not re-collect.
        using Harness h = new();
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");
        h.Feed("You notice a long sword here.");   // redisplay of the same floor

        Assert.Single(h.Sent);
        Assert.Equal("get long sword", h.SentText[0]);
    }

    [Fact]
    public void ReRenderLargerPile_CollectsOnlyTheDelta()
    {
        // A fresh drop grows the pile between renders — grab only the new copy.
        using Harness h = new();
        h.Flags["piece of amber"] = true;

        h.Feed("You notice 2 piece of amber here.");   // grab two
        h.Feed("You notice 3 piece of amber here.");   // a third dropped

        Assert.Equal(
            new[] { "get piece of amber", "get piece of amber", "get piece of amber" },
            h.SentText);
    }

    [Fact]
    public void TwoSeparateEntriesSameItem_OneSurvey_CollectsBoth()
    {
        // Two distinct copies listed as separate entries in ONE survey are two
        // floor items — both collect. The dedup is cross-survey, not within one.
        using Harness h = new();
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword and a long sword here.");

        Assert.Equal(new[] { "get long sword", "get long sword" }, h.SentText);
    }

    [Fact]
    public void DeferredFlush_ThenReRender_NoDuplicateGet()
    {
        // Reported bug: items deferred during combat flush once combat clears,
        // then the combat-clear redisplay re-lists the same pile — no re-send.
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["orc-head"] = true;

        h.Feed("You notice 2 orc-head here.");   // deferred — still fighting
        Assert.Empty(h.Sent);

        h.HasHostiles = false;
        h.Items.OnRoomObserved();                // combat clears — flush the two
        Assert.Equal(2, h.Sent.Count);

        h.Feed("You notice 2 orc-head here.");   // redisplay of the same floor
        Assert.Equal(2, h.Sent.Count);           // still two — no re-collect
    }

    [Fact]
    public void RoomChange_ClearsLedger_CollectsAgainInNewRoom()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;

        h.Feed("You notice a long sword here.");
        Assert.Single(h.Sent);

        h.Items.OnRoomChanged();                  // walked to a new room
        h.Feed("You notice a long sword here.");  // a sword on this floor too

        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void Confirmation_ThenReListing_IsTreatedAsNewDrop()
    {
        // "You took X" means that copy left the floor, so a later survey listing
        // X again is a fresh drop (not a re-render) and collects.
        using Harness h = new();
        h.Flags["orc-head"] = true;

        h.Feed("You notice orc-head here.");   // get sent, in-flight
        Assert.Single(h.Sent);

        h.Feed("You took orc-head.");           // server confirms — off the floor
        h.Feed("You notice orc-head here.");   // a fresh orc-head dropped

        Assert.Equal(2, h.Sent.Count);          // collected the new one
    }

    [Fact]
    public void OtherPlayerPickup_DoesNotClearInFlight()
    {
        // Someone else's "Bob picks up orc-head." is NOT our confirmation — the
        // in-flight get stands, so a re-render of the same floor still dedups.
        using Harness h = new();
        h.Flags["orc-head"] = true;

        h.Feed("You notice orc-head here.");   // get sent, in-flight
        Assert.Single(h.Sent);

        h.Feed("Bob picks up orc-head.");       // another player, not us
        h.Feed("You notice orc-head here.");   // re-render of the same floor

        Assert.Single(h.Sent);                  // still deduped
    }

    // ----- Post-kill drop re-look ----------------------------------

    [Fact]
    public void RequestDropReLook_SendsBareEnter()
    {
        using Harness h = new();
        h.Items.RequestDropReLook();
        // A lone carriage return (Enter) re-renders the "You notice … here." survey
        // without look's room description / flavor text.
        Assert.Equal(new[] { "" }, h.SentText);
        Assert.Equal(new byte[] { (byte)'\r' }, h.Sent[0]);
    }

    [Fact]
    public void RequestDropReLook_Cooldown_SuppressesSecond()
    {
        using Harness h = new();
        h.Items.RequestDropReLook();
        h.Items.RequestDropReLook();        // within cooldown → suppressed
        Assert.Single(h.Sent);
    }

    [Fact]
    public void RequestDropReLook_DisabledMaster_NoLook()
    {
        using Harness h = new() { Enabled = false };
        h.Items.RequestDropReLook();
        Assert.Empty(h.Sent);
    }

    // The kill clears the Combat gate in the same pass, so the walker has to be held
    // from the re-look until the room display it asked for has been read.
    [Fact]
    public void RequestDropReLook_HoldsTheWalkerUntilTheRoomDisplays()
    {
        using Harness h = new();
        MudPlay.Game.Map.MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        h.Items.SetAcquisitionGate(gate);

        h.Items.RequestDropReLook();
        Assert.True(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));

        h.Items.NoteRoomDisplayed();
        Assert.False(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));
    }

    [Fact]
    public void RequestDropReLook_DropOnTheFloor_HoldsUntilItIsTaken()
    {
        using Harness h = new();
        h.Flags["black star key"] = true;
        MudPlay.Game.Map.MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        h.Items.SetAcquisitionGate(gate);

        h.Items.RequestDropReLook();
        h.Feed("You notice black star key here.");
        h.Items.NoteRoomDisplayed();

        Assert.Equal(new[] { "", "get black star key" }, h.SentText);
        Assert.True(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));

        h.Feed("You took black star key.");
        Assert.False(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));
    }

    [Fact]
    public void RoomDisplay_WithNoReLookOut_LeavesTheGateAlone()
    {
        using Harness h = new();
        MudPlay.Game.Map.MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        h.Items.SetAcquisitionGate(gate);
        gate.NoteDeferredPending(1);        // someone else's hold

        h.Items.NoteRoomDisplayed();

        Assert.True(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));
    }

    // A named get (what a hang-up penalty dropped) is the character's own item
    // coming back: none of the auto-collect choices stand in its way, and it goes
    // out the way every other get here does.
    [Fact]
    public void CollectNamed_IgnoresTheAutoCollectChoices_OnePerCopyOnStock()
    {
        using Harness h = new() { Enabled = false };   // Auto Get Items off, item not flagged

        h.Items.CollectNamed("chainmail hauberk", 1, "test");
        h.Items.CollectNamed("torch", 2, "test");

        Assert.Equal(new[] { "get chainmail hauberk", "get torch", "get torch" }, h.SentText);
    }

    [Fact]
    public void CollectNamed_OnParadigm_SendsOneCountedGet()
    {
        using Harness h = new() { Paradigm = true };

        h.Items.CollectNamed("torch", 3, "test");

        Assert.Equal(new[] { "get 3 torch" }, h.SentText);
    }

    [Fact]
    public void CollectNamed_HoldsTheWalkerUntilTheItemIsTaken()
    {
        using Harness h = new();
        MudPlay.Game.Map.MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        h.Items.SetAcquisitionGate(gate);

        h.Items.CollectNamed("torch", 1, "test");
        Assert.True(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));

        h.Feed("You took torch.");
        Assert.False(coord.IsGateAsserted(MudPlay.Game.Map.MovementCoordinator.AcquisitionGate));
    }

    // The same floor drawn again before the get is answered must not send a
    // second get for a copy already asked for.
    [Fact]
    public void CollectNamed_IsNotCollectedAgain_ByARedrawOfTheSameFloor()
    {
        using Harness h = new();
        h.Flags["torch"] = true;

        h.Items.CollectNamed("torch", 1, "test");
        h.Feed("You notice torch here.");

        Assert.Equal(new[] { "get torch" }, h.SentText);
    }

    // The engine's own get for a flagged item can already be out when the named
    // one is asked for: a room redisplay the caller sent lists the item first.
    // That get counts towards the copies wanted.
    [Fact]
    public void CollectNamed_CountsAGetAlreadyOutForTheItem()
    {
        using Harness h = new();
        h.Flags["torch"] = true;
        h.Feed("You notice torch here.");

        h.Items.CollectNamed("torch", 1, "test");
        Assert.Equal(new[] { "get torch" }, h.SentText);

        h.Items.CollectNamed("torch", 3, "test");
        Assert.Equal(new[] { "get torch", "get torch", "get torch" }, h.SentText);
    }

    // Collect-after-combat flushes on the observation that ends the fight, just
    // ahead of a caller that waited for the same one.
    [Fact]
    public void CollectNamed_AfterTheCombatFlush_SendsNoSecondGet()
    {
        using Harness h = new() { CollectAfterCombat = true, HasHostiles = true };
        h.Flags["torch"] = true;
        h.Feed("You notice torch here.");
        Assert.Empty(h.Sent);

        h.HasHostiles = false;
        h.Items.OnRoomObserved();
        h.Items.CollectNamed("torch", 1, "test");

        Assert.Equal(new[] { "get torch" }, h.SentText);
    }

    [Fact]
    public void Disposed_StopsSending()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;
        h.Items.Dispose();

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

}
