using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Keeps a checkspell hazard buff up while the walker traverses a hazard room.
//
// Some room hazards (the Scorching Desert's heat, the underwater drown) are
// survived not by HOLDING a counter but by an active buff the player raises with
// `use <item>` — the desert waterskin `use`s to cast buff 711, safe only while
// that buff is up. Carrying the source item is enough to let the route pass the
// hazard gate (RoomHazardIndex.RoomHazard.IsSatisfiedBy checks carrying), but the
// buff must actually be RAISED, or the route walks straight into the damage — the
// "it required a waterskin but never used it" report.
//
// This engine closes that gap. On the walker's approach hook (fired the instant a
// step is committed, before the move bytes, so the `use` lands the buff before we
// arrive), it resolves the room's hazard and, for each checkspell counter whose
// source item we carry, `use`s it — but only when the buff would have lapsed. A
// per-source-item timer keyed on the buff's data-driven duration debounces the
// re-use: a fast traverse of a long hazard stretch spends ONE charge, and a
// stretch outlasting the buff re-raises it once the window closes (matching "if
// you're still in the desert when it expires, use the waterskin again"). Charges
// are finite (a fresh waterskin holds 3; players carry two or three), so the
// timer is what keeps it from burning one per room.
//
// No master toggle: surviving a hazard room the route already commits to walking
// is not opt-in (mirrors auto-light's "leave it off if you don't want it" — here
// the equivalent is simply not routing through the hazard). It only ever acts when
// a checkspell hazard and a carried source item coincide during a live walk.
public sealed class AutoHazardCounterProvisioner
{
    // LogService category — [HazardCounter] rows per buff raise / skip.
    public const string LogCategory = "HazardCounter";

    // Re-raise the buff this many seconds BEFORE its computed expiry, so a step
    // into the next hazard room never lands in the gap between lapse and refresh.
    private const int RefreshMarginSeconds = 15;

    // Fallback refresh interval when the buff's duration isn't in the data (Dur 0):
    // still re-use periodically rather than once-and-never so an un-timed counter
    // doesn't silently lapse.
    private const int UnknownDurationRefreshSeconds = 60;

    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int, RoomHazardIndex.RoomHazard?> _hazardForSpell;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Per buff-source item id: when we last `use`d it. The refresh window is the
    // buff's duration minus the margin; a use inside that window is a no-op.
    private readonly Dictionary<int, DateTimeOffset> _lastUsed = new();

    public AutoHazardCounterProvisioner(
        Func<RoomKey, Room?> resolveRoom,
        Func<int, RoomHazardIndex.RoomHazard?> hazardForSpell,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(resolveRoom);
        ArgumentNullException.ThrowIfNull(hazardForSpell);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        _resolveRoom = resolveRoom;
        _hazardForSpell = hazardForSpell;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel. Until bound, a `use` is recorded for tests only.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the engine asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Predictive one-room-lookahead buff. The walker / loop-runner call this the
    // instant they commit to a step, with the room about to be entered, BEFORE the
    // move bytes go out. If that room's cast-on-enter spell is a checkspell hazard
    // whose buff source we carry, we `use` it now — so the buff is up when the step
    // lands. A no-op for a seeable / benign / passive-counter room; the per-item
    // timer skips a re-use while the buff is still covering us.
    public void OnApproachingRoom(RoomKey target)
    {
        if (_resolveRoom(target) is not { } room) return;
        if (room.Spell <= 0) return;
        if (_hazardForSpell(room.Spell) is not { } hazard) return;
        foreach (RoomHazardIndex.BuffCounter counter in hazard.BuffCounters)
            TryRaiseBuff(counter);
    }

    private void TryRaiseBuff(RoomHazardIndex.BuffCounter counter)
    {
        int pick = 0;
        foreach (int id in counter.SourceItems)
            if (_carriedCount(id) > 0) { pick = id; break; }
        if (pick == 0)
        {
            // Nothing carried to raise this buff. The route's own gating / obtain
            // detour owns getting one; here we can only note the exposure.
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: no source item carried — can't raise");
            return;
        }

        int refreshSec = counter.DurationSeconds > 0
            ? Math.Max(1, counter.DurationSeconds - RefreshMarginSeconds)
            : UnknownDurationRefreshSeconds;
        DateTimeOffset now = _now();
        if (_lastUsed.TryGetValue(pick, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(refreshSec))
            return;   // buff still up — don't spend a charge

        string? name = _itemName(pick);
        if (string.IsNullOrWhiteSpace(name))
        {
            _log?.Debug(LogCategory,
                $"buff spell {counter.BuffSpell}: item {pick} has no name — can't `use`");
            return;
        }

        _wire.Send($"use {name}");
        _lastUsed[pick] = now;
        _log?.Info(LogCategory,
            $"raised buff {counter.BuffSpell} with `use {name}` (refresh ~{refreshSec}s)");
    }
}
