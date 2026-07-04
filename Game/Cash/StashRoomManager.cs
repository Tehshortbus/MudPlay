using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E follow-up — stash dispatch for user-marked stash
/// rooms. Dispatches one <c>hide N &lt;coin&gt;</c> command per
/// currency whose held amount exceeds its <see cref="CashSettings"/>
/// <c>KeepXxxOnHand</c> floor, then one <c>hide &lt;item&gt;</c> per
/// carried item flagged <see cref="Models.GameData.ItemOverlay.AutoStash"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two triggers.</b> A stash fires either as a step of an
/// auto-deposit reroute (when the wealth / coin gate trips while a Loop
/// or Auto-Lair is running and the configured destination is a stash
/// room, <see cref="AutoDepositManager"/> walks the character there and
/// calls <see cref="ExecuteStash"/> on arrival) OR when the character
/// naturally passes through a stash room that sits on the active
/// loop / lair route — a dedicated detour is only spent on a stash room
/// that is off-route. A purely manual walk through a stash room while no
/// engine is running never triggers a hide (per user direction).
/// </para>
/// <para>
/// Room set lives on <see cref="CharacterProfile.StashRooms"/> — the
/// same list <see cref="Services.MovementFilter"/> uses, populated
/// by the right-click "Toggle: Stash room" on the Navigation map.
/// Per-currency keep-on-hand lives on <see cref="CashSettings"/> so
/// the rules apply uniformly across every stash room (per user
/// direction — no per-room rules).
/// </para>
/// <para>
/// Stash rooms hold cash <i>and</i> items (banks are cash-only): every
/// carried, unworn item whose game-data <c>AutoStash</c> flag is set is
/// hidden by its canonical name. The per-item opt-in comes from the
/// injected resolver, which reads the 4-tier
/// <see cref="Models.GameData.ItemOverlay"/> override.
/// </para>
/// <para>
/// Master gate: <see cref="AutoActionDefaults.AutoGetCash"/> — same
/// toggle as <see cref="CashManager"/>. Item stashing rides the same
/// gate: a stash is one operation ("dump my excess cash and my
/// auto-stash items"), not two independently toggled behaviours.
/// </para>
/// </remarks>
public sealed class StashRoomManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[StashRoom]</c>
    /// rows per entry + dispatch.</summary>
    public const string LogCategory = "StashRoom";

    /// <summary>
    /// What a single stash dispatch put away: the room it happened in,
    /// the currency amounts hidden, and the canonical names of the items
    /// hidden. Either list may be empty (cash-only or items-only stash),
    /// but the event only fires when at least one is non-empty.
    /// </summary>
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

    /// <summary>Fires after a successful stash dispatch. Carries the
    /// room key, the (currency, amount) pairs, and the item names that
    /// were sent.</summary>
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

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Called by <see cref="AutoDepositManager"/> on arrival at a stash
    /// destination during an auto-deposit reroute. Dispatches one
    /// <c>hide N &lt;coin&gt;</c> per currency whose held amount exceeds
    /// its keep-on-hand floor. Guarded by the cash master toggle and a
    /// defensive stash-room membership check (the caller only routes here
    /// for stash destinations, but the guard keeps the contract local).
    /// </summary>
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
