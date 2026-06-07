using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E follow-up — on-entry stash plan for user-marked
/// stash rooms. When <c>RoomTracker.StateChanged</c> reports we've
/// arrived in a configured <see cref="StashRoom"/>, dispatch one
/// <c>hide N &lt;coin&gt;</c> command per
/// <see cref="StashCurrencyRule"/> whose held amount exceeds its
/// <see cref="StashCurrencyRule.KeepAtLeast"/> floor.
/// </summary>
/// <remarks>
/// <para>
/// Reads held tallies from <see cref="CashManager"/>; the
/// CashManager's own <see cref="KnownPatterns.CashHidden"/>
/// subscription decrements the tally on the server's confirmation
/// line, so the second visit to the same stash room produces no
/// command (held is already at the floor).
/// </para>
/// <para>
/// v1 scope: cash only, single-pass (one dispatch per entry, no
/// re-evaluation while standing in the room). Items + the
/// retrieval direction ("on visit, search + collect everything")
/// land when the inventory subsystem ships per the MudProxy
/// CashManager audit.
/// </para>
/// <para>
/// Known race (deferred): if the user walks in, walks out, and
/// walks back in within the server's confirmation window, the
/// second dispatch will compute against stale held values and
/// over-stash. Fixing this needs the in-flight 60s delta projection
/// from <see cref="CashManager"/>'s deferred follow-up list; for now
/// the engine accepts the race because real-world stash visits are
/// seconds apart.
/// </para>
/// <para>
/// Master gate: <see cref="AutoActionDefaults.AutoGetCash"/> — same
/// toggle as <see cref="CashManager"/>. Splitting into a separate
/// AutoStash flag is a follow-up if users want finer-grained
/// control.
/// </para>
/// </remarks>
public sealed class StashRoomManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[StashRoom]</c>
    /// rows per entry + dispatch.</summary>
    public const string LogCategory = "StashRoom";

    private readonly CashManager _cash;
    private readonly Func<StashRoomSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>Fires after a successful stash dispatch. Carries the
    /// matched <see cref="StashRoom"/> + the (currency, amount)
    /// pairs that were sent.</summary>
    public event Action<StashRoom, IReadOnlyList<(string Currency, long Amount)>>? StashExecuted;

    public StashRoomManager(
        CashManager cash,
        Func<StashRoomSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _cash = cash;
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        _log = log;
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>. Until set,
    /// the engine logs dispatches but doesn't actually emit the
    /// <c>hide</c> commands.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Called by <c>AppServices</c> when
    /// <c>RoomTracker.StateChanged</c> reports a room change.
    /// Looks up the room key in the user's configured stash list
    /// and dispatches the plan if a match is found.
    /// </summary>
    /// <param name="enteredRoom">The room we just entered.</param>
    public void NoteRoomEntered(RoomKey enteredRoom)
    {
        if (!_isEnabled()) return;

        StashRoomSettings settings = _readSettings();
        if (settings.Rooms.Count == 0) return;

        StashRoom? match = null;
        foreach (StashRoom candidate in settings.Rooms)
        {
            if (candidate.Room is { } r
             && r.Map == enteredRoom.Map
             && r.Room == enteredRoom.Room)
            {
                match = candidate;
                break;
            }
        }
        if (match is null) return;

        _log?.Debug(LogCategory,
            $"entered stash room map={enteredRoom.Map} room={enteredRoom.Room} " +
            $"name={match.DisplayName} rules={match.CurrencyRules.Count}");

        List<(string Currency, long Amount)> dispatched = new();
        foreach (StashCurrencyRule rule in match.CurrencyRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Currency)) continue;
            long held = _cash.HeldCoin(rule.Currency);
            long excess = held - rule.KeepAtLeast;
            if (excess <= 0) continue;

            Send($"hide {excess} {rule.Currency.Trim()}");
            dispatched.Add((rule.Currency, excess));
        }

        if (dispatched.Count > 0)
        {
            _log?.Info(LogCategory,
                $"stash dispatched room='{match.DisplayName}' currencies={dispatched.Count}");
            StashExecuted?.Invoke(match, dispatched);
        }
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
        // No subscriptions to dispose — the engine is driven from
        // AppServices' RoomTracker.StateChanged callback.
    }
}
