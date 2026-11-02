using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A diamond mine tunnel: heavy timber frames down a low passage of brownish-red rock,
// diamonds clustered thick in the walls and glinting. Out of the black at its far end
// the Devil Fiend Malivek comes stalking up the tunnel, a wisp of smoke trailing from
// its mouth; it growls (in a bubble), throws up its arms, and hellfire (its own
// attack) roars up from the floor to fill the lens, then the fire dies and the smoke
// thins away.
//
// The tunnel is the Diamond Vein the fiend roams (map 15).
public sealed class MalivekScene : SplashScene
{
    public override int LoopFrames => 150;

    private const int FadeInEnd  = 10;
    private const int StalkEnd   = 44;    // it has come up the tunnel to us
    private const int GrowlAt    = 48;
    private const int FireStart  = 96;
    private const int FireFull   = 110;
    private const int SmokeEnd   = 144;
    // 144..149: clear.

    // Its greeting is a growl with no words in it, so the bubble holds the growl.
    private const string Growl = "Grrrrrr...";

    private static readonly (byte, byte, byte) RockC = (96, 50, 36), RockDk = (66, 34, 24), Dark = (12, 8, 8),
        Beam = (88, 58, 30), BeamDk = (58, 38, 20), Diamond = (200, 240, 255), DiamondHot = (255, 255, 255),
        Hide = (150, 24, 20), HideDk = (92, 12, 12), Horn = (220, 210, 180), Eye = (255, 230, 60),
        SmokeC = (120, 116, 112), FireOuter = (200, 50, 10), FireMid = (245, 130, 20), FireCore = (255, 230, 120),
        Snarl = (230, 120, 60), BubbleFill = (16, 8, 6), BubbleRim = (140, 90, 70);

    // Up close: a hulking, horned fiend, all shoulders and arms, claws at its fists.
    private static readonly string[] Near =
    {
        "  ▄▀                     ▀▄  ",
        "  █▄      ▄▄▄███▄▄▄      ▄█  ",
        "   ▀▀▄▄▄▄██▀●███●▀██▄▄▄▄▀▀   ",
        "         ███▄▀▀▀▄███         ",
        " ▄▄██▄▄▄▄████▀▀▀████▄▄▄▄██▄▄ ",
        "▐███████████████████████████▌",
        "████▀ ▐████▀▀███▀▀████▌ ▀████",
        "███▌  ▐███▀▀█████▀▀███▌  ▐███",
        "▀██▄  ▐█████▄▄▀▄▄█████▌  ▄██▀",
        " ▀▀▀▀  ▐█████████████▌  ▀▀▀▀ ",
        "        ▐████▌ ▐████▌        ",
        "       ▄████▀   ▀████▄       ",
    };
    private static readonly string[] NearPaint =
    {
        "  hh                     hh  ",
        "  hh      HHHHHHHHH      hh  ",
        "   hhhHHHHHHeHHHeHHHHHHhhh   ",
        "         HHHHdddHHHH         ",
        " HHHHHHHHHHHHdddHHHHHHHHHHHH ",
        "HHHHHHHHHHHHHHHHHHHHHHHHHHHHH",
        "HHHHH HHHHHddHHHddHHHHH HHHHH",
        "HHHH  HHHHddHHHHHddHHHH  HHHH",
        "HHHH  HHHHHHHHdHHHHHHHH  HHHH",
        " HHHH  HHHHHHHHHHHHHHH  HHHH ",
        "        HHHHHH HHHHHH        ",
        "       hhhhhh   hhhhhh       ",
    };
    // Calling up the hellfire: both arms flung up, fists beside its horns.
    private static readonly string[] Raised =
    {
        "▄█▄  ▄▀               ▀▄  ▄█▄",
        "███  █▄   ▄▄▄███▄▄▄   ▄█  ███",
        "▐█▌ ▀▀▄▄▄██▀●███●▀██▄▄▄▀▀ ▐█▌",
        " ██▌     ███▄▀▀▀▄███     ▐██ ",
        " ▐██▄▄▄▄▄████▀▀▀████▄▄▄▄▄██▌ ",
        "▐███████████████████████████▌",
        " ▐█████████▀▀███▀▀█████████▌ ",
        "      ▐███▀▀█████▀▀███▌      ",
        "      ▐█████▄▄▀▄▄█████▌      ",
        "       ▐█████████████▌       ",
        "        ▐████▌ ▐████▌        ",
        "       ▄████▀   ▀████▄       ",
    };
    private static readonly string[] RaisedPaint =
    {
        "HHH  hh               hh  HHH",
        "HHH  hh   HHHHHHHHH   hh  HHH",
        "HHH hhhhHHHHeHHHeHHHHhhhh HHH",
        " HHH     HHHHdddHHHH     HHH ",
        " HHHHHHHHHHHHdddHHHHHHHHHHHH ",
        "HHHHHHHHHHHHHHHHHHHHHHHHHHHHH",
        " HHHHHHHHHHddHHHddHHHHHHHHHH ",
        "      HHHHddHHHHHddHHHH      ",
        "      HHHHHHHHdHHHHHHHH      ",
        "       HHHHHHHHHHHHHHH       ",
        "        HHHHHH HHHHHH        ",
        "       hhhhhh   hhhhhh       ",
    };
    private static readonly string[] Far =
    {
        "▀▄▄▄▀",
        " ●█● ",
        "▐███▌",
        " ▌ ▐ ",
    };
    private static readonly string[] FarPaint =
    {
        "hHHHh",
        " eHe ",
        "dHHHd",
        " d d ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= SmokeEnd) return;
        int top = SplashCanvas.TitleRows;
        int floor = c.Rows;
        int cx = c.Cols / 2;

        if (f >= FireFull)
        {
            // The fire has burned out; smoke hangs over the lens and thins away.
            double p = (f - FireFull) / (double)(SmokeEnd - FireFull);
            for (int y = top; y < c.Rows; y++)
                for (int x = 0; x < c.Cols; x++)
                {
                    double n = SplashCanvas.Noise((x + f / 2) / 2, y + f / 4);
                    if (n < p * 1.05) continue;
                    if (n > 0.92 - p * 0.5) c.Put(x, y, '░', SplashCanvas.Fg(SmokeC, 1 - p));
                    else c.PutBg(x, y, SplashCanvas.Bg(SmokeC, (1 - p) * 0.5));
                }
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);

        // The tunnel: rock walls closing toward a black mouth at the far end.
        int mouthHalf = Math.Max(4, c.Cols / 12);
        int mouthTop = top + (int)Math.Round((floor - top) * 0.3), mouthBot = top + (int)Math.Round((floor - top) * 0.65);
        for (int y = top; y < floor; y++)
            for (int x = 0; x < c.Cols; x++)
            {
                bool inMouth = y >= mouthTop && y <= mouthBot && Math.Abs(x - cx) <= mouthHalf;
                c.PutBg(x, y, inMouth ? SplashCanvas.Bg(Dark, light)
                    : SplashCanvas.Bg(SplashCanvas.Noise(x / 2, y) > 0.62 ? RockDk : RockC, light));
            }
        // Timber frames, nearer ones wider apart, the beam across the top.
        foreach (double depth in new[] { 0.2, 0.55, 1.0 })
        {
            int half = (int)Math.Round(SplashCanvas.Lerp(mouthHalf + 2, c.Cols / 2 - 2, depth));
            int beamY = (int)Math.Round(SplashCanvas.Lerp(mouthTop - 1, top, depth));
            int footY = (int)Math.Round(SplashCanvas.Lerp(mouthBot + 1, floor - 1, depth));
            for (int x = cx - half; x <= cx + half; x++) c.PutBg(x, beamY, SplashCanvas.Bg(Beam, light));
            for (int y = beamY; y <= footY; y++)
            {
                c.PutBg(cx - half, y, SplashCanvas.Bg(BeamDk, light));
                c.PutBg(cx + half, y, SplashCanvas.Bg(BeamDk, light));
            }
        }
        // Diamonds in the walls, glinting in turn.
        for (int i = 0; i < c.Cols / 2; i++)
        {
            int x = (int)(SplashCanvas.Noise(i, 3) * c.Cols), y = top + (int)(SplashCanvas.Noise(i, 7) * (floor - top));
            if (y >= mouthTop && y <= mouthBot && Math.Abs(x - cx) <= mouthHalf) continue;
            bool glint = SplashCanvas.Noise(i, f / 3) > 0.85;
            c.Put(x, y, glint ? '✦' : '◊', SplashCanvas.Fg(glint ? DiamondHot : Diamond, light));
        }

        // Malivek: a shape in the mouth of the tunnel, then stalking up it to us.
        CellAttributes Paints(char k) => k switch
        {
            'h' => SplashCanvas.Fg(Horn, light), 'e' => SplashCanvas.Fg(Eye, light),
            'd' => SplashCanvas.Fg(HideDk, light), _ => SplashCanvas.Fg(Hide, light),
        };
        double stalk = SplashCanvas.Smooth((f - FadeInEnd) / (double)(StalkEnd - FadeInEnd));
        int headY, mouthDy, mouthDx;
        if (stalk < 0.5)
        {
            headY = mouthBot - Far.Length + 1;
            (mouthDy, mouthDx) = (1, 3);
            c.Sprite(cx - 2, headY, Far, FarPaint, Paints);
        }
        else
        {
            int bottom = (int)Math.Round(SplashCanvas.Lerp(mouthBot + 2, floor - 1, (stalk - 0.5) * 2));
            headY = bottom - Near.Length + 1;
            (mouthDy, mouthDx) = (3, 3);
            // It throws its arms up as the hellfire comes.
            bool raised = f >= FireStart - 4;
            c.Sprite(cx - Near[0].Length / 2, headY, raised ? Raised : Near, raised ? RaisedPaint : NearPaint, Paints);
        }
        // The wisp of smoke trailing from the corner of its mouth, up and away.
        for (int k = 0; k < 4; k++)
            c.Put(cx + mouthDx + k + (int)Math.Round(Math.Sin((f + k * 3) * 0.4)), headY + mouthDy - k,
                k % 2 == 0 ? '~' : '∙', SplashCanvas.Fg(SmokeC, light * (1 - k * 0.2)));

        if (f >= GrowlAt && f < FireStart)
        {
            int left = Math.Min(cx + Near[0].Length / 2 + 2, Math.Max(1, c.Cols - Growl.Length - 5));
            c.SpeechBubble(left, headY + 1, Growl.Length + 4, Growl, (f - GrowlAt) * 2,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Snarl, light), SplashCanvas.Bg(BubbleFill, light),
                (cx + Near[0].Length / 2 - 1, headY + 2));
        }

        // Hellfire: a wall of flame climbing from the floor to the top of the lens.
        if (f >= FireStart)
        {
            double p = SplashCanvas.EaseIn((f - FireStart) / (double)(FireFull - FireStart));
            for (int x = 0; x < c.Cols; x++)
            {
                int flameTop = c.Rows - (int)Math.Round(p * (c.Rows - top) * (1.0 + 0.4 * SplashCanvas.Noise(x, f)));
                for (int y = Math.Max(top, flameTop); y < c.Rows; y++)
                {
                    double heat = (y - flameTop) / (double)Math.Max(1, c.Rows - flameTop);
                    c.PutBg(x, y, SplashCanvas.Bg(heat > 0.6 ? FireCore : heat > 0.25 ? FireMid : FireOuter));
                }
                if (flameTop > top) c.Put(x, flameTop - 1, SplashCanvas.Noise(x, f) > 0.5 ? '▲' : '^', SplashCanvas.Fg(FireMid));
            }
        }
    }
}
