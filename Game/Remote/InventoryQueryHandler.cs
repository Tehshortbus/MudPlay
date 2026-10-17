using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Read-only handler for the QueryInventory commands that report held wealth /
// carry weight / possession / room loot without touching the wire:
//   - @wealth — coins on hand, broken down by denomination.
//   - @enc — current / max carry weight, percentage, bracket.
//   - @have <item> — yes + count, or no, for a name substring across carried,
//     worn, and key-ring items.
//   - @inv — the carried pack items + key ring: everything on us that another
//     player CAN'T see by looking. Worn/wielded gear (EquippedItems) and a readied
//     light are deliberately excluded — an onlooker sees those, so they add no
//     hidden-inventory signal a partymate couldn't get with a look.
//   - @what — the items visible on the room floor, off the GroundItemTracker's
//     last "You notice" survey.
// The wealth / carry / have / inv set read the immutable InventoryManager.Snapshot,
// each replying a friendly "parse inventory first" line until the first full i
// dump lands (IsLoaded); @what reads the room-scoped ground snapshot instead. The
// engine gates authorisation via RemoteCommandCatalog before the handler runs.
public sealed class InventoryQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@wealth", "@enc", "@have", "@inv", "@what" };

    // Per-reply char budget for the @inv item / key lists. The engine wraps the
    // payload in { } and prefixes the recipient, and the game caps a single say's
    // input length — so a big pack is split across MULTIPLE replies (each chunk
    // within budget) rather than truncated. Kept under HelpHandler's 200 to leave
    // headroom for the "(i/N)" label and the wrapper so no reply overruns the say line.
    private const int PackBudget = 170;

    private readonly RemoteCommandManager _engine;
    private readonly InventoryManager _inventory;
    private readonly GroundItemTracker _ground;
    private readonly CurrencyNaming _naming;
    private bool _disposed;

    public InventoryQueryHandler(
        RemoteCommandManager engine, InventoryManager inventory, GroundItemTracker ground,
        CurrencyNaming? naming = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ground);
        _engine = engine;
        _inventory = inventory;
        _ground = ground;
        // Unbound falls back to stock "runic".
        _naming = naming ?? new CurrencyNaming();

        Register("@wealth", OnWealth);
        Register("@enc", OnEnc);
        Register("@have", OnHave);
        Register("@inv", OnInv);
        Register("@what", OnWhat);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // @wealth — non-zero denominations high→low, plus the consolidated copper
    // value the game's Wealth: line reports.
    private void OnWealth(RemoteCommandContext ctx)
    {
        // Protocol, not a refusal: a leader's PartyWealthProbe reads "wealth
        // unknown" as this member having answered, so it goes whatever the Warn
        // setting says.
        if (!_inventory.IsLoaded) { ctx.Reply("wealth unknown - parse inventory first (type i)"); return; }
        CurrencyHoldings c = _inventory.Snapshot.Currency;
        string coins = FormatCoins(c);
        // TotalCoinCount == 0 means no physical coins; "coins" already
        // reads "no coins on hand", so don't tack on a "(= 0 copper)".
        // ASCII-only: the reply rides a Latin1/CP437 BBS wire, so no em-dash /
        // approx glyphs (they degrade to '?' or worse in-game).
        ctx.Reply(c.TotalCoinCount == 0 ? coins : $"{coins} (= {c.TotalCopperValue:N0} copper)");
    }

    // @enc — "Encumbrance cur/max (pct%) - Bracket".
    private void OnEnc(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Refuse("encumbrance unknown - parse inventory first (type i)"); return; }
        EncumbranceReading e = _inventory.Snapshot.Encumbrance;
        ctx.Reply($"Encumbrance {e.CurrentWeight}/{e.MaxWeight} ({e.Percentage}%) - {e.Category}");
    }

    // @have <item> — case-insensitive substring match across carried, worn, AND
    // key-ring items (wearing a piece still counts as having it; keys live on the
    // ring's own list, not in the pack), replying yes + the match count or no.
    // Substring rather than exact so a sender can ask "@have dagger" against "a
    // rusty dagger".
    private void OnHave(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0) { ctx.Refuse("usage: @have <item name>"); return; }
        if (!_inventory.IsLoaded) { ctx.Refuse("inventory not parsed yet (type i)"); return; }

        InventorySnapshot snap = _inventory.Snapshot;
        int count = 0;
        // Sum the QUANTITY, not the number of matching entries. A stack prints as a
        // single "N item" line ("25 black diamonds" is one CarriedItems entry), so
        // counting entries reported 1x when the sender actually held 25 (bug: @have
        // black diamond → "1x" with 25 carried). SplitLeadingCount pulls the stack
        // count off the token (a lone item with no leading number counts as 1).
        foreach (string item in snap.CarriedItems)
            if (item.Contains(query, StringComparison.OrdinalIgnoreCase))
                count += CountedCommand.SplitLeadingCount(item).Count;
        foreach (EquippedItem item in snap.EquippedItems)
            if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                count++;   // a worn piece is a single item
        // Keys ride the ring's separate "You have the following keys:" list, not the
        // pack — @have missed them entirely, so "@have black star key" reported "no"
        // with the key on the ring. ParseKeyEntry splits a stacked "3 iron key" into
        // its count so a held stack tallies correctly.
        foreach (string key in snap.Keys ?? Array.Empty<string>())
        {
            (int qty, string name) = InventorySnapshot.ParseKeyEntry(key);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                count += qty;
        }

        // "yes - Nx '<item>'" — echoes the queried name (kept for the @party
        // inventory probe's per-item reply correlation) with the true carried count.
        // "no" is an answer the probe reads, not a refusal.
        ctx.Reply(count > 0 ? $"yes - {count}x '{query}'" : $"no - nothing matching '{query}'");
    }

    // @inv — the carried pack + keys, the inventory an onlooker can't see. Reads
    // CarriedItems (no slot suffix; coins already excluded) and the separate key
    // ring; worn/wielded gear and the readied light are visible on a look, so both
    // are left out. The WHOLE list is reported — a pack too long for one say is
    // split across multiple replies rather than truncated with a "+N more" tail.
    private void OnInv(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Refuse("inventory not parsed yet (type i)"); return; }

        InventorySnapshot snap = _inventory.Snapshot;
        IReadOnlyList<string> carried = snap.CarriedItems;
        IReadOnlyList<string> keys = snap.Keys ?? Array.Empty<string>();
        if (carried.Count == 0 && keys.Count == 0) { ctx.Reply("carrying nothing"); return; }

        // Compact single reply when the whole thing fits one say — preserves the
        // familiar "carrying: …; keys: …" line for a normal pack.
        string? combined = CombineWithinBudget(carried, keys, PackBudget);
        if (combined is not null) { ctx.Reply(combined); return; }

        // Too long for one say → report the FULL list, carried then keys, across as
        // many chunked replies as it takes (no "+N more" truncation).
        if (carried.Count > 0) ReplyChunked(ctx, "carrying", carried);
        if (keys.Count > 0) ReplyChunked(ctx, "keys", keys);
    }

    // The compact "carrying: …; keys: …" line when it fits within budget, else null.
    private static string? CombineWithinBudget(
        IReadOnlyList<string> carried, IReadOnlyList<string> keys, int budget)
    {
        List<string> parts = new(2);
        if (carried.Count > 0) parts.Add($"carrying: {string.Join(", ", carried)}");
        if (keys.Count > 0) parts.Add($"keys: {string.Join(", ", keys)}");
        string combined = string.Join("; ", parts);
        return combined.Length <= budget ? combined : null;
    }

    // Send items as one or more replies covering the FULL list — each reply a
    // comma-joined chunk within PackBudget so a big pack doesn't overrun the game's
    // say-input limit. A single chunk reads "<label>: a, b, c"; a split list labels
    // each part "<label> (i/N): ..." so the reader knows it's continued.
    //
    // Shared with the other handlers that answer with a list of any length
    // (EventRemoteHandler), so a long list reads the same whoever sends it.
    internal static void ReplyChunked(RemoteCommandContext ctx, string label, IReadOnlyList<string> items)
    {
        List<string> chunks = ChunkJoined(items, PackBudget);
        if (chunks.Count == 1) { ctx.Reply($"{label}: {chunks[0]}"); return; }
        for (int i = 0; i < chunks.Count; i++)
            ctx.Reply($"{label} ({i + 1}/{chunks.Count}): {chunks[i]}");
    }

    // Comma-join item names into as few chunks as possible, each <= budget chars.
    // The first item of a chunk always goes in even if it alone exceeds budget, so
    // no item is ever dropped and no chunk is empty.
    private static List<string> ChunkJoined(IReadOnlyList<string> items, int budget)
    {
        List<string> chunks = new();
        StringBuilder sb = new();
        foreach (string item in items)
        {
            if (sb.Length > 0 && sb.Length + 2 + item.Length > budget)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(item);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    // @what — the items on the room floor from the latest "You notice" survey
    // (cash excluded — that's @wealth's domain). Reports the verbatim item wording
    // so the caller sees exactly what @get-all would pick up. The snapshot is
    // room-scoped and clears on movement, so an empty list means the current room
    // has no visible loot.
    private void OnWhat(RemoteCommandContext ctx)
    {
        IReadOnlyList<string> items = _ground.Items;
        if (items.Count == 0) { ctx.Reply("nothing on the ground here"); return; }
        ctx.Reply($"on the ground: {string.Join(", ", items)}");
    }

    private string FormatCoins(CurrencyHoldings c)
    {
        List<string> parts = new(5);
        if (c.Runic > 0) parts.Add($"{c.Runic:N0} {_naming.RunicName}");
        if (c.Platinum > 0) parts.Add($"{c.Platinum:N0} platinum");
        if (c.Gold > 0) parts.Add($"{c.Gold:N0} gold");
        if (c.Silver > 0) parts.Add($"{c.Silver:N0} silver");
        if (c.Copper > 0) parts.Add($"{c.Copper:N0} copper");
        return parts.Count == 0 ? "no coins on hand" : string.Join(", ", parts);
    }
}
