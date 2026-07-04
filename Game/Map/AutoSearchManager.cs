using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Base auto-search engine — issues a bare sea on each room entry while
// the AutoSearch master toggle is on. A room-wide search reveals
// concealed items, which surface on the "You notice ... here." survey
// line and are then picked up by AutoGetItemsManager / CashManager
// exactly as visible loot would be.
//
// This is the whole-room scan, distinct from HiddenExitRevealManager's
// targeted sea <dir> retry loop (which reveals a specific hidden exit
// the walker needs). The two are complementary and never contend — one
// searches the room, the other a direction — so both can fire on the
// same entry.
//
// Fired from AppServices's RoomTracker.StateChanged seam, which already
// collapses same-room redisplays to a single genuine room change, so
// OnRoomChanged runs once per new room.
//
// Two independent gates arm the search: the persisted master toggle
// (AutoSearch, off by default, driven by the toolbar / Action menu /
// @auto-search), and a transient demand gate — Settings → Other "search
// rooms if item needed" while a route needs an item we lack
// (PathItemDemandTracker.SearchDemandActive). Either being true issues
// the search; the demand gate never mutates the persisted flag, so it
// can't strand the toggle on once the item is found.
public sealed class AutoSearchManager
{
    // LogService category — [AutoSearch] rows per sent search.
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

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Called on each genuine room change. Sends a bare sea when the
    // master toggle is on OR the demand gate is active (a route needs an
    // item we lack and "search rooms if item needed" is on); a no-op
    // otherwise.
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
