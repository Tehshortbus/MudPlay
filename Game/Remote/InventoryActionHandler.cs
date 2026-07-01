using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Write-side <see cref="RemoteCommandManager"/> consumer for the three
/// inventory / cash action commands. Unlike the read-only
/// <see cref="InventoryQueryHandler"/>, these emit wire commands, so a
/// wire-sender is bound (<see cref="SetWireSender"/>):
/// <list type="bullet">
///   <item><c>@get-all</c> — <c>get &lt;item&gt;</c> for every item on the
///         room floor the <see cref="GroundItemTracker"/> last surveyed
///         (cash is left for the cash policy engine).</item>
///   <item><c>@drop-all</c> — <c>drop &lt;item&gt;</c> for every
///         carried-but-unworn item (equipped gear is left worn).</item>
///   <item><c>@deposit-all</c> — bank the wealth above the per-denomination
///         keep-on-hand floors, or withdraw up to them when the character is
///         below. Amount is the copper-farthing total the game consolidates
///         to the highest denomination on <c>dep</c> / <c>with</c>.</item>
///   <item><c>@share</c> — split held coin evenly, per denomination, across
///         the whole party (self keeps a share plus the remainder); a
///         party-whitelist command, so any active party member can call it.</item>
/// </list>
/// <c>@drop-all</c> / <c>@deposit-all</c> / <c>@share</c> read the immutable
/// <see cref="InventoryManager.Snapshot"/> and gate on
/// <see cref="InventoryManager.IsLoaded"/> — a full <c>i</c> dump has to have
/// landed before we know what to drop / bank / share. <c>@get-all</c> reads
/// the room-scoped <see cref="GroundItemTracker"/> instead (the last "You
/// notice" survey). The engine gates authorisation via
/// <see cref="RemoteCommandCatalog"/> before the handler runs. Wire replies
/// ride the Latin1/CP437 BBS wire, so every reply is ASCII-only (no em-dash /
/// approx glyphs).
/// </summary>
public sealed class InventoryActionHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
        { "@drop-all", "@deposit-all", "@share", "@get-all" };

    private readonly RemoteCommandManager _engine;
    private readonly InventoryManager _inventory;
    private readonly GroundItemTracker _ground;
    private readonly PartyState _party;
    private readonly Func<CashSettings> _readCash;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public InventoryActionHandler(
        RemoteCommandManager engine,
        InventoryManager inventory,
        GroundItemTracker ground,
        PartyState party,
        Func<CashSettings> readCash)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readCash);
        _engine = engine;
        _inventory = inventory;
        _ground = ground;
        _party = party;
        _readCash = readCash;

        Register("@drop-all", OnDropAll);
        Register("@deposit-all", OnDepositAll);
        Register("@share", OnShare);
        Register("@get-all", OnGetAll);
    }

    /// <summary>
    /// Bind the wire-sender — the gate-wrapped <c>SendUserInput</c> pipeline
    /// from <c>MainWindowViewModel</c>, same shape the cash / divert handlers
    /// use. Without it the commands still authorise and reply, but no
    /// <c>drop</c> / <c>dep</c> / <c>give</c> reaches the game.
    /// </summary>
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

    /// <summary>
    /// <c>@get-all</c> — <c>get &lt;item&gt;</c> for every item on the room
    /// floor from the latest "You notice" survey. Cash is excluded by the
    /// <see cref="GroundItemTracker"/> (the cash-policy engine owns coin), and
    /// the leading article is stripped so the wire verb matches the item's
    /// noun phrase. There is no bulk "get all" verb in MajorMUD, so this
    /// paces one <c>get</c> per item. Encumbrance is left to the game to
    /// enforce — the server refuses a pickup that would overload us, same as
    /// the auto-get engine.
    /// </summary>
    private void OnGetAll(RemoteCommandContext ctx)
    {
        IReadOnlyList<string> ground = _ground.Items;
        if (ground.Count == 0) { ctx.Reply("nothing on the ground to get"); return; }

        int sent = 0;
        foreach (string item in ground)
        {
            string name = StripArticle(item);
            if (name.Length == 0) continue;
            Send($"get {name}");
            sent++;
        }
        ctx.Reply($"getting {sent} ground item{(sent == 1 ? "" : "s")}");
    }

    /// <summary>
    /// <c>@drop-all</c> — <c>drop &lt;item&gt;</c> for every carried-but-unworn
    /// item. <see cref="InventorySnapshot.CarriedItems"/> already excludes worn
    /// gear (slot-suffixed lines land in <see cref="InventorySnapshot.EquippedItems"/>)
    /// and currency tokens, so worn equipment and coin are never dropped. The
    /// leading article is stripped so the wire verb matches on the item's noun
    /// phrase ("a rusty dagger" → <c>drop rusty dagger</c>).
    /// </summary>
    private void OnDropAll(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Reply("inventory not parsed yet (type i)"); return; }
        IReadOnlyList<string> carried = _inventory.Snapshot.CarriedItems;
        if (carried.Count == 0) { ctx.Reply("nothing to drop"); return; }

        foreach (string item in carried)
        {
            string name = StripArticle(item);
            if (name.Length == 0) continue;
            Send($"drop {name}");
        }
        ctx.Reply($"dropping {carried.Count} carried item{(carried.Count == 1 ? "" : "s")}");
    }

    /// <summary>
    /// <c>@deposit-all</c> — level the character's wealth to the per-denomination
    /// keep-on-hand floors. Over the floor → <c>dep &lt;excess&gt;</c>; under it
    /// → <c>with &lt;shortfall&gt;</c>; exactly on it → no-op reply. The amount
    /// is the consolidated copper-farthing value (same figure <c>@wealth</c>
    /// reports); the game re-consolidates held coin to the highest denomination
    /// after the transaction, so we never have to name individual coins.
    /// </summary>
    private void OnDepositAll(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Reply("wealth unknown - parse inventory first (type i)"); return; }
        long keep = _readCash().KeepOnHandCopper();
        long held = _inventory.Snapshot.Currency.TotalCopperValue;
        long delta = held - keep;
        if (delta > 0)
        {
            Send($"dep {delta}");
            ctx.Reply($"depositing {delta:N0} copper (keeping {keep:N0})");
        }
        else if (delta < 0)
        {
            long shortfall = -delta;
            Send($"with {shortfall}");
            ctx.Reply($"withdrawing {shortfall:N0} copper (up to {keep:N0} on hand)");
        }
        else
        {
            ctx.Reply($"already at keep-on-hand ({keep:N0} copper)");
        }
    }

    /// <summary>
    /// <c>@share</c> — split held coin evenly across the whole party. For each
    /// denomination, the per-head share is <c>count / partySize</c> (integer
    /// division, party size counting self); every non-self member is
    /// <c>give &lt;share&gt; &lt;denom&gt; to &lt;member&gt;</c>'d that amount,
    /// so self keeps one share plus any indivisible remainder. Party-whitelist
    /// gated (catalog category <see cref="PlayerRemoteControls.None"/>), so the
    /// engine only reaches here for an active party member.
    /// </summary>
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
        (string Denom, int Count)[] denominations =
        {
            ("copper", c.Copper),
            ("silver", c.Silver),
            ("gold", c.Gold),
            ("platinum", c.Platinum),
            ("runic", c.Runic),
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

    /// <summary>Drop a leading indefinite / definite article so the wire item
    /// name is the bare noun phrase MajorMUD matches ("a rusty dagger" →
    /// "rusty dagger"). Leaves the name untouched when it carries no article.</summary>
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
