using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The lens cakes over with mud, then a windshield wiper cleans it in two sweeps:
// the first pass wipes every OTHER row clean, the second pass wipes the rows it
// left behind — so it clears half the lines, then the rest, ending on clean glass.
public sealed class WiperScene : SplashScene
{
    public override int LoopFrames => 92;

    private const int CoverEnd = 12;    // mud floods in
    private const int Pass1End = 46;    // sweep clears the even rows
    private const int Pass2End = 80;    // sweep back clears the odd rows
    // 80..91: clean.

    private static readonly CellAttributes Blade = SplashCanvas.Rgb(210, 210, 220);
    private static readonly CellAttributes Arm   = SplashCanvas.Rgb(70, 70, 78);

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        double maxR = c.MaxRadius();

        if (f >= Pass2End) return;   // clean

        if (f < CoverEnd)
        {
            c.CoverMud(cx, cy, maxR * SplashCanvas.EaseOut(f / (double)CoverEnd));
            return;
        }

        bool pass1 = f < Pass1End;
        double t = pass1
            ? (f - CoverEnd) / (double)(Pass1End - CoverEnd)
            : (f - Pass1End) / (double)(Pass2End - Pass1End);
        int wx = pass1
            ? (int)Math.Round(t * (c.Cols - 1))          // blade L→R
            : (int)Math.Round((1 - t) * (c.Cols - 1));   // blade R→L

        for (int y = SplashCanvas.TitleRows; y < c.Rows; y++)
        {
            bool evenRow = (y - SplashCanvas.TitleRows) % 2 == 0;
            for (int x = 0; x < c.Cols; x++)
            {
                // Pass 1 clears even rows in the blade's wake (x < wx). Pass 2:
                // even rows are already clean; odd rows clear in this wake (x > wx).
                bool cleared = pass1
                    ? evenRow && x < wx
                    : evenRow || x > wx;
                if (cleared) continue;
                double it = SplashCanvas.SplatAt(x, y, cx, cy, maxR);
                if (it > 0) c.PutMud(x, y, it, x, y);
            }
        }

        // The blade sweeping across, on a little arm.
        for (int y = SplashCanvas.TitleRows; y < c.Rows; y++) c.Put(wx, y, '█', Blade);
        c.Put(wx, c.Rows - 1, '╨', Arm);
    }
}
