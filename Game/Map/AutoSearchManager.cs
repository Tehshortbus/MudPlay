using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Base auto-search engine — issues a bare <c>sea</c> on each room entry
/// while the <see cref="Models.Profile.AutoActionDefaults.AutoSearch"/>
/// master toggle is on. A room-wide search reveals concealed items, which
/// surface on the "You notice ... here." survey line and are then picked
/// up by <see cref="Inventory.AutoGetItemsManager"/> /
/// <see cref="Cash.CashManager"/> exactly as visible loot would be.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole-room scan, distinct from
/// <see cref="HiddenExitRevealManager"/>'s targeted <c>sea &lt;dir&gt;</c>
/// retry loop (which reveals a specific hidden <i>exit</i> the walker
/// needs). The two are complementary and never contend — one searches the
/// room, the other a direction — so both can fire on the same entry.
/// </para>
/// <para>
/// Fired from <c>AppServices</c>'s <see cref="RoomTracker.StateChanged"/>
/// seam, which already collapses same-room redisplays to a single
/// genuine room change, so <see cref="OnRoomChanged"/> runs once per new
/// room.
/// </para>
/// <para>
/// Two independent gates arm the search: the persisted master toggle
/// (<see cref="Models.Profile.AutoActionDefaults.AutoSearch"/>, off by
/// default, driven by the toolbar / Action menu / <c>@auto-search</c>),
/// and a transient <i>demand</i> gate — Settings → Other "search rooms if
/// item needed" while a route needs an item we lack
/// (<see cref="PathItemDemandTracker.SearchDemandActive"/>). Either being
/// true issues the search; the demand gate never mutates the persisted
/// flag, so it can't strand the toggle on once the item is found.
/// </para>
/// </remarks>
public sealed class AutoSearchManager
{
    /// <summary>LogService category — <c>[AutoSearch]</c> rows per sent search.</summary>
    public const string LogCategory = "AutoSearch";

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isDemandActive;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    public AutoSearchManager(
        Func<bool> isEnabled,
        Func<bool>? isDemandActive = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        _isEnabled = isEnabled;
        _isDemandActive = isDemandActive ?? (static () => false);
        _log = log;
    }

    /// <summary>Bind the wire-sender — the gate-wrapped engine pipeline
    /// from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Test seam — bytes the manager asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>
    /// Called on each genuine room change. Sends a bare <c>sea</c> when the
    /// master toggle is on OR the demand gate is active (a route needs an
    /// item we lack and "search rooms if item needed" is on); a no-op
    /// otherwise.
    /// </summary>
    public void OnRoomChanged()
    {
        bool onDemand = !_isEnabled() && _isDemandActive();
        if (!_isEnabled() && !onDemand) return;
        _wire.Send("sea");
        _log?.Debug(LogCategory,
            onDemand ? "sent 'sea' on room entry (path-item demand)"
                     : "sent 'sea' on room entry");
    }
}
