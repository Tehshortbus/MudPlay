using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The Volcanic Chamber: a lake of bubbling lava strewn with rocky islands, and on the
// great island at its centre the pulsing ruby the adult red dragon guards. She lies
// curled round it asleep; she wakes, her eyes open, and she gets to her feet, side-on
// with her head turned to us, roars her warning, and breathes: dragonfire pours out
// of her open jaws and swells until it fills the lens, then gutters out cell by cell
// back to black.
//
// The chamber is the dragon's own room (7/1358: obsidian walls, lava, the islands,
// the lair and its ruby); her words, in a bubble, open her greeting.
public sealed class RedDragonScene : SplashScene
{
    public override int LoopFrames => 150;

    private const int FadeInEnd   = 10;
    private const int WakeAt      = 38;    // her eyes open
    private const int StandStart  = 46;    // she gets up
    private const int StandEnd    = 58;
    private const int RoarAt      = 60;
    private const int BreathStart = 96;    // the fire leaves her jaws
    private const int BreathFull  = 108;   // the lens is aflame
    private const int BurnOutEnd  = 144;   // every flame out
    // 144..149: clear.

    private const string Roar = "Foolish mortals, your arrogance will cost you your life!";

    private static readonly (byte, byte, byte) Obsidian = (22, 18, 24), ObsidianLt = (44, 38, 48),
        Lava = (190, 52, 10), LavaHot = (250, 140, 30), LavaDk = (120, 26, 6), Rock = (54, 44, 40),
        RockLt = (84, 70, 62), Ruby = (230, 20, 50), RubyHot = (255, 120, 140), Scale = (168, 28, 20),
        ScaleLt = (214, 66, 40), ScaleDk = (96, 16, 12), Wing = (120, 22, 18), Eye = (255, 220, 60),
        Fang = (240, 232, 210), Maw = (70, 12, 8), Snore = (200, 190, 170), FireOuter = (230, 70, 10), FireMid = (250, 150, 30), FireCore = (255, 236, 150),
        Text = (230, 200, 120), BubbleFill = (20, 10, 10), BubbleRim = (170, 90, 60);

    // Her body side-on, wings raised and tail curling away to the right; her head
    // turned on the long neck to face us, horns up, jaws open on the fangs.
    private static readonly string[] Dragon =
    {
        "                     ▄            ▄       ",
        "▲▄     ▄▲           ▟█▙▄       ▄▟█▙       ",
        "▐█▄▄▄▄▄█▌           ▜███▙▄   ▄▟███▛       ",
        "█●█▀▀▀█●█▄▄▄▄        ▜████▙▟████▛         ",
        " ███████▀▀███▄▄       ▜████████▛          ",
        " ▐▀▄▄▄▀▌  ▀▀▀███████▄▟██████████▙▄▄       ",
        "  ▼▀▀▀▼      ▀▀▀▀▀▀████████████████████▙▄ ",
        "                   ▜█████████████▛▀▀  ▀█  ",
        "                    ▐█▌  ▐█▌ ▐█▌      ▝█  ",
        "                    ▀▀▀  ▀▀▀ ▀▀▀          ",
    };
    private static readonly string[] DragonPaint =
    {
        "                     w            w       ",
        "fS     Sf           wwww       wwww       ",
        "SSSSSSSSS           wwwwww   wwwwww       ",
        "SeSSSSSeSssss        wwwwwwwwwwww         ",
        " SSSSSSSSssssss       wwwwwwwwww          ",
        " SSmmmSS  sssssssssssssssssssssssss       ",
        "  fSSSf      ssssssssssssssssssssssssssss ",
        "                   dddddddddddddddss  ss  ",
        "                    ddd  ddd ddd      dd  ",
        "                    ddd  ddd ddd          ",
    };

    // Asleep: lying curled round the ruby, wings folded, head on her forelegs, eyes shut.
    private static readonly string[] Curled =
    {
        "          ▄▄▄▄▄▄▄▄▄▄           ",
        " ▲▄     ▄█▀▀▀▀▀▀▀▀▀▀█▄▄        ",
        "▐█─█▄▄▄████████████████▄▄▄     ",
        " ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▄ ",
    };
    private static readonly string[] CurledAwake =
    {
        "          ▄▄▄▄▄▄▄▄▄▄           ",
        " ▲▄     ▄█▀▀▀▀▀▀▀▀▀▀█▄▄        ",
        "▐█●█▄▄▄████████████████▄▄▄     ",
        " ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▄ ",
    };
    private static readonly string[] CurledPaint =
    {
        "          wwwwwwwwww           ",
        " fS     wwwwwwwwwwwwwwww       ",
        "SSeSssssssssssssssssssssssss   ",
        " ddddddddddddddddddddddddddddd ",
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
        // The great island at the centre, wide enough for her to lie on.
        for (int dx = -17; dx <= 17; dx++)
        {
            c.PutBg(cx + dx, lavaTop, SplashCanvas.Bg(Rock, light));
            if (Math.Abs(dx) <= 15) c.PutBg(cx + dx, lavaTop + 1, SplashCanvas.Bg(RockLt, light));
        }

        CellAttributes Paints(char k) => k switch
        {
            'w' => SplashCanvas.Fg(Wing, light), 'S' => SplashCanvas.Fg(ScaleLt, light),
            'd' => SplashCanvas.Fg(ScaleDk, light), 'e' => SplashCanvas.Fg(Eye, light),
            'f' => SplashCanvas.Fg(Fang, light), 'm' => SplashCanvas.Fg(f >= BreathStart ? FireCore : Maw, light),
            _ => SplashCanvas.Fg(Scale, light),
        };

        // Asleep, curled round the ruby; then awake; then up on her feet, rising out of
        // her crouch onto the island.
        int dragonX = cx - Dragon[0].Length / 2;
        double stand = SplashCanvas.Smooth((f - StandStart) / (double)(StandEnd - StandStart));
        int dragonY = lavaTop - Dragon.Length + (int)Math.Round((1 - stand) * 5);
        (int X, int Y) mouth = (dragonX + 4, dragonY + 5);
        if (f < StandStart)
        {
            int curlX = cx - Curled[0].Length / 2, curlY = lavaTop - Curled.Length;
            c.Sprite(curlX, curlY, f < WakeAt ? Curled : CurledAwake, CurledPaint, Paints);
            if (f < WakeAt)
                for (int z = 0; z < 3; z++)
                {
                    int age = (f + z * 7) % 21;
                    c.Put(curlX + 2 + age / 4, curlY - 1 - age / 5, age < 10 ? 'z' : 'Z', SplashCanvas.Fg(Snore, light * (1 - age / 24.0)));
                }
        }
        else
        {
            for (int r = 0; r < Dragon.Length; r++)
                if (dragonY + r < lavaTop)   // whatever is still crouched below the island's top stays hidden
                    c.Sprite(dragonX, dragonY + r, [Dragon[r]], [DragonPaint[r]], Paints);
        }
        double pulse = 0.5 + 0.5 * Math.Sin(f * 0.45);
        c.Put(cx, lavaTop - 1, '◆', SplashCanvas.Fg(SplashCanvas.Mix(Ruby, RubyHot, pulse), light));

        if (f >= RoarAt && f < BreathStart)
        {
            // Below and in front of her jaws, over the lava, the tail back up to her mouth.
            int width = Math.Clamp(mouth.X - 3, 16, 30);
            c.SpeechBubble(Math.Max(1, mouth.X - 2 - width), mouth.Y + 3, width, Roar, (f - RoarAt) * 3,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Text, light), SplashCanvas.Bg(BubbleFill, light),
                (mouth.X, mouth.Y + 2));
        }

        // Dragonfire: out of her open jaws, swelling toward us until it fills the lens,
        // hottest at her mouth.
        if (f >= BreathStart)
        {
            double p = SplashCanvas.EaseIn((f - BreathStart) / (double)(BreathFull - BreathStart));
            double reach = SplashCanvas.Lerp(1.5, c.MaxRadius() * 1.6, p);
            for (int y = top; y < c.Rows; y++)
                for (int x = 0; x < c.Cols; x++)
                {
                    double dx = x - mouth.X, dy = (y - mouth.Y) * 2.0;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double edge = reach * (0.85 + 0.3 * SplashCanvas.Noise(x + f, y));
                    if (d > edge) continue;
                    double heat = 1 - d / Math.Max(1, edge);
                    c.PutBg(x, y, SplashCanvas.Bg(heat > 0.6 ? FireCore : heat > 0.25 ? FireMid : FireOuter));
                }
        }
    }
}
