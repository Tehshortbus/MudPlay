using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Gnaj the Creator in his laboratory, making monsters. Specimen vats bubble along the
// wall with his half-made creatures floating in them; beside him stands his
// interdimensional portal device, a great ring wound with coils. He throws up his
// arms and rants, the device crackles up to full power, the things in the vats open
// their eyes, and the portal drags the whole room into itself (his sphere of
// implosion) and closes.
//
// His words, in a bubble, are from his greeting.
public sealed class GnajScene : SplashScene
{
    public override int LoopFrames => 160;

    private const int FadeInEnd  = 12;
    private const int ShriekAt   = 30;
    private const int PowerAt    = 84;    // the device starts to crackle
    private const int WakeAt     = 104;   // the specimens open their eyes
    private const int ImplodeAt  = 126;
    private const int ImplodeEnd = 152;
    // 152..159: clear.

    private const string Shriek = "Gnaj is the end of you, behold the furious wrath of the beginner of all things!";

    private static readonly (byte, byte, byte) WallC = (30, 28, 34), WallDk = (20, 18, 24), FloorC = (40, 36, 40),
        Glass = (150, 190, 200), Fluid = (40, 110, 70), FluidLt = (80, 170, 110), BubbleC = (170, 230, 190),
        Specimen = (40, 60, 44), SpecEye = (255, 60, 60), Bench = (90, 64, 40), Flask = (200, 120, 230),
        FlaskB = (90, 180, 240), Ring = (120, 124, 136), RingLt = (180, 184, 196), Coil = (190, 130, 60),
        Spark = (210, 230, 255), Energy = (150, 80, 220), EnergyHot = (120, 230, 200), Robe = (88, 70, 50),
        RobeDk = (60, 46, 32), Skin = (206, 178, 150), Hair = (226, 226, 232), Eye = (255, 250, 120),
        Quote = (60, 200, 90), BubbleFill = (10, 10, 14), BubbleRim = (130, 130, 140);

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
        if (f <= 0 || f >= ImplodeEnd) return;
        int top = SplashCanvas.TitleRows;
        int floor = c.Rows - 2;
        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);

        // The portal device stands at the right; everything falls into it at the end.
        int ringR = Math.Max(3, Math.Min((floor - top) / 2 - 1, c.Cols / 9));
        int px = c.Cols - ringR * 2 - 4, py = top + (floor - top) / 2;
        double keep = double.MaxValue;
        if (f >= ImplodeAt)
            keep = (1 - SplashCanvas.Smooth((f - ImplodeAt) / (double)(ImplodeEnd - 2 - ImplodeAt))) * c.MaxRadius() * 1.4;
        bool Kept(int x, int y)
        {
            double dx = x - px, dy = (y - py) * 2.0;
            return Math.Sqrt(dx * dx + dy * dy) <= keep;
        }
        void Bg(int x, int y, (byte, byte, byte) col, double l) { if (Kept(x, y)) c.PutBg(x, y, SplashCanvas.Bg(col, l)); }
        void Glyph(int x, int y, char ch, (byte, byte, byte) col, double l) { if (Kept(x, y)) c.Put(x, y, ch, SplashCanvas.Fg(col, l)); }

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

        // The workbench, and Gnaj behind it with his arms flung up.
        int benchY = floor - 2;
        int gx = Math.Max(20, Math.Min(26, px - ringR * 2 - 16)), gy = benchY - Gnaj.Length + 2;
        int sway = f >= ShriekAt ? (f / 6) % 2 : 0;
        for (int r = 0; r < Gnaj.Length; r++)
            for (int i = 0; i < Gnaj[r].Length; i++)
            {
                char ch = Gnaj[r][i];
                if (ch == ' ' || gy + r >= benchY) continue;
                (byte, byte, byte) col = GnajPaint[r][i] switch
                {
                    's' => Skin, 'h' => Hair, 'e' => Eye, 'R' => Robe, _ => RobeDk,
                };
                Glyph(gx + sway + i, gy + r, ch, col, light);
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

        if (f >= ImplodeAt)
        {
            // The rim of the pull, crackling as it closes on the portal.
            for (int k = 0; k < 48; k++)
            {
                double a = k * (Math.PI * 2 / 48);
                c.Put(px + (int)Math.Round(Math.Cos(a) * keep), py + (int)Math.Round(Math.Sin(a) * keep * 0.5),
                    (k + f) % 2 == 0 ? '✦' : '·', SplashCanvas.Fg(EnergyHot));
            }
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
}
