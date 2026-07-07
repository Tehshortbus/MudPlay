using System.Text;
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

        public bool Enabled { get; set; } = true;
        public bool CollectAfterCombat { get; set; }
        public bool HasHostiles { get; set; }
        public bool PeekSuppressed { get; set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Items = new AutoGetItemsManager(Router,
                resolve: Resolve,
                isEnabled: () => Enabled,
                collectAfterCombatFinished: () => CollectAfterCombat,
                hasEngageableHostiles: () => HasHostiles,
                isPeekSuppressed: () => PeekSuppressed,
                log: Log);
            Items.SetWireSender(b => Sent.Add(b));
        }

        private AutoGetItemsManager.ResolvedItem? Resolve(string entry)
        {
            string key = Strip(entry);
            if (!Flags.TryGetValue(key, out bool auto)) return null;
            return new AutoGetItemsManager.ResolvedItem(key, auto);
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
