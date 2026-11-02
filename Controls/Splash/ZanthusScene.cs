using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// Night on Crystal Lake. The Isle of Shadow rises out of the black water with its
// fortress of bones, and Zanthus the Lich stands before it and speaks; then, as he
// foretold, the lake runs to blood, and the night closes over it all.
//
// The island is the one the gypsy shows in her cloud (a dark island with a fortress
// of bones at its centre). His words, in a bubble beside him, are his own: his
// greeting, then his prophecy as the old tale records it.
public sealed class ZanthusScene : SplashScene
{
    public override int LoopFrames => 150;

    private const int FadeInEnd  = 12;
    private const int RiseEnd    = 40;    // the island is up
    private const int LichIn     = 44;    // Zanthus fades in before the fortress
    private const int SpeakAt    = 52;
    private const int BloodStart = 86;    // the lake starts turning
    private const int BloodEnd   = 112;
    private const int FadeOutAt  = 128;
    private const int FadeOutEnd = 146;
    // 146..149: clear.

    private const string Greeting = "Mortal, you are forgiven, for mine is a power you can't destroy.";
    private const string Prophecy = "'My return will be marked by the lake turning to blood and this land shall be mine one century hence.'";

    private static readonly (byte, byte, byte) Night = (8, 10, 26), NightLow = (16, 20, 44), Star = (190, 200, 230),
        Water = (22, 44, 76), WaterLit = (64, 104, 152), Blood = (92, 8, 12), BloodLit = (176, 30, 36),
        Land = (24, 22, 28), Bone = (206, 198, 176), BoneDk = (130, 122, 106), Robe = (62, 30, 84),
        Hood = (40, 20, 56), LichEye = (120, 255, 140), BubbleFill = (10, 12, 26), BubbleRim = (150, 160, 200),
        Quote = (60, 200, 90);

    private static readonly string[] Fortress =
    {
        "      ▄███▄      ",
        "     █▀▄█▄▀█     ",
        "  ▄   ▀█▀█▀   ▄  ",
        " ╫█╫   ███   ╫█╫ ",
        " ╫█╫▄▄▄███▄▄▄╫█╫ ",
        " ╫█╫╬╬╬╬╬╬╬╬╬╫█╫ ",
        "▄╫█╫╬╬╬▄▄▄╬╬╬╫█╫▄",
    };
    private static readonly string[] FortressPaint =
    {
        "      bbbbb      ",
        "     bdbbbdb     ",
        "  b   bdbdb   b  ",
        " bbb   bbb   bbb ",
        " bbbbbbbbbbbbbbb ",
        " bbbdddddddddbbb ",
        "bbbbddddddddbbbbb",
    };
    private static readonly string[] Lich =
    {
        "  ▄▄▄  ",
        " ▐◉ ◉▌ ",
        " ▐▄▀▄▌ ",
        "▄▓▓▓▓▓▄",
        "▓▓▓▓▓▓▓",
        " ▓▓▓▓▓ ",
        "▄▓▓▓▓▓▄",
    };
    private static readonly string[] LichPaint =
    {
        "  hhh  ",
        " hehhe ",
        " hbbbh ",
        "rrrrrrr",
        "rrrrrrr",
        " rrrrr ",
        "rrrrrrr",
    };

    public override void Render(SplashCanvas c, int f)
    {
        double light = SplashCanvas.FadeInOut(f, FadeInEnd, FadeOutAt, FadeOutEnd);
        if (light <= 0) return;

        int top = SplashCanvas.TitleRows;
        int horizon = top + (int)Math.Round((c.Rows - top) * 0.5);
        double blood = SplashCanvas.Smooth((f - BloodStart) / (double)(BloodEnd - BloodStart));

        // Night sky, lighter toward the water line, and its stars.
        c.FillBg(0, top, c.Cols - 1, horizon - 1, SplashCanvas.Bg(Night, light));
        c.FillBg(0, horizon - 2, c.Cols - 1, horizon - 1, SplashCanvas.Bg(NightLow, light));
        for (int i = 0; i < c.Cols / 3; i++)
        {
            int sx = (int)(SplashCanvas.Noise(i, 3) * c.Cols);
            int sy = top + (int)(SplashCanvas.Noise(i, 5) * Math.Max(1, horizon - top - 3));
            bool twinkle = SplashCanvas.Noise(i, f / 5) > 0.8;
            c.Put(sx, sy, twinkle ? '✦' : '·', SplashCanvas.Fg(Star, light * (twinkle ? 1 : 0.6)));
        }

        // The lake: dark water with drifting crests, running to blood.
        (byte, byte, byte) water = SplashCanvas.Mix(Water, Blood, blood);
        (byte, byte, byte) crest = SplashCanvas.Mix(WaterLit, BloodLit, blood);
        c.FillBg(0, horizon, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(water, light));
        for (int y = horizon; y < c.Rows; y++)
            for (int x = 0; x < c.Cols; x++)
            {
                double n = SplashCanvas.Noise(x + f / 2 * (1 + (y & 1)), y);
                if (n > 0.86) c.Put(x, y, n > 0.95 ? '≈' : '~', SplashCanvas.Fg(crest, light));
            }

        // The isle rises out of the water, the bone fortress on its back.
        int cx = c.Cols / 2;
        double rise = SplashCanvas.Smooth((f - FadeInEnd) / (double)(RiseEnd - FadeInEnd));
        int islandTop = horizon + 1 + (int)Math.Round((1 - rise) * (Fortress.Length + 2));
        for (int y = islandTop; y < horizon + 3 && y < c.Rows; y++)
        {
            int half = 6 + (y - islandTop) * 4;
            for (int x = cx - half; x <= cx + half; x++) c.PutBg(x, y, SplashCanvas.Bg(Land, light));
        }
        int fortressY = islandTop - Fortress.Length;
        CellAttributes BonePaint(char k) => SplashCanvas.Fg(k == 'd' ? BoneDk : Bone, light);
        // Under the water line the fortress is still submerged: only what's risen shows.
        for (int r = 0; r < Fortress.Length; r++)
            if (fortressY + r < horizon + 1)
                c.Sprite(cx - Fortress[0].Length / 2, fortressY + r, [Fortress[r]], [FortressPaint[r]], BonePaint);

        // Zanthus, fading in before the gate.
        if (f >= LichIn)
        {
            double lichLight = light * SplashCanvas.Smooth((f - LichIn) / 10.0);
            CellAttributes LichPaints(char k) => k switch
            {
                'e' => SplashCanvas.Fg(LichEye, lichLight * (0.7 + 0.3 * Math.Sin(f * 0.5))),
                'h' => SplashCanvas.Fg(Hood, lichLight),
                'b' => SplashCanvas.Fg(Bone, lichLight),
                _ => SplashCanvas.Fg(Robe, lichLight),
            };
            c.Sprite(cx - Lich[0].Length / 2, islandTop - Lich.Length + 2, Lich, LichPaint, LichPaints);
        }

        // He speaks: his greeting, then the prophecy as the water turns.
        if (f >= SpeakAt && f < FadeOutAt)
        {
            int lichTop = islandTop - Lich.Length + 2;
            int left = Math.Min(cx + 8, Math.Max(1, c.Cols - 24));
            bool prophecy = f >= BloodStart;
            c.SpeechBubble(left, lichTop - 1, Math.Min(36, c.Cols - left - 1),
                prophecy ? Prophecy : Greeting, (f - (prophecy ? BloodStart : SpeakAt)) * 3,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Quote, light), SplashCanvas.Bg(BubbleFill, light),
                (cx + 4, lichTop + 1));
        }
    }
}
