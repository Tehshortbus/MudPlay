using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Fat mudballs plop down from the top and splat where they land, accumulating
// until they've caked over every last bit of the lens — then the whole sheet
// slides off. Coverage is guaranteed: the landing spots tile the view (jittered
// grid), so the drops alone fill it, no flood underneath.
public sealed class MudRainScene : SplashScene
{
    public override int LoopFrames => 128;

    private const int CoverEnd  = 74;   // every spot has landed → lens fully caked
    private const int SlideEnd  = 122;  // sheet slides off
    // 122..127: clear.
    private const int FallFrames = 8;   // a drop is airborne this long before landing
    private const int SpaceX = 3;       // grid spacing — tight enough that blobs overlap
    private const int SpaceY = 2;

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int cx = c.Cols / 2, cy = (c.Rows + SplashCanvas.TitleRows) / 2;
        int top = SplashCanvas.TitleRows;

        if (f > CoverEnd && f <= SlideEnd)
        {
            // Slide the accumulated drop-fill that's actually on screen — not a
            // reconstructed radial splat.
            c.SlideSheetOff((f - CoverEnd) / (double)(SlideEnd - CoverEnd));
            return;
        }
        if (f > SlideEnd) return;   // clear

        int gx = 0;
        for (int lx = 1; lx < c.Cols - 1; lx += SpaceX, gx++)
        {
            int gy = 0;
            for (int ly = top + 1; ly < c.Rows; ly += SpaceY, gy++)
            {
                // Jittered landing spot + a scattered land time in [0, CoverEnd] so
                // the fill order looks random, not row-by-row.
                int px = lx + (int)Math.Round((SplashCanvas.Noise(gx, gy) - 0.5) * SpaceX);
                int py = ly + (int)Math.Round((SplashCanvas.Noise(gy, gx) - 0.5) * SpaceY);
                int landAt = (int)(SplashCanvas.Noise(gx * 7 + 1, gy * 13 + 3) * (CoverEnd - FallFrames));

                if (f >= landAt)
                {
                    c.MudPatch(px, py, 2);   // landed — canonical sheet, tiles into the slide
                }
                else if (f >= landAt - FallFrames)
                {
                    double a = (f - (landAt - FallFrames)) / (double)FallFrames;
                    int y = top + (int)Math.Round(a * (py - top));
                    c.Put(px, y, '●', SplashCanvas.BrownLite);
                    c.Put(px, y - 1, '│', SplashCanvas.Brown);
                }
            }
        }
    }
}
