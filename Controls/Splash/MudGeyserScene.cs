using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A little mound of mud swells at the bottom, then a geyser rockets up from it to
// the top of the view — where the mud fans out and pours back DOWN over the lens,
// caking it from the top, before the whole sheet slides off.
public sealed class MudGeyserScene : SplashScene
{
    public override int LoopFrames => 120;

    private const int MoundEnd  = 12;   // a mound swells at the bottom
    private const int RocketEnd = 30;   // column rockets to the top
    private const int FillEnd   = 56;   // mud pours down from the top, caking the lens
    private const int SlideEnd  = 114;  // sheet slides off
    // 114..119: clear.

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0) return;   // the clear lens the last scene handed off on
        int cx = c.Cols / 2;
        int ground = c.Rows - 2;
        int topY = SplashCanvas.TitleRows;

        if (f > FillEnd && f <= SlideEnd)
        {
            c.SlideSheetOff((f - FillEnd) / (double)(SlideEnd - FillEnd));
            return;
        }
        if (f > SlideEnd) return;   // clear

        // Mound + column stay standing on screen; the fill pouring down from the top
        // buries them as its edge descends, rather than them vanishing up front.
        DrawGeyser(c, f, cx, ground, topY);
        if (f > RocketEnd)
            c.FillFromTop((f - RocketEnd) / (double)(FillEnd - RocketEnd));
    }

    private static void DrawGeyser(SplashCanvas c, int f, int cx, int ground, int topY)
    {
        // A little mound swells at the bottom centre, then holds.
        double swell = SplashCanvas.Smooth(Math.Min(1.0, f / (double)MoundEnd));
        int mh = 1 + (int)Math.Round(swell * 2);
        for (int i = 0; i <= mh; i++)
        {
            int w = mh - i + 1;
            c.Str(cx - w, ground - i, new string('▄', w * 2 + 1),
                SplashCanvas.MudAttr(0.6, cx, ground - i));
        }
        for (int b = 0; b < 4 && f < MoundEnd; b++)   // bubbling
            c.Put(cx - 3 + b * 2, ground - 1 - ((f + b) % 3), 'o', SplashCanvas.BrownLite);

        if (f < MoundEnd) return;

        // Column rockets to the top, then holds there (climb clamped) so it stays
        // standing under the fill until the descending mud buries it. It erupts from
        // the PEAK of the mound (moundTop), narrow at that origin and fanning wider as
        // it climbs — so the jet reads as spouting out the top of the mound rather
        // than rooted at the ground floor.
        int moundTop = ground - mh;
        double climb = SplashCanvas.EaseOut(Math.Min(1.0, (f - MoundEnd) / (double)(RocketEnd - MoundEnd)));
        int colTop = (int)Math.Round(SplashCanvas.Lerp(moundTop, topY, climb));
        int wob = f % 2;
        for (int y = colTop; y <= moundTop; y++)
        {
            int w = 1 + (moundTop - y) / 7;
            c.Str(cx - w + wob, y, new string('█', w * 2 + 1), SplashCanvas.MudAttr(0.7, cx, y));
        }
        if (climb > 0.7 && f <= RocketEnd)   // crown of gobs while still rising
            for (int i = 0; i < 7; i++)
            {
                double a = SplashCanvas.Lerp(0.15, 0.85, i / 6.0) * Math.PI;
                double d = (climb - 0.7) * 26;
                c.Blob(cx + (int)Math.Round(Math.Cos(a) * d),
                       colTop - (int)Math.Round(Math.Sin(a) * d * 0.5), 1);
            }
    }
}
