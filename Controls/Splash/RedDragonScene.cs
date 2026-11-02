using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The Volcanic Chamber: a lake of bubbling lava strewn with rocky islands, and on the
// great island at its centre the pulsing ruby the adult red dragon guards. She rises
// up behind it, roars her warning, and breathes: dragonfire sweeps the whole lens,
// then gutters out cell by cell back to black.
//
// The chamber is the dragon's own room (7/1358: obsidian walls, lava, the islands,
// the lair and its ruby); her words, in a bubble, open her greeting.
public sealed class RedDragonScene : SplashScene
{
    public override int LoopFrames => 150;

    private const int FadeInEnd   = 10;
    private const int RiseStart   = 14;
    private const int RiseEnd     = 40;
    private const int RoarAt      = 42;
    private const int BreathStart = 96;    // the fire leaves her jaws
    private const int BreathFull  = 108;   // the lens is aflame
    private const int BurnOutEnd  = 144;   // every flame out
    // 144..149: clear.

    private const string Roar = "Foolish mortals, your arrogance will cost you your life!";

    private static readonly (byte, byte, byte) Obsidian = (22, 18, 24), ObsidianLt = (44, 38, 48),
        Lava = (190, 52, 10), LavaHot = (250, 140, 30), LavaDk = (120, 26, 6), Rock = (54, 44, 40),
        RockLt = (84, 70, 62), Ruby = (230, 20, 50), RubyHot = (255, 120, 140), Scale = (168, 28, 20),
        ScaleLt = (214, 66, 40), ScaleDk = (96, 16, 12), Wing = (120, 22, 18), Eye = (255, 220, 60),
        Fang = (240, 232, 210), FireOuter = (230, 70, 10), FireMid = (250, 150, 30), FireCore = (255, 236, 150),
        Text = (230, 200, 120), BubbleFill = (20, 10, 10), BubbleRim = (170, 90, 60);

    // Side-on, facing left: horned head with the jaws open, the long neck back to her
    // body, both wings raised, the tail curling away to the right.
    private static readonly string[] Dragon =
    {
        "                     ▄            ▄      ",
        "                    ▟█▙▄       ▄▟█▙      ",
        "   ▲ ▲              ▜███▙▄   ▄▟███▛      ",
        "  ▟███▙▄             ▜████▙▟████▛        ",
        " ▟█●████▙▄▄           ▜████████▛         ",
        "▐█▀▀▀▀▀████▙▄▄▄▄▄▄▄▄▄▄▟██████████▙▄▄     ",
        " ▀▼▀▼     ▀▀▀▀▀▀▀▀████████████████████▙▄ ",
        "                   ▜█████████████▛▀▀  ▀█ ",
        "                    ▐█▌  ▐█▌ ▐█▌      ▝█ ",
        "                    ▀▀▀  ▀▀▀ ▀▀▀         ",
    };
    private static readonly string[] DragonPaint =
    {
        "                     w            w      ",
        "                    wwww       wwww      ",
        "   h h              wwwwww   wwwwww      ",
        "  SSSSSS             wwwwwwwwwwww        ",
        " SSeSSSSSSS           wwwwwwwwww         ",
        "SSSSSSSSSSSsssssssssssssssssssssssss     ",
        " SfSf     Ssssssssssssssssssssssssssssss ",
        "                   dddddddddddddddss  ss ",
        "                    ddd  ddd ddd      dd ",
        "                    ddd  ddd ddd         ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= BurnOutEnd) return;
        int top = SplashCanvas.TitleRows;
        int cx = c.Cols / 2;

        if (f >= BreathFull)
        {
            // Every cell burning; each goes out at its own moment, the hottest last.
            double p = (f - BreathFull) / (double)(BurnOutEnd - BreathFull);
            for (int y = top; y < c.Rows; y++)
                for (int x = 0; x < c.Cols; x++)
                {
                    double n = SplashCanvas.Noise(x, y);
                    if (n < p) continue;
                    double heat = (n - p) / Math.Max(0.01, 1 - p);
                    (byte, byte, byte) col = heat > 0.7 ? FireCore : heat > 0.35 ? FireMid : FireOuter;
                    if (SplashCanvas.Noise(x + f, y) > 0.75) c.Put(x, y, '▲', SplashCanvas.Fg(FireCore, heat));
                    else c.PutBg(x, y, SplashCanvas.Bg(col, 0.4 + 0.6 * heat));
                }
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);
        int lavaTop = top + (int)Math.Round((c.Rows - top) * 0.55);

        // Obsidian walls, sheer and smooth, shimmering in the heat.
        c.FillBg(0, top, c.Cols - 1, lavaTop - 1, SplashCanvas.Bg(Obsidian, light));
        for (int x = 0; x < c.Cols; x++)
            if (SplashCanvas.Noise(x, 3) > 0.85)
                for (int y = top; y < lavaTop - 1; y++)
                    if (SplashCanvas.Noise(x, y + f / 3) > 0.6) c.PutBg(x, y, SplashCanvas.Bg(ObsidianLt, light));

        // The lava, bubbling, and the rocky islands in it.
        for (int y = lavaTop; y < c.Rows; y++)
            for (int x = 0; x < c.Cols; x++)
            {
                double n = SplashCanvas.Noise(x + f / 4, y * 3 + f / 6);
                c.PutBg(x, y, SplashCanvas.Bg(n > 0.8 ? LavaHot : n < 0.25 ? LavaDk : Lava, light));
                if (SplashCanvas.Noise(x, y + f) > 0.97) c.Put(x, y, 'o', SplashCanvas.Fg(LavaHot, light));
            }
        for (int i = 0; i < 6; i++)
        {
            int ix = (int)(SplashCanvas.Noise(i, 9) * c.Cols);
            int iy = lavaTop + 1 + (int)(SplashCanvas.Noise(i, 11) * Math.Max(1, c.Rows - lavaTop - 4));
            if (Math.Abs(ix - cx) < 12) continue;
            c.Str(ix, iy, "▄██▄", SplashCanvas.Fg(Rock, light));
        }
        // The great island at the centre, and the ruby pulsing on it.
        for (int dx = -9; dx <= 9; dx++)
        {
            c.PutBg(cx + dx, lavaTop, SplashCanvas.Bg(Rock, light));
            if (Math.Abs(dx) <= 7) c.PutBg(cx + dx, lavaTop + 1, SplashCanvas.Bg(RockLt, light));
        }
        double pulse = 0.5 + 0.5 * Math.Sin(f * 0.45);
        c.Put(cx, lavaTop - 1, '◆', SplashCanvas.Fg(SplashCanvas.Mix(Ruby, RubyHot, pulse), light));

        // She rises up from behind the island.
        double rise = SplashCanvas.Smooth((f - RiseStart) / (double)(RiseEnd - RiseStart));
        int dragonY = lavaTop - 1 - (int)Math.Round(rise * (Dragon.Length + 1));
        int dragonX = cx - Dragon[0].Length / 2;
        (int X, int Y) mouth = (dragonX + 2, dragonY + 6);
        if (rise > 0)
        {
            CellAttributes Paints(char k) => k switch
            {
                'w' => SplashCanvas.Fg(Wing, light), 'S' => SplashCanvas.Fg(ScaleLt, light),
                'd' => SplashCanvas.Fg(ScaleDk, light), 'e' => SplashCanvas.Fg(Eye, light),
                'f' or 'h' => SplashCanvas.Fg(Fang, light), _ => SplashCanvas.Fg(Scale, light),
            };
            for (int r = 0; r < Dragon.Length; r++)
                if (dragonY + r < lavaTop - 1)   // still behind the island below that
                    c.Sprite(dragonX, dragonY + r, [Dragon[r]], [DragonPaint[r]], Paints);
        }

        if (f >= RoarAt && f < BreathStart)
        {
            // Below and in front of her jaws, over the lava, the tail back up to her mouth.
            int width = Math.Clamp(mouth.X - 3, 16, 30);
            c.SpeechBubble(Math.Max(1, mouth.X - 2 - width), mouth.Y + 2, width, Roar, (f - RoarAt) * 3,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Text, light), SplashCanvas.Bg(BubbleFill, light),
                mouth);
        }

        // Dragonfire: a cone out of her jaws, widening toward us until it fills the lens.
        if (f >= BreathStart)
        {
            double p = SplashCanvas.EaseIn((f - BreathStart) / (double)(BreathFull - BreathStart));
            for (int y = top; y < c.Rows; y++)
            {
                double down = Math.Max(0, y - mouth.Y) / (double)Math.Max(1, c.Rows - mouth.Y);
                double half = (2 + down * c.Cols * 0.6) * (0.3 + p * 1.4);
                if (y < mouth.Y - p * (mouth.Y - top)) continue;
                double fx = SplashCanvas.Lerp(mouth.X, cx, p);
                for (int x = (int)(fx - half); x <= fx + half; x++)
                {
                    double n = SplashCanvas.Noise(x + f, y);
                    c.PutBg(x, y, SplashCanvas.Bg(n > 0.66 ? FireCore : n > 0.3 ? FireMid : FireOuter));
                }
            }
        }
    }
}
