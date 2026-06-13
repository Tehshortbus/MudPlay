using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E follow-up — on-entry stash plan for user-marked
/// stash rooms. When <see cref="RoomTracker.StateChanged"/> reports
/// we've arrived in a room flagged in
/// <see cref="CharacterProfile.StashRooms"/>, dispatch one
/// <c>hide N &lt;coin&gt;</c> command per currency whose held amount
/// exceeds its <see cref="CashSettings"/> <c>KeepXxxOnHand</c> floor.
/// </summary>
/// <remarks>
/// <para>
/// Room set lives on <see cref="CharacterProfile.StashRooms"/> — the
/// same list <see cref="Services.MovementFilter"/> uses, populated
/// by the right-click "Toggle: Stash room" on the Navigation map.
/// Per-currency keep-on-hand lives on <see cref="CashSettings"/> so
/// the rules apply uniformly across every stash room (per user
/// direction — no per-room rules).
/// </para>
/// <para>
/// v1 scope: cash only. Item-side stash rules (dump excess
/// potions / etc.) land when the inventory subsystem ships per the
/// MudProxy CashManager audit.
/// </para>
/// <para>
/// Master gate: <see cref="AutoActionDefaults.AutoGetCash"/> — same
/// toggle as <see cref="CashManager"/>.
/// </para>
/// </remarks>
public sealed class StashRoomManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[StashRoom]</c>
    /// rows per entry + dispatch.</summary>
    public const string LogCategory = "StashRoom";

    private readonly ProfileService _profile;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>Fires after a successful stash dispatch. Carries the
    /// room key + the (currency, amount) pairs that were sent.</summary>
    public event Action<RoomKey, IReadOnlyList<(string Currency, long Amount)>>? StashExecuted;

    public StashRoomManager(
        ProfileService profile,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _profile = profile;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
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
    /// Called by <c>AppServices</c> when <c>RoomTracker.StateChanged</c>
    /// reports a room change. Looks up the room in
    /// <see cref="CharacterProfile.StashRooms"/> and dispatches the
    /// per-currency hide commands if it's a marked stash room.
    /// </summary>
    public void NoteRoomEntered(RoomKey enteredRoom)
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
        // Authoritative per-denomination holdings (the `i`-seeded,
        // delta-tracked snapshot) — NOT CashManager's since-engine-start
        // pickup tally, which never sees the starting balance and would
        // undercount the hide amounts.
        CurrencyHoldings held = _getSnapshot().Currency;
        _log?.Debug(LogCategory,
            $"entered stash room map={enteredRoom.Map} room={enteredRoom.Room}");

        List<(string Currency, long Amount)> dispatched = new();
        DispatchOne("copper",   held.Copper,   cash.KeepCopperOnHand,   dispatched);
        DispatchOne("silver",   held.Silver,   cash.KeepSilverOnHand,   dispatched);
        DispatchOne("gold",     held.Gold,     cash.KeepGoldOnHand,     dispatched);
        DispatchOne("platinum", held.Platinum, cash.KeepPlatinumOnHand, dispatched);
        DispatchOne("runic",    held.Runic,    cash.KeepRunicOnHand,    dispatched);

        if (dispatched.Count > 0)
        {
            _log?.Info(LogCategory,
                $"stash dispatched room=({enteredRoom.Map},{enteredRoom.Room}) currencies={dispatched.Count}");
            StashExecuted?.Invoke(enteredRoom, dispatched);
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
