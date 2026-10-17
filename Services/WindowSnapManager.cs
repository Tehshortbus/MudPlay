using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MudPlay.Services;

// Magnetic window snapping for MudPlay's panel windows — the ones reachable from
// the main terminal's right-click menu / the View menu (main, conversation, party,
// buff watchdog, player workshop, navigation, spell book, session stats). Child
// windows opened from within a panel deliberately don't participate.
//
// Behaviour:
//   - Drag any non-main panel and, when an edge lands within SnapThreshold of
//     another panel's opposite edge (with overlap on the perpendicular axis), it
//     snaps flush against it.
//   - Drag the MAIN window and the whole transitively-snapped cluster moves with
//     it, keeping each panel's relative position.
//   - Grab any non-main panel and it pulls off freely — adjacency is re-derived
//     from live positions every move, so a window dragged away simply stops being
//     part of the cluster. There is no snap graph to maintain.
//
// On Linux the window manager reports a drag once it ends; on Windows every step
// of the drag raises PositionChanged. A snap therefore waits until the panel has
// stopped moving (SettleMs): snapping mid-drag moved the window under the cursor and
// pinned it to whatever edge the drag passed, so it could hardly be pulled away. A
// snap is a small correction after the move, not a live pull, and never one that
// leaves the panel's title bar off the screen's working area (behind the taskbar).
// Positions are physical desktop pixels; sizes are device-independent, so a rect
// is built by scaling the frame size to the window's screen — the same convention
// WindowLayoutStore uses.
//
// Enable is read live from the Global "Snap windows together" setting; when off the
// manager still tracks positions but never moves a window.
public sealed class WindowSnapManager
{
    // Snap when a dragged edge lands within this many physical pixels of another
    // panel's opposite edge.
    internal const int SnapThreshold = 12;
    private const string MainId = "main";

    private static readonly HashSet<string> Participants = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "conversation", "buffwatchdog", "party",
        "workshop", "navigation", "spellbook", "session-stats",
    };

    private readonly Func<bool> _enabled;

    private sealed class Panel
    {
        public required Window Window;
        public required string Id;
        public bool IsMain;
        public PixelPoint LastPos;
        // Set just before we move the window ourselves; the resulting
        // PositionChanged that matches it is swallowed so our own moves don't
        // recurse into snapping / group logic.
        public PixelPoint? Expected;
        public DispatcherTimer? Settle;
    }

    // A dragged panel snaps once it has gone this long without moving.
    private const int SettleMs = 200;
    // The top strip of a panel that must stay on a screen's working area after a snap,
    // so the title bar can still be grabbed.
    private const int TitleStrip = 32;

    private readonly Dictionary<string, Panel> _open = new(StringComparer.OrdinalIgnoreCase);

    // Consecutive main-window moves within this gap are treated as one drag; a longer
    // pause ends it, and the next main move recaptures the cluster.
    private const double DragGapMs = 300;
    // Cluster membership is captured off main's PRE-move rect (flush with the children,
    // so a fast first drag step can't outrun it). A generous tolerance keeps a snapped
    // panel in the cluster even if a frame edge is a few pixels off; non-snapped panels
    // sit far past it.
    private const int DragAdjacencyTolerance = 40;
    // The snapped cluster captured at the START of the current main drag: member id →
    // its fixed offset from the main window. Held rigid for the whole drag so async WM
    // lag (setting Position is a request the WM fulfils later) can't shake a member
    // loose mid-drag. Null between drags.
    private Dictionary<string, PixelPoint>? _clusterOffsets;
    private DateTime _lastClusterMoveAt;

    public WindowSnapManager(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
    }

    // Called by WindowLayoutStore.AttachWindow for every window; non-participants
    // are ignored. Registration is per Window instance (toggle windows re-open as a
    // fresh instance), so the handlers wire once.
    public void Register(Window window, string id)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (id is null || !Participants.Contains(id)) return;

        window.Opened += (_, _) =>
        {
            _open[id] = new Panel
            {
                Window = window,
                Id = id,
                IsMain = string.Equals(id, MainId, StringComparison.OrdinalIgnoreCase),
                LastPos = window.Position,
            };
        };
        window.Closed += (_, _) =>
        {
            if (_open.TryGetValue(id, out Panel? p) && ReferenceEquals(p.Window, window))
            {
                p.Settle?.Stop();
                _open.Remove(id);
            }
        };
        window.PositionChanged += (_, e) => OnMoved(window, id, e.Point);
    }

    // WindowLayoutStore calls this right before it repositions a window itself (a
    // profile-load re-layout), so the snap manager swallows that move instead of
    // treating a main-window reposition as a user drag and hauling the cluster.
    public void ExpectMove(string id, PixelPoint pos)
    {
        if (_open.TryGetValue(id, out Panel? p)) p.Expected = pos;
    }

    private void OnMoved(Window window, string id, PixelPoint newPos)
    {
        if (!_open.TryGetValue(id, out Panel? p) || !ReferenceEquals(p.Window, window))
            return;

        // Our own programmatic move (snap correction, cluster drag, or a layout
        // restore that called ExpectMove) — record and swallow.
        if (p.Expected is { } exp && IsClose(exp, newPos))
        {
            p.Expected = null;
            p.LastPos = newPos;
            return;
        }

        if (!_enabled() || window.WindowState != WindowState.Normal)
        {
            p.LastPos = newPos;
            return;
        }

        if (p.IsMain)
        {
            DragCluster(newPos, p.LastPos);
            p.LastPos = newPos;
            return;
        }

        // A cluster member's own PositionChanged arriving mid main-drag — our shift, or
        // the WM still catching up to it — must NOT read as the user pulling it off, and
        // must NOT overwrite the intended LastPos MoveTo set with a lagging value. Only
        // re-snap a member once the main drag has settled.
        if (_clusterOffsets is not null && _clusterOffsets.ContainsKey(id)
            && (DateTime.UtcNow - _lastClusterMoveAt).TotalMilliseconds < DragGapMs)
            return;

        p.LastPos = newPos;
        ScheduleSettle(p);
    }

    private void ScheduleSettle(Panel p)
    {
        p.Settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(SettleMs), DispatcherPriority.Background,
            (_, _) =>
            {
                p.Settle!.Stop();
                if (_open.TryGetValue(p.Id, out Panel? cur) && ReferenceEquals(cur, p)
                    && _enabled() && p.Window.WindowState == WindowState.Normal)
                    SnapAndSettle(p, p.Window.Position);
            });
        p.Settle.Stop();
        p.Settle.Start();
    }

    // Main moved by the user. On the first move of a drag, capture the snapped cluster
    // and each member's offset from main; then hold every member at that fixed offset
    // for the rest of the drag. Offsets (not per-step adjacency) keep the cluster rigid
    // even while the WM lags behind our position requests.
    private void DragCluster(PixelPoint newMainPos, PixelPoint mainLastPos)
    {
        DateTime now = DateTime.UtcNow;
        bool newDrag = _clusterOffsets is null
            || (now - _lastClusterMoveAt).TotalMilliseconds > DragGapMs;
        _lastClusterMoveAt = now;

        if (newDrag)
        {
            // Membership + offsets are both measured against main's PRE-move position
            // (mainLastPos == where main sat while the children were snapped to it; it's
            // still pre-move here, OnMoved updates it after us). Capturing the offset from
            // the pre-move base — not the already-moved live position — means the first
            // drag step is applied to the members like every other step, so they stay
            // exactly flush instead of lagging (and drifting) by that first step. Children
            // use their live at-rest rect; the generous DragAdjacencyTolerance keeps a
            // snapped panel in even if a frame edge is a few px off.
            Dictionary<string, PixelRect> rects = new(StringComparer.OrdinalIgnoreCase);
            foreach (Panel p in _open.Values)
                rects[p.Id] = p.IsMain ? RectAt(p, p.LastPos) : RectOf(p);

            _clusterOffsets = new(StringComparer.OrdinalIgnoreCase);
            foreach (string cid in ConnectedFrom(MainId, rects, DragAdjacencyTolerance))
            {
                if (string.Equals(cid, MainId, StringComparison.OrdinalIgnoreCase)) continue;
                if (_open.TryGetValue(cid, out Panel? m))
                    _clusterOffsets[cid] = m.Window.Position - mainLastPos;
            }
        }

        foreach ((string cid, PixelPoint offset) in _clusterOffsets!)
            if (_open.TryGetValue(cid, out Panel? m))
                MoveTo(m, newMainPos + offset);
    }

    // A non-main panel was dragged: snap its nearest edge to another panel if one is
    // within reach. Returns true (and records the snapped position as LastPos via
    // MoveTo) when it snapped; false when it stays where the user dropped it.
    private bool SnapAndSettle(Panel c, PixelPoint newPos)
    {
        PixelRect cr = RectAt(c, newPos);
        List<PixelRect> others = new();
        foreach (Panel v in _open.Values)
        {
            if (ReferenceEquals(v, c) || v.Window.WindowState != WindowState.Normal) continue;
            others.Add(RectOf(v));   // intended position — a reference window isn't being dragged
        }

        PixelRect[] workAreas = c.Window.Screens?.All.Select(s => s.WorkingArea).ToArray() ?? [];
        (int axis, int shift) = ComputeSnap(cr, others, SnapThreshold, workAreas);
        if (axis == 0) return false;

        PixelPoint snapped = axis == 1
            ? new PixelPoint(newPos.X + shift, newPos.Y)
            : new PixelPoint(newPos.X, newPos.Y + shift);
        if (snapped == newPos) return false;
        MoveTo(c, snapped);
        return true;
    }

    private void MoveTo(Panel p, PixelPoint pos)
    {
        p.Expected = pos;
        p.LastPos = pos;
        p.Window.Position = pos;
    }

    // ----- Pure geometry -----------------------------------------------------

    // The single closest edge snap for c against others, or (0, 0) when nothing is
    // within threshold. axis 1 = adjust X, 2 = adjust Y; shift is the signed delta.
    // Adjacency only (opposite edges touch), so panels tile beside/above each other
    // rather than overlapping — and only one axis moves (no perpendicular align).
    // With workAreas given, an edge that would put c's title strip off every screen's
    // working area is passed over, so the next-closest edge can still win.
    internal static (int Axis, int Shift) ComputeSnap(
        PixelRect c, IReadOnlyList<PixelRect> others, int threshold,
        IReadOnlyList<PixelRect>? workAreas = null)
    {
        int bestAxis = 0, bestShift = 0, bestAbs = threshold + 1;

        void Consider(int shift, int axis)
        {
            int abs = Math.Abs(shift);
            if (abs >= bestAbs) return;
            if (workAreas is { Count: > 0 })
            {
                PixelPoint at = axis == 1 ? new PixelPoint(c.X + shift, c.Y) : new PixelPoint(c.X, c.Y + shift);
                PixelRect strip = new(at, new PixelSize(c.Width, Math.Min(TitleStrip, c.Height)));
                if (!workAreas.Any(a => a.Intersect(strip) == strip)) return;
            }
            bestAbs = abs; bestShift = shift; bestAxis = axis;
        }

        foreach (PixelRect v in others)
        {
            if (RangesOverlap(c.Y, c.Bottom, v.Y, v.Bottom))   // side by side → snap X
            {
                Consider(v.X - c.Right, 1);        // c to the left of v
                Consider(v.Right - c.X, 1);        // c to the right of v
            }
            if (RangesOverlap(c.X, c.Right, v.X, v.Right))     // stacked → snap Y
            {
                Consider(v.Y - c.Bottom, 2);       // c above v
                Consider(v.Bottom - c.Y, 2);       // c below v
            }
        }
        return (bestAxis, bestShift);
    }

    // Two rects are clustered when a pair of opposite edges touches (within tol) and
    // they overlap on the perpendicular axis — flush neighbours, not mere overlap.
    internal static bool Adjacent(PixelRect a, PixelRect b, int tol)
    {
        bool vOverlap = a.Y < b.Bottom && b.Y < a.Bottom;
        bool hOverlap = a.X < b.Right && b.X < a.Right;
        bool hTouch = Math.Abs(a.Right - b.X) <= tol || Math.Abs(b.Right - a.X) <= tol;
        bool vTouch = Math.Abs(a.Bottom - b.Y) <= tol || Math.Abs(b.Bottom - a.Y) <= tol;
        return (hTouch && vOverlap) || (vTouch && hOverlap);
    }

    // BFS the adjacency graph from start; returns every reachable id (including start).
    internal static HashSet<string> ConnectedFrom(
        string start, IReadOnlyDictionary<string, PixelRect> rects, int tol)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (!rects.ContainsKey(start)) return seen;

        Queue<string> queue = new();
        queue.Enqueue(start);
        seen.Add(start);
        while (queue.Count > 0)
        {
            PixelRect a = rects[queue.Dequeue()];
            foreach ((string id, PixelRect b) in rects)
            {
                if (seen.Contains(id) || !Adjacent(a, b, tol)) continue;
                seen.Add(id);
                queue.Enqueue(id);
            }
        }
        return seen;
    }

    private static bool RangesOverlap(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;

    private static bool IsClose(PixelPoint a, PixelPoint b)
        => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2;

    // A panel's rect at its LIVE position. Used for snap references and the drag-start
    // cluster capture — both happen while the panel is at rest, so live is exactly where
    // it sits (and, unlike LastPos, can't be left stale by a silent WM frame settle).
    private static PixelRect RectOf(Panel p) => RectAt(p, p.Window.Position);

    private static PixelRect RectAt(Panel p, PixelPoint pos)
    {
        Window w = p.Window;

        // On Windows the reported Position / FrameSize include the invisible resize
        // border, so snapping the frame rects flush leaves a visible gap; use the DWM
        // visible bounds instead so visible edges land flush. The inset is a fixed
        // border width, so it applies to a hypothetical `pos` too. No-op / fallback
        // off Windows and on any failure — where FrameSize already IS the visible frame.
        if (NativeWindowFrame.TryGetVisibleFrame(w, out PixelPoint inset, out PixelSize visible))
            return new PixelRect(new PixelPoint(pos.X + inset.X, pos.Y + inset.Y), visible);

        double scale = w.Screens?.ScreenFromWindow(w)?.Scaling ?? w.RenderScaling;
        if (scale <= 0) scale = 1;
        Size size = w.FrameSize ?? w.Bounds.Size;
        return new PixelRect(pos, new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale))));
    }
}
