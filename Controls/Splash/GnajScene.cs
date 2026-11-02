using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Gnaj the Creator in his laboratory, making monsters. Specimen vats bubble along the
// wall with his half-made creatures floating in them; beside him stands his
// interdimensional portal device, a great ring wound with coils. He throws up his
// arms and rants, the device crackles up to full power, the things in the vats open
// their eyes, and he casts eldritch storm (esto, the spell he casts in the game): he
// glows bright red and points a finger at us, and the storm bursts out of it across
// the whole lens, then dies away.
//
// His words, in a bubble, are from his greeting; the cast is as the game tells it.
public sealed class GnajScene : SplashScene
{
    public override int LoopFrames => 160;

    private const int FadeInEnd  = 12;
    private const int ShriekAt   = 30;
    private const int PowerAt    = 84;    // the device starts to crackle
    private const int WakeAt     = 104;   // the specimens open their eyes
    private const int CastAt     = 116;   // he glows bright red and points at us
    private const int StormAt    = 124;   // the storm leaves his finger
    private const int StormFull  = 136;   // it fills the lens
    private const int StormEnd   = 154;   // and has died away
    // 154..159: clear.

    private const string Shriek = "Gnaj is the end of you, behold the furious wrath of the beginner of all things!";

    private static readonly (byte, byte, byte) WallC = (30, 28, 34), WallDk = (20, 18, 24), FloorC = (40, 36, 40),
        Glass = (150, 190, 200), Fluid = (40, 110, 70), FluidLt = (80, 170, 110), BubbleC = (170, 230, 190),
        Specimen = (40, 60, 44), SpecEye = (255, 60, 60), Bench = (90, 64, 40), Flask = (200, 120, 230),
        FlaskB = (90, 180, 240), Ring = (120, 124, 136), RingLt = (180, 184, 196), Coil = (190, 130, 60),
        Spark = (210, 230, 255), Energy = (150, 80, 220), EnergyHot = (120, 230, 200), Robe = (88, 70, 50),
        RobeDk = (60, 46, 32), Skin = (206, 178, 150), Hair = (226, 226, 232), Eye = (255, 250, 120),
        Quote = (60, 200, 90), BubbleFill = (10, 10, 14), BubbleRim = (130, 130, 140), Glow = (255, 50, 40),
        StormRed = (230, 40, 60), StormViolet = (170, 60, 230), StormWhite = (255, 220, 240);

    private static readonly string[] Gnaj =
    {
        "╲  ▄▄▄  ╱",
        " ╲▐◉▀◉▌╱ ",
        "  ▐▓▓▓▌  ",
        "  ▓███▓  ",
        " ▓█████▓ ",
        " ▓█████▓ ",
        " ▀▀▀▀▀▀▀ ",
    };
    private static readonly string[] GnajPaint =
    {
        "s  hhh  s",
        " shehehs ",
        "  hhhhh  ",
        "  rRRRr  ",
        " rRRRRRr ",
        " rRRRRRr ",
        " rrrrrrr ",
    };
    // Casting: one arm thrust straight at us, finger pointing, the other raised.
    private static readonly string[] Pointing =
    {
        "╲  ▄▄▄   ",
        " ╲▐◉▀◉▌  ",
        "  ▐▓▓▓▌● ",
        "  ▓███▓▀ ",
        " ▓█████▓ ",
        " ▓█████▓ ",
        " ▀▀▀▀▀▀▀ ",
    };
    private static readonly string[] PointingPaint =
    {
        "s  hhh   ",
        " sheheh  ",
        "  hhhhhs ",
        "  rRRRrs ",
        " rRRRRRr ",
        " rRRRRRr ",
        " rrrrrrr ",
    };
    // What floats in a vat: a hunched, half-made thing.
    private static readonly string[] Thing =
    {
        " ▄▄ ",
        "▐●●▌",
        "████",
        "▐██▌",
        " ▌▐ ",
    };
    private static readonly string[] ThingPaint =
    {
        " bb ",
        "beeb",
        "bbbb",
        "bbbb",
        " bb ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= StormEnd) return;
        int top = SplashCanvas.TitleRows;
        int floor = c.Rows - 2;
        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);

        // The portal device stands at the right.
        int ringR = Math.Max(3, Math.Min((floor - top) / 2 - 1, c.Cols / 9));
        int px = c.Cols - ringR * 2 - 4, py = top + (floor - top) / 2;
        void Bg(int x, int y, (byte, byte, byte) col, double l) => c.PutBg(x, y, SplashCanvas.Bg(col, l));
        void Glyph(int x, int y, char ch, (byte, byte, byte) col, double l) => c.Put(x, y, ch, SplashCanvas.Fg(col, l));

        if (f >= StormFull)
        {
            // The storm fills the lens, then dies away from the edges in.
            double p = SplashCanvas.Smooth((f - StormFull) / (double)(StormEnd - 2 - StormFull));
            StormCells(c, f, c.Cols / 2, (top + c.Rows) / 2, c.MaxRadius() * 1.5 * (1 - p), 1 - p * 0.5);
            return;
        }

        // Stone walls and the floor.
        for (int y = top; y < c.Rows; y++)
            for (int x = 0; x < c.Cols; x++)
                Bg(x, y, y >= floor ? FloorC : SplashCanvas.Noise(x / 4, y / 2) > 0.7 ? WallDk : WallC, light);

        // Shelves of flasks along the top of the wall.
        for (int x = 2; x < px - ringR * 2 - 2; x++)
        {
            Glyph(x, top + 1, '▁', Bench, light);
            if (x % 3 == 0) Glyph(x, top, SplashCanvas.Noise(x, 5) > 0.5 ? 'ő' : 'ò', x % 2 == 0 ? Flask : FlaskB, light);
        }

        // The specimen vats: glass tanks of green fluid, bubbles rising, a thing in each.
        double wake = SplashCanvas.Smooth((f - WakeAt) / 6.0);
        int vatTop = top + 3, vatH = floor - vatTop;
        for (int v = 0; v < 2; v++)
        {
            int vx = 2 + v * 9;
            for (int y = vatTop; y < floor; y++)
            {
                Glyph(vx, y, '│', Glass, light);
                Glyph(vx + 6, y, '│', Glass, light);
                for (int x = vx + 1; x < vx + 6; x++) Bg(x, y, y < vatTop + 1 ? FluidLt : Fluid, light);
            }
            for (int x = vx; x <= vx + 6; x++) { Glyph(x, vatTop - 1, '▄', Glass, light); Glyph(x, floor, '▀', Bench, light); }
            for (int b = 0; b < 3; b++)
            {
                int by = floor - 1 - ((f / 2 + b * 4 + v * 3) % Math.Max(1, vatH));
                Glyph(vx + 1 + (b * 2 + v) % 5, by, 'o', BubbleC, light);
            }
            int ty = vatTop + Math.Max(0, (vatH - Thing.Length) / 2) + (int)Math.Round(Math.Sin(f * 0.15 + v));
            for (int r = 0; r < Thing.Length; r++)
                for (int i = 0; i < Thing[r].Length; i++)
                    if (Thing[r][i] != ' ')
                    {
                        bool eye = ThingPaint[r][i] == 'e';
                        Glyph(vx + 2 + i, ty + r, Thing[r][i], eye ? SpecEye : Specimen, eye ? light * wake : light);
                    }
        }

        // The workbench, and Gnaj behind it: arms flung up as he rants, then glowing
        // bright red and pointing a finger at us as he casts.
        int benchY = floor - 2;
        int gx = Math.Max(20, Math.Min(26, px - ringR * 2 - 16)), gy = benchY - Gnaj.Length + 2;
        bool casting = f >= CastAt;
        int sway = f >= ShriekAt && !casting ? (f / 6) % 2 : 0;
        string[] pose = casting ? Pointing : Gnaj;
        string[] posePaint = casting ? PointingPaint : GnajPaint;
        double glow = casting ? 0.5 + 0.5 * Math.Sin(f * 0.9) : 0;
        for (int r = 0; r < pose.Length; r++)
            for (int i = 0; i < pose[r].Length; i++)
            {
                char ch = pose[r][i];
                if (ch == ' ' || gy + r >= benchY) continue;
                (byte, byte, byte) col = posePaint[r][i] switch
                {
                    's' => Skin, 'h' => Hair, 'e' => Eye, 'R' => Robe, _ => RobeDk,
                };
                Glyph(gx + sway + i, gy + r, ch, casting ? SplashCanvas.Mix(col, Glow, 0.55 + 0.35 * glow) : col, light);
            }
        for (int x = gx - 4; x < gx + Gnaj[0].Length + 4; x++) Bg(x, benchY, Bench, light);
        Glyph(gx - 4, benchY + 1, '║', Bench, light);
        Glyph(gx + Gnaj[0].Length + 3, benchY + 1, '║', Bench, light);
        Glyph(gx - 2, benchY - 1, 'ő', Flask, light);
        Glyph(gx + Gnaj[0].Length + 1, benchY - 1, 'ò', FlaskB, light);

        // The portal device: a heavy ring on its stand, coils at its sides, the void
        // inside swirling, brighter as the device powers up.
        double power = f < PowerAt ? 0.25 : 0.25 + 0.75 * SplashCanvas.Smooth((f - PowerAt) / 20.0);
        for (int y = py - ringR; y <= py + ringR; y++)
            for (int x = px - ringR * 2; x <= px + ringR * 2; x++)
            {
                double dx = (x - px) / 2.0, dy = y - py;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > ringR + 0.5) continue;
                if (d > ringR - 0.8)
                {
                    Bg(x, y, (x + y) % 3 == 0 ? RingLt : Ring, light);
                    continue;
                }
                double swirl = Math.Sin(Math.Atan2(dy, dx) * 3 + d * 0.9 - f * 0.35);
                Bg(x, y, SplashCanvas.Mix(Energy, EnergyHot, (swirl + 1) / 2 * power), light * (0.3 + 0.7 * power));
            }
        for (int y = py + ringR + 1; y < floor; y++) { Bg(px - 2, y, Ring, light); Bg(px + 2, y, Ring, light); }
        foreach (int side in new[] { -1, 1 })
        {
            int coilX = px + side * (ringR * 2 + 2);
            for (int y = py - 2; y <= py + 2; y++) Glyph(coilX, y, '╪', Coil, light);
            if (f >= PowerAt && (f + side) % 3 == 0)
                c.Line(coilX, py + ((f / 3) % 5) - 2, px + side * (ringR * 2 - 1), py + ((f / 2) % 3) - 1,
                    side < 0 ? '╲' : '╱', SplashCanvas.Fg(Spark, light));
        }

        if (f >= StormAt)
        {
            // Eldritch storm: out of his pointing finger, swelling toward us.
            double p = SplashCanvas.EaseIn((f - StormAt) / (double)(StormFull - StormAt));
            StormCells(c, f, gx + 4, gy + 1, 2 + p * c.MaxRadius() * 1.5, 1);
            return;
        }

        if (f >= ShriekAt && f < PowerAt + 10)
        {
            int left = Math.Max(1, gx - 6);
            c.SpeechBubble(left, top + 2, Math.Max(16, Math.Min(30, px - ringR * 2 - left - 4)), Shriek,
                (f - ShriekAt) * 3, SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Quote, light),
                SplashCanvas.Bg(BubbleFill, light), (gx + 4 + sway, gy - 1));
        }
    }

    // The storm's cells within radius r of (sx, sy): crackling red and violet arcs
    // wheeling out from the middle, at `strength` of their brightness.
    private static void StormCells(SplashCanvas c, int f, int sx, int sy, double r, double strength)
    {
        for (int y = SplashCanvas.TitleRows; y < c.Rows; y++)
            for (int x = 0; x < c.Cols; x++)
            {
                double dx = x - sx, dy = (y - sy) * 2.0;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                double swirl = Math.Sin(Math.Atan2(dy, dx) * 5 - d * 0.35 + f * 0.6);
                double n = SplashCanvas.Noise(x + f, y);
                if (swirl > 0.7 && n > 0.4)
                    c.Put(x, y, n > 0.8 ? '✦' : n > 0.6 ? '*' : '·', SplashCanvas.Fg(StormWhite, strength));
                else
                    c.PutBg(x, y, SplashCanvas.Bg(swirl > 0 ? StormRed : StormViolet, strength * (0.35 + 0.4 * Math.Abs(swirl))));
            }
    }
}
