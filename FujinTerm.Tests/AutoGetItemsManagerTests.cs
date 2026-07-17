using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

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

        // Encumbrance reading + item bracket gates the manager reads each survey.
        // Default Empty (MaxWeight 0) disables the gate — the pre-existing tests
        // set no weights and expect ungated collection.
        public EncumbranceReading Enc { get; set; } = EncumbranceReading.Empty;
        public bool GateLight { get; set; }
        public bool GateMedium { get; set; }
        public bool GateHeavy { get; set; }

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
                itemEncGates: () => (GateLight, GateMedium, GateHeavy),
                log: Log);
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
            return new AutoGetItemsManager.ResolvedItem(number, key, auto, noTake, cap, weight);
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

        LineExtractor lines = new(new TerminalEmulator(80, 24));
        h.Items.AttachLineExtractor(lines);

        FeedLine(lines, "You notice a long sword, a torch and a");
        FeedLine(lines, "shield here.");

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

    // ----- Post-kill drop re-look ----------------------------------

    [Fact]
    public void RequestDropReLook_SendsLook()
    {
        using Harness h = new();
        h.Items.RequestDropReLook();
        Assert.Equal(new[] { "look" }, h.SentText);
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

    [Fact]
    public void Disposed_StopsSending()
    {
        using Harness h = new();
        h.Flags["long sword"] = true;
        h.Items.Dispose();

        h.Feed("You notice a long sword here.");

        Assert.Empty(h.Sent);
    }

    private static void FeedLine(LineExtractor lines, string text)
    {
        System.Reflection.FieldInfo? field = typeof(LineExtractor)
            .GetField("LineEmitted",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }
}
