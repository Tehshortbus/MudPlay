using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A little pig trots in, reaches the wallow, and belly-flops into the mud —
// flinging a sheet of it straight up at the lens, which then oozes off.
public sealed class PigWallowScene : SplashScene
{
    public override int LoopFrames => 116;

    private const int FlopFrame = 30;   // pig hits the wallow
    private const int CoverEnd  = 44;   // mud has flown up and flooded the view
    private const int SlideEnd  = 110;  // oozed off
    // 110..115: clear.

    private static readonly CellAttributes Pig    = SplashCanvas.Rgb(232, 150, 170);
    private static readonly CellAttributes PigDk   = SplashCanvas.Rgb(190, 110, 132);
    private static readonly CellAttributes Eye     = SplashCanvas.Rgb(30, 20, 24);
    private static readonly CellAttributes Wallow  = SplashCanvas.Rgb(90, 70, 46);

    private static readonly string[] PigBody =
    {
        " ▄▟███▙▄",
        "▐███████◗",   // ◗ = snout
        " ▔█▔ ▔█▔ ",   // trotters
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        int ground = c.Rows - 3;

        if (f > CoverEnd && f <= SlideEnd)
        {
            c.SlideMudOff((f - CoverEnd) / (double)(SlideEnd - CoverEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;   // clear

        // The wallow puddle sits at the bottom centre.
        c.Str(cx - 6, ground + 2, "▁▂▃▄▄▄▄▃▂▁", Wallow);

        if (f < FlopFrame)
        {
            // Trot in from the left; a little vertical bob sells the gait.
            double t = SplashCanvas.Smooth(f / (double)FlopFrame);
            int px = (int)Math.Round(SplashCanvas.Lerp(-9, cx - 4, t));
            int bob = (f % 4 < 2) ? 0 : 1;
            for (int r = 0; r < PigBody.Length; r++)
            {
                string row = PigBody[r];
                for (int i = 0; i < row.Length; i++)
                {
                    char ch = row[i];
                    if (ch == ' ') continue;
                    CellAttributes a = ch == '◗' ? PigDk : r == PigBody.Length - 1 ? PigDk : Pig;
                    c.Put(px + i, ground - 2 + r + bob, ch, a);
                }
            }
            c.Put(px + 5, ground - 1 + bob, '·', Eye);   // eye
            return;
        }

        // FLOP — mud erupts upward off the wallow and floods the lens.
        double p = (f - FlopFrame) / (double)(CoverEnd - FlopFrame);
        for (int i = 0; i < 12; i++)
        {
            double a = SplashCanvas.Lerp(0.12, 0.88, i / 11.0) * Math.PI;
            int gx = cx + (int)Math.Round(Math.Cos(a) * (5 + p * 16));
            int gy = ground - (int)Math.Round(Math.Sin(a) * (6 + p * 14) * 0.8);
            c.Blob(gx, gy, 1 + (int)Math.Round(p * 2));
        }
        c.CoverMud(cx, cy, c.MaxRadius() * SplashCanvas.EaseOut(p) * 0.9);
        c.Flecks(cx, cy, c.MaxRadius() * SplashCanvas.EaseOut(p), 22);
    }
}
