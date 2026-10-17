namespace MudPlay.Game.Inventory;

// Coalesces the post-kill room re-render that the cash and item collect engines
// each request. On the LAST monster's death both fire — AutoGetItemsManager's
// drop re-look and CashManager's combat-clear re-display — and each would send
// its own bare Enter, rendering the room TWICE. One Enter re-renders the room for
// both (they read the same "You notice" survey), so this is the single dedup
// authority: the first engine to ask within the window sends the Enter, the
// second coalesces onto that render. UI-thread only — no lock.
public sealed class RoomRedisplayCoordinator
{
    // Matches AutoGetItemsManager's re-look cooldown: long enough to fold the two
    // combat-clear requests (fired within the same frame) into one, short enough
    // that a genuinely later re-render (a fresh kill a round away) isn't
    // suppressed. A room visit lasts far longer, so no per-room reset is needed.
    private const int WindowMs = 750;

    private readonly Func<DateTime> _now;
    private DateTime _lastAt = DateTime.MinValue;

    // now defaults to UtcNow.
    public RoomRedisplayCoordinator(Func<DateTime>? now = null)
        => _now = now ?? (static () => DateTime.UtcNow);

    // True (and stamps the clock) when no room re-render has gone out within the
    // window — the caller then sends the bare Enter itself. A second engine
    // asking right after gets false and skips, so the last-kill re-render happens
    // once, not twice.
    public bool ShouldSend()
    {
        DateTime now = _now();
        if ((now - _lastAt).TotalMilliseconds < WindowMs) return false;
        _lastAt = now;
        return true;
    }
}
