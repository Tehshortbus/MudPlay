using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A small mountain sits in the distance. A mudslide lets go at the peak, runs down
// the slope, drops onto the ground between us and the mountain — bouncing up in a
// splash — then that surge rears up and hits us in the lens, before sliding off.
public sealed class MudslideScene : SplashScene
{
    public override int LoopFrames => 128;

    private const int RevealEnd = 8;    // mountain + ground draw in
    private const int DownEnd   = 30;   // mud runs down the slope to the base
    private const int BounceEnd = 46;   // mud drops to the ground and bounces
    private const int FloodEnd  = 64;   // the surge rears up and floods the lens
    private const int SlideEnd  = 122;  // sheet runs off
    // 122..127: clear.

    private static readonly CellAttributes Rock   = SplashCanvas.Rgb(78, 74, 82);
    private static readonly CellAttributes RockLt   = SplashCanvas.Rgb(110, 104, 112);
    private static readonly CellAttributes Snow     = SplashCanvas.Rgb(220, 224, 232);
    private static readonly CellAttributes Ground   = SplashCanvas.Rgb(90, 74, 52);

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        int top = SplashCanvas.TitleRows;
        int peakY = top + 1;
        int span = c.Rows - top;
        int baseY   = top + Math.Max(5, (int)Math.Round(span * 0.34));   // foot of the mountain
        int groundY = top + Math.Max(baseY + 3, (int)Math.Round(span * 0.72)); // foreground ground
        int slope(int y) => (int)Math.Round((y - peakY) * 1.3);

        if (f > FloodEnd && f <= SlideEnd)
        {
            c.SlideMudOff((f - FloodEnd) / (double)(SlideEnd - FloodEnd), cx, cy);
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Mountain + a foreground ground line, revealed over the first frames.
        double reveal = Math.Min(1.0, f / (double)RevealEnd);
        int revealY = peakY + (int)Math.Round(reveal * (baseY - peakY));
        for (int y = peakY; y <= Math.Min(baseY, revealY); y++)
        {
            int half = slope(y);
            for (int x = cx - half; x <= cx + half; x++)
            {
                bool edge = x <= cx - half || x >= cx + half;
                c.Put(x, y, y <= peakY + 1 ? '▀' : edge ? '◣' : '█',
                    y <= peakY + 1 ? Snow : edge ? RockLt : Rock);
            }
        }
        c.Str(0, groundY, new string('▄', c.Cols), Ground);

        // Stage 1: mud runs DOWN the slope to the base.
        if (f > RevealEnd)
        {
            double p1 = Math.Clamp((f - RevealEnd) / (double)(DownEnd - RevealEnd), 0, 1);
            int mudBot = peakY + (int)Math.Round(p1 * (baseY - peakY));
            for (int y = peakY; y <= mudBot; y++)
            {
                int w = 1 + (y - peakY) / 3;
                for (int x = cx - w; x <= cx + w; x++)
                    c.PutMud(x, y, 0.8, x, y);
            }
        }

        // Stage 2: the mud drops off the base onto the ground and bounces.
        if (f > DownEnd && f <= BounceEnd)
        {
            double p2 = (f - DownEnd) / (double)(BounceEnd - DownEnd);
            int fy = (int)Math.Round(SplashCanvas.Lerp(baseY, groundY, SplashCanvas.EaseIn(p2)));
            c.Blob(cx, fy, 3);
            if (p2 > 0.6)   // bounce splash off the ground
                for (int i = 0; i < 9; i++)
                {
                    double a = SplashCanvas.Lerp(0.1, 0.9, i / 8.0) * Math.PI;
                    double d = (p2 - 0.6) * 30;
                    c.Blob(cx + (int)Math.Round(Math.Cos(a) * d),
                           groundY - (int)Math.Round(Math.Sin(a) * d * 0.6), 1);
                }
        }

        // Stage 3: the surge rears up off the ground and floods the lens — its
        // centre rides from the ground up to mid-lens as it swells to full.
        if (f > BounceEnd)
        {
            double p3 = (f - BounceEnd) / (double)(FloodEnd - BounceEnd);
            double e = SplashCanvas.EaseOut(p3);
            int scy = (int)Math.Round(SplashCanvas.Lerp(groundY, cy, e));
            c.CoverMud(cx, scy, c.MaxRadius() * e * 1.15);
            c.Flecks(cx, scy, c.MaxRadius() * e, 18);
        }
    }
}
