using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Shared drawing surface every startup SplashScene paints through. Wraps the
// harness's standalone TerminalScreen and centralises the primitives + the mud
// palette so each scene speaks one visual language — the same browns, the same
// splat/slide look — and stays short.
//
// Coordinate contract: the top TitleRows are reserved for the header (title /
// byline / hint) the harness draws; scenes paint only rows [TitleRows, Rows).
// Every Put* clips to that band, so a scene can't scribble on the header or off
// the grid.
public sealed class SplashCanvas
{
    // Rows reserved at the top for the header. Mud never touches them.
    public const int TitleRows = 4;

    public TerminalScreen Screen { get; }
    public int Cols => Screen.Cols;
    public int Rows => Screen.Rows;

    public SplashCanvas(TerminalScreen screen) => Screen = screen;

    // ----- frame footprint tracking -----------------------------------------
    // Every Put records the cell it touched into _footprint; BeginFrame rolls that
    // into _prevFootprint and clears those cells back to background before the next
    // frame draws — so a frame only ever repaints its own moving footprint instead
    // of the whole lens re-cleared every tick.
    private HashSet<int> _footprint = new();
    private HashSet<int> _prevFootprint = new();
    private static readonly Cell BlankCell = new(' ', BgAttr);

    // Start a fresh animation frame: clear the previous frame's scene footprint back
    // to background (the header and untouched background persist), then let the scene
    // draw. Call once before scene.Render each tick.
    public void BeginFrame()
    {
        (_prevFootprint, _footprint) = (_footprint, _prevFootprint);
        _footprint.Clear();
        foreach (int idx in _prevFootprint)
            Screen.Put(idx % Cols, idx / Cols, BlankCell);
    }

    // Drop all footprint state — used after a full redraw (init / resize) where the
    // whole grid was just cleared, so there's no prior frame to un-draw next tick.
    public void ResetFootprint()
    {
        _footprint.Clear();
        _prevFootprint.Clear();
    }

    // ----- palette (shared mud tones) ---------------------------------------
    public static readonly CellAttributes BgAttr    = CellAttributes.Default;
    public static readonly CellAttributes BrownDark = Rgb(64, 44, 26);
    public static readonly CellAttributes Brown     = Rgb(104, 72, 40);
    public static readonly CellAttributes BrownLite = Rgb(150, 108, 62);
    public static readonly CellAttributes MudGreen  = Rgb(86, 96, 44);
    public static readonly CellAttributes Fleck     = Rgb(176, 134, 80);

    public static CellAttributes Rgb(byte r, byte g, byte b) =>
        CellAttributes.Default.WithForeground(TerminalColor.Rgb(r, g, b));

    // ----- basic drawing ----------------------------------------------------

    // Put a single glyph. Clips to the mud band and the grid; ' ' is skipped so
    // callers can pass sprite strings with transparent gaps.
    public void Put(int x, int y, char ch, CellAttributes attr)
    {
        if (ch == ' ') return;
        if (x < 0 || x >= Cols || y < TitleRows || y >= Rows) return;
        Screen.Put(x, y, new Cell(ch, attr));
        _footprint.Add(y * Cols + x);
    }

    // Draw a string left-to-right from (x, y). Spaces leave the underlying cell
    // untouched (transparent), so multi-line sprites can overlap cleanly.
    public void Str(int x, int y, string text, CellAttributes attr)
    {
        for (int i = 0; i < text.Length; i++) Put(x + i, y, text[i], attr);
    }

    // Emit a settled-mud cell as a BACKGROUND fill (a space whose bg is the mud
    // colour) rather than a foreground block glyph. A '█' block and a bg-filled cell
    // look identical, and the softer dither shades flatten to solid colour — the win
    // is that a run of same-colour mud renders as ONE batched rectangle with no
    // glyphs to draw, which keeps the whole-band mud fills (cover / slide) cheap.
    // Colour still varies by intensity + noise (MudAttr),
    // so the mud keeps its shading; only the fine ░▒▓ grain is gone. Unlike Put, this
    // writes an opaque cell even though the glyph is a space, so it can't be skipped.
    public void PutMud(int x, int y, double it, int sx, int sy)
        => PutBg(x, y, BgAttr.WithBackground(MudAttr(it, sx, sy).Foreground));

    // Write an opaque BACKGROUND-only cell (a space carrying a bg colour). Unlike Put
    // it is NOT skipped for a space — flat colour fills (mud, the chocobo's plain) use
    // it so a run of same-colour cells renders as one batched rectangle with no glyph.
    public void PutBg(int x, int y, CellAttributes attr)
    {
        if (x < 0 || x >= Cols || y < TitleRows || y >= Rows) return;
        Screen.Put(x, y, new Cell(' ', attr));
        _footprint.Add(y * Cols + x);
    }

    // Flat background-colour rectangle (inclusive bounds) — cheap batched fill.
    public void FillBg(int x0, int y0, int x1, int y1, CellAttributes attr)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            PutBg(x, y, attr);
    }

    // A rough integer line (Bresenham) for limbs and other thin strokes.
    public void Line(int x0, int y0, int x1, int y1, char ch, CellAttributes attr)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            Put(x0, y0, ch, attr);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    // ----- mud language -----------------------------------------------------

    // Mud-sheet intensity at a cell for a splat centred at (cx, cy) with radius r
    // (0 outside). Cells are ~1:2, so y is scaled to keep the blob round. The edge
    // is noised so the flood reads organic, never a clean circle.
    public static double SplatAt(int x, int y, int cx, int cy, double r)
    {
        double dx = x - cx, dy = (y - cy) * 2.0;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double edge = r * (0.80 + 0.32 * Noise(x, y));
        return d > edge ? 0 : 1.0 - d / Math.Max(1.0, edge);
    }

    // The maximum splat radius that floods the whole lens from centre.
    public double MaxRadius() => Math.Max(Cols, (Rows - TitleRows) * 2) * 0.66;

    // Flood the lens with mud out to radius r from (cx, cy). The shared "mud
    // covers the view" beat every scene borrows for its payoff.
    public void CoverMud(int cx, int cy, double r)
    {
        for (int y = TitleRows; y < Rows; y++)
        for (int x = 0; x < Cols; x++)
        {
            double it = SplatAt(x, y, cx, cy, r);
            if (it > 0) PutMud(x, y, it, x, y);
        }
    }

    // A small solid mud ball (flying gob, falling plop). Round despite 1:2 cells.
    public void Blob(int cx, int cy, int rad)
    {
        for (int y = cy - rad; y <= cy + rad; y++)
        for (int x = cx - rad; x <= cx + rad; x++)
        {
            double dx = x - cx, dy = (y - cy) * 2.0;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > rad + 0.4) continue;
            double it = 1.0 - d / Math.Max(1.0, rad + 0.4);
            PutMud(x, y, it, x, y);
        }
    }

    // A golden-angle spray of flecks just outside a splat of radius r — the
    // spatter ring that sells an impact.
    public void Flecks(int cx, int cy, double r, int count = 16)
    {
        for (int i = 0; i < count; i++)
        {
            double a = i * 2.399963;
            double dist = r * (1.02 + 0.35 * Noise(i, i * 7));
            int x = cx + (int)Math.Round(Math.Cos(a) * dist);
            int y = cy + (int)Math.Round(Math.Sin(a) * dist * 0.5);
            Put(x, y, Noise(x, y) > 0.5 ? '·' : '•', Noise(y, x) > 0.6 ? MudGreen : Brown);
        }
    }

    // Run a full-lens mud sheet (splat centred at cx, cy) down and off the bottom
    // as p goes 0→1. Each COLUMN slides at its own pace so the receding top edge
    // stays ragged and reads as mud running, not a solid panel dropping; sparse
    // residue streaks cling in the cleared band and wash away by the end. This is
    // the shared "clear the lens" ending — at p=1 the band is empty, giving every
    // scene the same clean boundary so the harness can swap to any other scene
    // seamlessly.
    public void SlideMudOff(double p, int cx, int cy)
    {
        double maxR = MaxRadius();
        // Over-clear so even the slowest column empties the splat's full extent by
        // p=1 (margin must exceed 0.5 / minSpeed).
        double baseOff = EaseInSoft(p) * (Rows + maxR) * 1.3;
        double residueFade = 1.0 - p;

        for (int x = 0; x < Cols; x++)
        {
            double speed = 0.42 + 1.25 * Noise(x, 991);
            int off = (int)Math.Round(baseOff * speed);
            int edgeY = TitleRows + off;
            double runny = Noise(x, 13);
            char streakCh = runny > 0.85 ? '║' : '│';

            for (int y = TitleRows; y < Rows; y++)
            {
                if (y < edgeY)
                {
                    double nearEdge = 1.0 - (edgeY - y) / (double)Math.Max(1, off);
                    double prob = (0.05 + 0.55 * nearEdge) * residueFade * (0.35 + runny);
                    if (runny > 0.25 && Noise(x, y) < prob)
                    {
                        CellAttributes c = y > edgeY - 2 ? BrownLite : y > edgeY - 6 ? Brown : BrownDark;
                        Put(x, y, y > edgeY - 2 && Noise(x, y * 3) > 0.7 ? '•' : streakCh, c);
                    }
                    continue;
                }
                double it = SplatAt(x, y - off, cx, cy, maxR);
                if (it > 0) PutMud(x, y, it, x, y - off);
            }
        }
    }

    // Like SlideMudOff, but slides a FULL, uniform mud sheet down and off — for
    // scenes whose lens is caked evenly (rain accumulation, a filled pool) rather
    // than a radial splat. Same per-column ragged speed + residue so it reads as
    // running mud; at p=1 the band is clear for the seamless hand-off.
    public void SlideSheetOff(double p)
    {
        double maxR = MaxRadius();
        double baseOff = EaseInSoft(p) * (Rows + maxR) * 1.3;
        double residueFade = 1.0 - p;

        for (int x = 0; x < Cols; x++)
        {
            double speed = 0.42 + 1.25 * Noise(x, 991);
            int off = (int)Math.Round(baseOff * speed);
            int edgeY = TitleRows + off;
            double runny = Noise(x, 13);
            char streakCh = runny > 0.85 ? '║' : '│';

            for (int y = TitleRows; y < Rows; y++)
            {
                if (y < edgeY)
                {
                    double nearEdge = 1.0 - (edgeY - y) / (double)Math.Max(1, off);
                    double prob = (0.05 + 0.55 * nearEdge) * residueFade * (0.35 + runny);
                    if (runny > 0.25 && Noise(x, y) < prob)
                    {
                        CellAttributes cc = y > edgeY - 2 ? BrownLite : y > edgeY - 6 ? Brown : BrownDark;
                        Put(x, y, y > edgeY - 2 && Noise(x, y * 3) > 0.7 ? '•' : streakCh, cc);
                    }
                    continue;
                }
                PutSheet(x, y, x, y - off);            // the (full) sheet, shifted down
            }
        }
    }

    // Fill the lens with mud FROM THE TOP down as p goes 0→1: a ragged, dripping
    // leading edge descends per-column (some rivulets race ahead) until the whole
    // band is caked at p=1. For scenes where the mud pours down over the lens from
    // above (a geyser raining back, a slop tipped over the top).
    public void FillFromTop(double p)
    {
        int band = Rows - TitleRows;
        for (int x = 0; x < Cols; x++)
        {
            double speed = 1.0 + 0.6 * Noise(x, 411);          // drips race ahead
            int edge = TitleRows + (int)Math.Round(p * band * speed);
            for (int y = TitleRows; y < Rows; y++)
            {
                if (y <= edge)
                {
                    PutSheet(x, y, x, y);
                }
                else if (y <= edge + 2 && Noise(x, y * 5) > 0.6)
                {
                    Put(x, y, '│', Brown);                       // a drip ahead of the edge
                }
            }
        }
    }

    // One CANONICAL caked-mud cell: the single source of truth for "settled mud on
    // the lens", sampling its texture at (sx, sy). SlideSheetOff / FillFromTop paint
    // with it, and MudPatch stamps it, so a scene that accumulates coverage
    // (rain, a filled pool) hands off to the slide with zero pattern/colour snap.
    // Intensity stays in the ordinary Brown/BrownDark band — never the bright
    // highlights.
    public void PutSheet(int x, int y, int sx, int sy)
    {
        double it = 0.35 + 0.33 * Noise(sx, sy);
        PutMud(x, y, it, sx, sy);
    }

    // Stamp a round patch of the canonical sheet (sampled at each cell's own
    // coords) — a landed rain-splat that tiles seamlessly into SlideSheetOff.
    public void MudPatch(int cx, int cy, int rad)
    {
        for (int y = cy - rad; y <= cy + rad; y++)
        for (int x = cx - rad; x <= cx + rad; x++)
        {
            double dx = x - cx, dy = (y - cy) * 2.0;
            if (Math.Sqrt(dx * dx + dy * dy) > rad + 0.4) continue;
            PutSheet(x, y, x, y);
        }
    }

    // Clear a caked lens from the EDGES IN: the surviving mud is a shrinking central
    // island, the outer ring clearing first, until p=1 clears everything. An
    // alternative ending to the slide, for scenes that read better closing in.
    public void ClearFromEdges(double p)
    {
        int cx = Cols / 2, cy = (Rows + TitleRows) / 2;
        double r = (1.0 - Smooth(p)) * MaxRadius() * 1.45;
        for (int y = TitleRows; y < Rows; y++)
        for (int x = 0; x < Cols; x++)
        {
            double dx = x - cx, dy = (y - cy) * 2.0;
            double edge = r * (0.85 + 0.3 * Noise(x, y));
            if (Math.Sqrt(dx * dx + dy * dy) > edge) continue;   // cleared (outer ring)
            PutSheet(x, y, x, y);
        }
    }

    // Mud colour for a cell — foreground on a glyph, or (via PutMud) the background of
    // a flattened bg-fill. Colour varies by intensity + a stable per-cell noise so the
    // mud reads shaded rather than a flat slab.
    public static CellAttributes MudAttr(double it, int x, int y)
    {
        double n = Noise(y, x);
        if (n > 0.86) return MudGreen;
        if (it > 0.78) return n > 0.5 ? BrownLite : Fleck;
        if (it > 0.45) return Brown;
        return BrownDark;
    }

    // ----- math -------------------------------------------------------------

    // Stable per-cell hash noise in [0,1) — no per-frame jitter, so mud grows and
    // slides smoothly rather than sizzling.
    public static double Noise(int x, int y)
    {
        unchecked
        {
            int h = x * 73856093 ^ y * 19349663;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h >> 8) & 0xFFFF) / 65535.0;
        }
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double EaseOut(double p) => 1.0 - (1.0 - p) * (1.0 - p);
    public static double EaseIn(double p) => p * p;
    // Gentle acceleration: a touch sticky at first, then a steady slow run.
    public static double EaseInSoft(double p) => p * p * (0.6 + 0.4 * p);
    // Smoothstep 0→1.
    public static double Smooth(double p) { p = Math.Clamp(p, 0, 1); return p * p * (3 - 2 * p); }
}
