using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Side view: a monster truck barrels across the frame, hits a mud puddle, and
// throws a wall of mud up onto the lens — which then slides off clean. Truck is
// off-screen at both ends, so the scene starts and ends on a clear lens.
public sealed class MonsterTruckScene : SplashScene
{
    public override int LoopFrames => 120;

    private const int HitFrame  = 40;   // front wheel meets the puddle
    private const int CoverEnd  = 52;   // mud wall has flooded the lens
    private const int SlideEnd  = 114;  // mud has run off
    // 114..119: clear.

    private static readonly CellAttributes Body   = SplashCanvas.Rgb(180, 40, 40);
    private static readonly CellAttributes BodyLt = SplashCanvas.Rgb(220, 80, 70);
    private static readonly CellAttributes Glass  = SplashCanvas.Rgb(120, 190, 220);
    private static readonly CellAttributes Tyre   = SplashCanvas.Rgb(40, 40, 44);
    private static readonly CellAttributes Rim    = SplashCanvas.Rgb(150, 150, 160);
    private static readonly CellAttributes Puddle = SplashCanvas.Rgb(90, 70, 46);

    // Side sprite; anchored so the last two rows are the big wheels sitting on the
    // ground row. DrawTruck colours each cell by its glyph: '▒' is glass, '█' in
    // the wheels is tyre, the rest body/rim.
    private static readonly string[] Cab =
    {
        "   ▄▄▄▄▄▄▄▄▄  ",
        "  ██████▛▒▒▒▖ ",   // windshield up front (the truck drives → right)
        " ██████▐▒▒▒▒█ ",
        "▐████████████▌",
    };
    private static readonly string[] Wheels =
    {
        " ╱██╲    ╱██╲ ",
        " ╲██╱    ╲██╱ ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int ground = c.Rows - 2;
        int puddleX = c.Cols / 2;
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;

        // Mud has flooded and is running off — truck already gone.
        if (f > CoverEnd && f <= SlideEnd)
        {
            c.SlideMudOff((f - CoverEnd) / (double)(SlideEnd - CoverEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Truck position: enters left, front wheel reaches the puddle at HitFrame,
        // keeps rolling and exits right.
        int spriteW = Cab[0].Length;
        double drive = f / (double)HitFrame;                 // 0..1 up to the hit, >1 after
        int truckX = (int)Math.Round(SplashCanvas.Lerp(-spriteW - 2, puddleX - spriteW + 3, Math.Min(drive, 1)))
                   + (f > HitFrame ? (f - HitFrame) * 3 : 0);

        DrawPuddle(c, puddleX, ground, splashing: f >= HitFrame);
        DrawTruck(c, truckX, ground - 5);

        // Impact: a mud mass launches up off the wheel and grows straight into the
        // camera (its centre rides from the puddle up to mid-lens as it swells to
        // full), so it reads as the sheet flying AT us — not a flood blooming from
        // the middle. Irregular back-spray flings up and behind the wheel too.
        if (f >= HitFrame)
        {
            double p = (f - HitFrame) / (double)(CoverEnd - HitFrame);
            double e = SplashCanvas.EaseOut(p);
            int scx = (int)Math.Round(SplashCanvas.Lerp(puddleX, cx, e));
            int scy = (int)Math.Round(SplashCanvas.Lerp(ground, cy, e));
            c.CoverMud(scx, scy, c.MaxRadius() * e * 1.15);

            for (int i = 0; i < 12; i++)   // ragged back-spray (up and to the left)
            {
                double a = SplashCanvas.Lerp(0.55, 1.05, SplashCanvas.Noise(i, 5)) * Math.PI;
                double dist = (5 + p * 16) * (0.6 + SplashCanvas.Noise(i, 9));
                int dxp = puddleX + (int)Math.Round(Math.Cos(a) * dist);
                int dyp = ground - (int)Math.Round(Math.Sin(a) * dist * 0.7);
                c.Blob(dxp, dyp, 1 + (int)Math.Round(p * 2));
            }
            c.Flecks(scx, scy, c.MaxRadius() * e, 18);
        }
    }

    private static void DrawTruck(SplashCanvas c, int x, int y)
    {
        for (int r = 0; r < Cab.Length; r++)
        {
            string row = Cab[r];
            for (int i = 0; i < row.Length; i++)
            {
                char ch = row[i];
                if (ch == ' ') continue;
                CellAttributes a = ch == '▒' ? Glass : r == 0 ? BodyLt : Body;
                c.Put(x + i, y + r, ch, a);
            }
        }
        for (int r = 0; r < Wheels.Length; r++)
            for (int i = 0; i < Wheels[r].Length; i++)
            {
                char ch = Wheels[r][i];
                if (ch == ' ') continue;
                c.Put(x + i, y + Cab.Length + r, ch, ch == '█' ? Tyre : Rim);
            }
    }

    private static void DrawPuddle(SplashCanvas c, int px, int ground, bool splashing)
    {
        c.Str(px - 6, ground + 1, "▁▂▃▄▄▄▃▂▁", Puddle);
        if (!splashing) c.Str(px - 4, ground, "░▒▒░ ░▒░", Puddle);
    }
}
