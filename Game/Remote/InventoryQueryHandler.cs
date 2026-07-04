using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

// Read-only handler for the QueryInventory commands that report held wealth /
// carry weight / possession / room loot without touching the wire:
//   - @wealth — coins on hand, broken down by denomination.
//   - @enc — current / max carry weight, percentage, bracket.
//   - @have <item> — yes + count, or no, for a name substring across carried and
//     worn items.
//   - @what — the items visible on the room floor, off the GroundItemTracker's
//     last "You notice" survey.
// The wealth / carry / have trio read the immutable InventoryManager.Snapshot,
// each replying a friendly "parse inventory first" line until the first full i
// dump lands (IsLoaded); @what reads the room-scoped ground snapshot instead. The
// engine gates authorisation via RemoteCommandCatalog before the handler runs.
public sealed class InventoryQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@wealth", "@enc", "@have", "@what" };

    private readonly RemoteCommandManager _engine;
    private readonly InventoryManager _inventory;
    private readonly GroundItemTracker _ground;
    private bool _disposed;

    public InventoryQueryHandler(
        RemoteCommandManager engine, InventoryManager inventory, GroundItemTracker ground)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ground);
        _engine = engine;
        _inventory = inventory;
        _ground = ground;

        Register("@wealth", OnWealth);
        Register("@enc", OnEnc);
        Register("@have", OnHave);
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
        if (!_inventory.IsLoaded) { ctx.Reply("encumbrance unknown - parse inventory first (type i)"); return; }
        EncumbranceReading e = _inventory.Snapshot.Encumbrance;
        ctx.Reply($"Encumbrance {e.CurrentWeight}/{e.MaxWeight} ({e.Percentage}%) - {e.Category}");
    }

    // @have <item> — case-insensitive substring match across carried AND worn
    // items (wearing a piece still counts as having it), replying yes + the match
    // count or no. Substring rather than exact so a sender can ask "@have dagger"
    // against "a rusty dagger".
    private void OnHave(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0) { ctx.Reply("usage: @have <item name>"); return; }
        if (!_inventory.IsLoaded) { ctx.Reply("inventory not parsed yet (type i)"); return; }

        InventorySnapshot snap = _inventory.Snapshot;
        int count = 0;
        foreach (string item in snap.CarriedItems)
            if (item.Contains(query, StringComparison.OrdinalIgnoreCase)) count++;
        foreach (EquippedItem item in snap.EquippedItems)
            if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) count++;

        ctx.Reply(count > 0 ? $"yes - {count}x matching '{query}'" : $"no - nothing matching '{query}'");
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

    private static string FormatCoins(CurrencyHoldings c)
    {
        List<string> parts = new(5);
        if (c.Runic > 0) parts.Add($"{c.Runic:N0} runic");
        if (c.Platinum > 0) parts.Add($"{c.Platinum:N0} platinum");
        if (c.Gold > 0) parts.Add($"{c.Gold:N0} gold");
        if (c.Silver > 0) parts.Add($"{c.Silver:N0} silver");
        if (c.Copper > 0) parts.Add($"{c.Copper:N0} copper");
        return parts.Count == 0 ? "no coins on hand" : string.Join(", ", parts);
    }
}
