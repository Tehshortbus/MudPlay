using System.Text;
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

    private readonly CashManager _cash;
    private readonly ProfileService _profile;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>Fires after a successful stash dispatch. Carries the
    /// room key + the (currency, amount) pairs that were sent.</summary>
    public event Action<RoomKey, IReadOnlyList<(string Currency, long Amount)>>? StashExecuted;

    public StashRoomManager(
        CashManager cash,
        ProfileService profile,
        Func<CashSettings> readCash,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _cash = cash;
        _profile = profile;
        _readCash = readCash;
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
        _log?.Debug(LogCategory,
            $"entered stash room map={enteredRoom.Map} room={enteredRoom.Room}");

        List<(string Currency, long Amount)> dispatched = new();
        DispatchOne("copper",   cash.KeepCopperOnHand,   dispatched);
        DispatchOne("silver",   cash.KeepSilverOnHand,   dispatched);
        DispatchOne("gold",     cash.KeepGoldOnHand,     dispatched);
        DispatchOne("platinum", cash.KeepPlatinumOnHand, dispatched);
        DispatchOne("runic",    cash.KeepRunicOnHand,    dispatched);

        if (dispatched.Count > 0)
        {
            _log?.Info(LogCategory,
                $"stash dispatched room=({enteredRoom.Map},{enteredRoom.Room}) currencies={dispatched.Count}");
            StashExecuted?.Invoke(enteredRoom, dispatched);
        }
    }

    private void DispatchOne(string currency, long keep,
                              List<(string, long)> dispatched)
    {
        long held = _cash.HeldCoin(currency);
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
