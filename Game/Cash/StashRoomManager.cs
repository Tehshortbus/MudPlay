using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

// Stash dispatch for user-marked stash rooms. Dispatches one `hide N <coin>`
// command per currency whose held amount exceeds its CashSettings KeepXxxOnHand
// floor, then one `hide <item>` per carried item flagged ItemOverlay.AutoStash.
//
// Two triggers. A stash fires either as a step of an auto-deposit reroute (when
// the wealth / coin gate trips while a Loop or Auto-Lair is running and the
// configured destination is a stash room, AutoDepositManager walks the character
// there and calls ExecuteStash on arrival) OR when the character naturally passes
// through a stash room that sits on the active loop / lair route — a dedicated
// detour is only spent on a stash room that is off-route. A purely manual walk
// through a stash room while no engine is running never triggers a hide.
//
// Room set lives on CharacterProfile.StashRooms — the same list MovementFilter
// uses, populated by the right-click "Toggle: Stash room" on the Navigation map.
// Per-currency keep-on-hand lives on CashSettings so the rules apply uniformly
// across every stash room (no per-room rules).
//
// Stash rooms hold cash and items (banks are cash-only): every carried, unworn
// item whose game-data AutoStash flag is set is hidden by its canonical name. The
// per-item opt-in comes from the injected resolver, which reads the 4-tier
// ItemOverlay override.
//
// Master gate: AutoActionDefaults.AutoGetCash — same toggle as CashManager. Item
// stashing rides the same gate: a stash is one operation ("dump my excess cash
// and my auto-stash items"), not two independently toggled behaviours.
public sealed class StashRoomManager : IDisposable
{
    // LogService category — appears as [StashRoom] rows per entry + dispatch.
    public const string LogCategory = "StashRoom";

    // What a single stash dispatch put away: the room it happened in, the
    // currency amounts hidden, and the canonical names of the items hidden. Either
    // list may be empty (cash-only or items-only stash), but the event only fires
    // when at least one is non-empty.
    public sealed record StashDispatch(
        RoomKey Room,
        IReadOnlyList<(string Currency, long Amount)> Currencies,
        IReadOnlyList<string> Items);

    private readonly ProfileService _profile;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<string, string?> _resolveAutoStashItem;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Fires after a successful stash dispatch. Carries the room key, the
    // (currency, amount) pairs, and the item names that were sent.
    public event Action<StashDispatch>? StashExecuted;

    public StashRoomManager(
        ProfileService profile,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        Func<string, string?> resolveAutoStashItem,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(resolveAutoStashItem);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _profile = profile;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
        _resolveAutoStashItem = resolveAutoStashItem;
        _isEnabled = isEnabled;
        _log = log;
    }

    // Bind the wire sender — typically the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Called by AutoDepositManager on arrival at a stash destination during an
    // auto-deposit reroute. Dispatches one `hide N <coin>` per currency whose held
    // amount exceeds its keep-on-hand floor. Guarded by the cash master toggle and
    // a defensive stash-room membership check (the caller only routes here for
    // stash destinations, but the guard keeps the contract local).
    public void ExecuteStash(RoomKey enteredRoom)
    {
        if (!_isEnabled()) return;
        if (_profile.Current is not { } profile) return;
        if (profile.StashRooms is not { Count: > 0 } stashes) return;

        bool isStash = false;
        foreach (RoomRef r in stashes)
        {
            if (r.Map == enteredRoom.Map && r.Room == enteredRoom.Room)
            {
                isStash = true;
                break;
            }
        }
        if (!isStash) return;

        CashSettings cash = _readCash();
        // Authoritative per-denomination holdings + carried items (the
        // `i`-seeded, delta-tracked snapshot) — NOT CashManager's
        // since-engine-start pickup tally, which never sees the starting
        // balance and would undercount the hide amounts.
        InventorySnapshot snapshot = _getSnapshot();
        CurrencyHoldings held = snapshot.Currency;
        _log?.Debug(LogCategory,
            $"entered stash room map={enteredRoom.Map} room={enteredRoom.Room}");

        List<(string Currency, long Amount)> dispatched = new();
        DispatchOne("copper",   held.Copper,   cash.KeepCopperOnHand,   dispatched);
        DispatchOne("silver",   held.Silver,   cash.KeepSilverOnHand,   dispatched);
        DispatchOne("gold",     held.Gold,     cash.KeepGoldOnHand,     dispatched);
        DispatchOne("platinum", held.Platinum, cash.KeepPlatinumOnHand, dispatched);
        DispatchOne("runic",    held.Runic,    cash.KeepRunicOnHand,    dispatched);

        // Stash rooms hold items too (banks are cash-only). Hide every
        // carried, unworn item flagged AutoStash by its canonical name.
        // One hide per listed carry entry: MajorMUD lists each carried
        // item slot as its own token, so repeated names hide repeated
        // copies naturally.
        List<string> hiddenItems = new();
        foreach (string entry in snapshot.CarriedItems)
        {
            if (_resolveAutoStashItem(entry) is not { } name) continue;
            Send($"hide {name}");
            hiddenItems.Add(name);
        }

        if (dispatched.Count > 0 || hiddenItems.Count > 0)
        {
            _log?.Info(LogCategory,
                $"stash dispatched room=({enteredRoom.Map},{enteredRoom.Room}) "
                + $"currencies={dispatched.Count} items={hiddenItems.Count}");
            StashExecuted?.Invoke(new StashDispatch(enteredRoom, dispatched, hiddenItems));
        }
    }

    private void DispatchOne(string currency, long held, long keep,
                              List<(string, long)> dispatched)
    {
        long excess = held - keep;
        if (excess <= 0) return;
        Send($"hide {excess} {currency}");
        dispatched.Add((currency, excess));
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
