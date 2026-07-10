using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Remote;

// Write-side handler for the inventory / cash action commands. Unlike the
// read-only InventoryQueryHandler, these emit wire commands, so a wire-sender is
// bound (SetWireSender):
//   - @get-all — get <item> for every item on the room floor the
//     GroundItemTracker last surveyed (cash is left for the cash policy engine).
//   - @drop-all — drop <item> for every carried-but-unworn item (equipped gear
//     is left worn).
//   - @deposit-all — bank the wealth above the per-denomination keep-on-hand
//     floors, or withdraw up to them when the character is below. Amount is the
//     copper-farthing total the game consolidates to the highest denomination on
//     dep / with.
//   - @share — split held coin evenly, per denomination, across the whole party
//     (self keeps a share plus the remainder); a party-whitelist command, so any
//     active party member can call it.
//
// @drop-all / @deposit-all / @share read the immutable InventoryManager.Snapshot
// and gate on IsLoaded — a full i dump has to have landed before we know what to
// drop / bank / share. @get-all reads the room-scoped GroundItemTracker instead
// (the last "You notice" survey). The engine gates authorisation via
// RemoteCommandCatalog before the handler runs. Wire replies ride the
// Latin1/CP437 BBS wire, so every reply is ASCII-only (no em-dash / approx
// glyphs).
public sealed class InventoryActionHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
        { "@drop-all", "@deposit-all", "@share", "@get-all" };

    private readonly RemoteCommandManager _engine;
    private readonly InventoryManager _inventory;
    private readonly GroundItemTracker _ground;
    private readonly PartyState _party;
    private readonly Func<CashSettings> _readCash;
    private readonly CurrencyNaming _naming;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public InventoryActionHandler(
        RemoteCommandManager engine,
        InventoryManager inventory,
        GroundItemTracker ground,
        PartyState party,
        Func<CashSettings> readCash,
        CurrencyNaming naming)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(naming);
        _engine = engine;
        _inventory = inventory;
        _ground = ground;
        _party = party;
        _readCash = readCash;
        _naming = naming;

        Register("@drop-all", OnDropAll);
        Register("@deposit-all", OnDepositAll);
        Register("@share", OnShare);
        Register("@get-all", OnGetAll);
    }

    // Bind the wire-sender — the gate-wrapped SendUserInput pipeline from
    // MainWindowViewModel, same shape the cash / divert handlers use. Without it
    // the commands still authorise and reply, but no drop / dep / give reaches the
    // game.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
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

    // @get-all — get <item> for every item on the room floor from the latest
    // "You notice" survey. Cash is excluded by the GroundItemTracker (the
    // cash-policy engine owns coin), and the leading article is stripped so the
    // wire verb matches the item's noun phrase. There is no bulk "get all" verb in
    // MajorMUD, so this paces one get per item. Encumbrance is left to the game to
    // enforce — the server refuses a pickup that would overload us, same as the
    // auto-get engine.
    private void OnGetAll(RemoteCommandContext ctx) => ctx.Reply(GetAll());

    // Run the get-all sweep and return the status line. Shared by the @get-all
    // remote handler (which replies it on the party channel) and the local
    // Action-menu / toolbar "Get All" (which logs it). Emits the paced get
    // commands as a side effect.
    public string GetAll()
    {
        IReadOnlyList<string> ground = _ground.Items;
        if (ground.Count == 0) return "nothing on the ground to get";

        int sent = 0;
        foreach (string item in ground)
        {
            string name = StripArticle(item);
            if (name.Length == 0) continue;
            Send($"get {name}");
            sent++;
        }
        return $"getting {sent} ground item{(sent == 1 ? "" : "s")}";
    }

    // @drop-all — drop <item> for every carried-but-unworn item.
    // InventorySnapshot.CarriedItems already excludes worn gear (slot-suffixed
    // lines land in EquippedItems) and currency tokens, so worn equipment and coin
    // are never dropped. The leading article is stripped so the wire verb matches
    // on the item's noun phrase ("a rusty dagger" → drop rusty dagger).
    private void OnDropAll(RemoteCommandContext ctx) => ctx.Reply(DropAll());

    // Run the drop-all sweep and return the status line. Shared by the @drop-all
    // remote handler and the local "Drop All" action. Emits a drop per
    // carried-but-unworn item as a side effect.
    public string DropAll()
    {
        if (!_inventory.IsLoaded) return "inventory not parsed yet (type i)";
        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;
        if (carried.Count == 0) return "nothing to drop";

        foreach (string item in carried)
        {
            string name = StripArticle(item);
            if (name.Length == 0) continue;
            Send($"drop {name}");
        }
        return $"dropping {carried.Count} carried item{(carried.Count == 1 ? "" : "s")}";
    }

    // @deposit-all — level the character's wealth to the per-denomination
    // keep-on-hand floors. Over the floor → dep <excess>; under it →
    // with <shortfall>; exactly on it → no-op reply. The amount is the
    // consolidated copper-farthing value (same figure @wealth reports); the game
    // re-consolidates held coin to the highest denomination after the transaction,
    // so we never have to name individual coins.
    private void OnDepositAll(RemoteCommandContext ctx) => ctx.Reply(DepositAll());

    // Level held coin to the keep-on-hand floor and return the status line. Shared
    // by the @deposit-all remote handler and the local "Deposit All" action. Emits
    // a dep / with as a side effect.
    public string DepositAll()
    {
        if (!_inventory.IsLoaded) return "wealth unknown - parse inventory first (type i)";
        long keep = _readCash().KeepOnHandCopper();
        long held = _inventory.Snapshot.Currency.TotalCopperValue;
        long delta = held - keep;
        if (delta > 0)
        {
            Send($"dep {delta}");
            return $"depositing {delta:N0} copper (keeping {keep:N0})";
        }
        if (delta < 0)
        {
            long shortfall = -delta;
            Send($"with {shortfall}");
            return $"withdrawing {shortfall:N0} copper (up to {keep:N0} on hand)";
        }
        return $"already at keep-on-hand ({keep:N0} copper)";
    }

    // @share — split held coin evenly across the whole party. For each
    // denomination, the per-head share is count / partySize (integer division,
    // party size counting self); every non-self member is
    // give <share> <denom> to <member>'d that amount, so self keeps one share plus
    // any indivisible remainder. Party-whitelist gated (catalog category None), so
    // the engine only reaches here for an active party member.
    private void OnShare(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Reply("wealth unknown - parse inventory first (type i)"); return; }

        List<string> recipients = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            recipients.Add(GivenName(m.Name));
        }
        if (recipients.Count == 0) { ctx.Reply("no party members to share with"); return; }

        // +1 for self: self is one of the sharers and keeps their own cut, so
        // the divisor is the full party size regardless of whether the par
        // table currently lists a self row.
        int partySize = recipients.Count + 1;
        CurrencyHoldings c = _inventory.Snapshot.Currency;
        // Runic carries the board's wire word so `give N <word>` is a command the
        // server accepts; the other four denominations are stable.
        (string Denom, int Count)[] denominations =
        {
            ("copper", c.Copper),
            ("silver", c.Silver),
            ("gold", c.Gold),
            ("platinum", c.Platinum),
            (_naming.RunicName, c.Runic),
        };

        bool anyShared = false;
        foreach ((string denom, int count) in denominations)
        {
            int per = count / partySize;
            if (per <= 0) continue; // fewer coins than sharers — nothing to split.
            foreach (string recipient in recipients)
                Send($"give {per} {denom} to {recipient}");
            anyShared = true;
        }

        ctx.Reply(anyShared
            ? $"sharing coins among {partySize} party members"
            : "nothing to share (too few coins to split)");
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Drop a leading indefinite / definite article so the wire item name is the
    // bare noun phrase MajorMUD matches ("a rusty dagger" → "rusty dagger").
    // Leaves the name untouched when it carries no article.
    private static string StripArticle(string name)
    {
        if (name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)) return name[2..];
        if (name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return name[3..];
        if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return name[4..];
        return name;
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
