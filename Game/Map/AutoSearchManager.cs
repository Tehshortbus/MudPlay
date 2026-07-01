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
/// room. Off by default; the user arms it manually (toolbar / Action menu
/// / <c>@auto-search</c>) — later PRs add the demand-driven auto-arm when
/// a path needs an item we lack.
/// </para>
/// </remarks>
public sealed class AutoSearchManager
{
    /// <summary>LogService category — <c>[AutoSearch]</c> rows per sent search.</summary>
    public const string LogCategory = "AutoSearch";

    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    public AutoSearchManager(Func<bool> isEnabled, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        _isEnabled = isEnabled;
        _log = log;
    }

    /// <summary>Bind the wire-sender — the gate-wrapped engine pipeline
    /// from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Test seam — bytes the manager asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>
    /// Called on each genuine room change. Sends a bare <c>sea</c> when the
    /// master toggle is on; a no-op otherwise.
    /// </summary>
    public void OnRoomChanged()
    {
        if (!_isEnabled()) return;
        _wire.Send("sea");
        _log?.Debug(LogCategory, "sent 'sea' on room entry");
    }
}
