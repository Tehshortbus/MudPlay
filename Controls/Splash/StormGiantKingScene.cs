using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The Throne Room of Lightning: a carpet up the steps to the dais, and a massive
// throne whose arms are carved as lightning strikes and whose back is shaped like
// storm clouds, a pattern that seems to shift whenever you look away. The storm giant
// king sits on it; he stands up from it and throws up his arms, forked lightning
// cracks down out of the carved clouds, and a blizzard (both his attacks) blows in
// across the lens and away.
//
// The room is his (16/1184).
public sealed class StormGiantKingScene : SplashScene
{
    public override int LoopFrames => 150;

    private const int FadeInEnd   = 12;
    private const int StandStart  = 50;    // he gets up off the throne
    private const int StandEnd    = 62;
    private const int CastAt      = 72;    // his arms go up
    private const int StrikeStart = 78;
    private const int StrikeEnd   = 98;
    private const int SnowStart   = 96;
    private const int SnowFull    = 116;
    private const int SnowEnd     = 146;
    // 146..149: clear.

    private static readonly (byte, byte, byte) Hall = (20, 22, 34), HallDk = (12, 14, 22), Carpet = (110, 24, 40),
        CarpetDk = (76, 14, 26), Step = (70, 72, 86), StepLt = (100, 102, 118), Cloud = (14, 18, 40),
        CloudLt = (40, 52, 96), Bolt = (210, 230, 255), Carve = (190, 214, 240), Giant = (150, 170, 200),
        GiantDk = (100, 116, 146), Beard = (230, 236, 248), Crown = (230, 200, 90), Eye = (180, 240, 255),
        Snow = (236, 242, 255), ThroneStone = (58, 64, 92), ThroneDk = (38, 42, 64);

    // Sitting on the throne: arms on its arms, knees bent, feet on the dais.
    private static readonly string[] Seated =
    {
        "    ▄▲▲▲▄    ",
        "   ▐█●█●█▌   ",
        "   ▐▓▓▓▓▓▌   ",
        " ▄███▓▓▓███▄ ",
        "▐█▌██▓▓▓██▐█▌",
        "   ███████   ",
        "  ▄██▀ ▀██▄  ",
        "  ██     ██  ",
        "  ▀▀     ▀▀  ",
    };
    private static readonly string[] SeatedPaint =
    {
        "    ccccc    ",
        "   gGeGeGg   ",
        "   gbbbbbg   ",
        " GGGGbbbGGGG ",
        "gGgGGbbbGGgGg",
        "   GGGGGGG   ",
        "  GGGg gGGG  ",
        "  GG     GG  ",
        "  gg     gg  ",
    };
    // On his feet.
    private static readonly string[] King =
    {
        "    ▄▲▲▲▄    ",
        "   ▐█●█●█▌   ",
        "   ▐▓▓▓▓▓▌   ",
        " ▄███▓▓▓███▄ ",
        "▐████▓▓▓████▌",
        "▐▌ ███████ ▐▌",
        "▀  ███████  ▀",
        "   ███ ███   ",
        "   ███ ███   ",
        "   ███ ███   ",
        "  ▄██▀ ▀██▄  ",
    };
    private static readonly string[] KingPaint =
    {
        "    ccccc    ",
        "   gGeGeGg   ",
        "   gbbbbbg   ",
        " GGGGbbbGGGG ",
        "gGGGGbbbGGGGg",
        "gg GGGGGGG gg",
        "g  GGGGGGG  g",
        "   GGG GGG   ",
        "   GGG GGG   ",
        "   GGG GGG   ",
        "  GGGg gGGG  ",
    };
    // Casting: both arms flung up to call the storm down.
    private static readonly string[] Casting =
    {
        "▗▖  ▄▲▲▲▄  ▗▖",
        "▐█ ▐█●█●█▌ █▌",
        " ██▐▓▓▓▓▓▌██ ",
        "  ███▓▓▓███  ",
        "   ███▓███   ",
        "   ███████   ",
        "   ███████   ",
        "   ███ ███   ",
        "   ███ ███   ",
        "   ███ ███   ",
        "  ▄██▀ ▀██▄  ",
    };
    private static readonly string[] CastingPaint =
    {
        "gg  GcccG  gg",
        "gG gGeGeGg Gg",
        " GGgbbbbbgGG ",
        "  GGGbbbGGG  ",
        "   GGGbGGG   ",
        "   GGGGGGG   ",
        "   GGGGGGG   ",
        "   GGG GGG   ",
        "   GGG GGG   ",
        "   GGG GGG   ",
        "  GGGg gGGG  ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= SnowEnd) return;
        int top = SplashCanvas.TitleRows;
        int cx = c.Cols / 2;
        int floor = c.Rows - 3;

        if (f >= SnowFull)
        {
            // The blizzard blows on through, thinning from the left as it passes.
            double p = (f - SnowFull) / (double)(SnowEnd - SnowFull);
            int clearTo = (int)Math.Round(SplashCanvas.Smooth(p) * (c.Cols + 12));
            for (int y = top; y < c.Rows; y++)
                for (int x = Math.Max(0, clearTo - 12 + (int)(SplashCanvas.Noise(y, 3) * 12)); x < c.Cols; x++)
                    DrawSnow(x, y);
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);

        // The hall, the dais steps and the carpet running up them.
        c.FillBg(0, top, c.Cols - 1, floor - 1, SplashCanvas.Bg(Hall, light));
        for (int x = 0; x < c.Cols; x += 10) c.FillBg(x, top, x, floor - 1, SplashCanvas.Bg(HallDk, light));
        int daisY = floor - 3;
        for (int s = 0; s < 3; s++)
        {
            int half = c.Cols / 4 + s * 4;
            c.FillBg(cx - half, daisY + s, cx + half, daisY + s, SplashCanvas.Bg(s % 2 == 0 ? Step : StepLt, light));
        }
        c.FillBg(0, floor, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(HallDk, light));
        for (int y = daisY; y <= floor; y++)
        {
            int half = 3 + (y - daisY);
            c.FillBg(cx - half, y, cx + half, y, SplashCanvas.Bg((y & 1) == 0 ? Carpet : CarpetDk, light));
        }

        // The throne back: storm clouds that won't hold still.
        int backTop = top + 1, backBot = daisY - 1, backHalf = Math.Max(10, c.Cols / 6);
        for (int y = backTop; y <= backBot; y++)
            for (int x = cx - backHalf; x <= cx + backHalf; x++)
            {
                double dx = (x - cx) / (double)backHalf, dy = (y - backTop) / (double)Math.Max(1, backBot - backTop);
                double edge = 0.75 + 0.25 * Math.Sin(x * 0.7 + f * 0.12) * (1 - dy);
                if (Math.Abs(dx) > edge) continue;
                double n = SplashCanvas.Noise((x + f / 6) / 3, (y + f / 9) / 2);
                c.PutBg(x, y, SplashCanvas.Bg(n > 0.6 ? CloudLt : Cloud, light));
            }
        // The seat in front of the clouds, and its arms carved as lightning strikes.
        int seatTop = daisY - 4, seatHalf = 8;
        c.FillBg(cx - seatHalf, seatTop, cx + seatHalf, daisY - 1, SplashCanvas.Bg(ThroneStone, light));
        c.FillBg(cx - seatHalf, seatTop, cx + seatHalf, seatTop, SplashCanvas.Bg(ThroneDk, light));
        foreach (int side in new[] { -1, 1 })
        {
            int ax = cx + side * (seatHalf + 2);
            c.FillBg(ax - 1, seatTop - 2, ax + 1, daisY - 1, SplashCanvas.Bg(ThroneDk, light));
            for (int k = 0; k < 4; k++)
                c.Put(ax + (k % 2 == 0 ? -side : side) * 0, seatTop - 2 + k, k % 2 == 0 ? '╲' : '╱', SplashCanvas.Fg(Carve, light));
        }

        // The king: seated, then getting up, then his arms up to cast.
        CellAttributes Paints(char k) => k switch
        {
            'c' => SplashCanvas.Fg(Crown, light), 'e' => SplashCanvas.Fg(Eye, light),
            'b' => SplashCanvas.Fg(Beard, light), 'g' => SplashCanvas.Fg(GiantDk, light),
            _ => SplashCanvas.Fg(Giant, light),
        };
        int kx = cx - King[0].Length / 2;
        if (f < StandStart)
        {
            c.Sprite(kx, daisY - Seated.Length + 1, Seated, SeatedPaint, Paints);
        }
        else
        {
            double stand = SplashCanvas.Smooth((f - StandStart) / (double)(StandEnd - StandStart));
            int kingY = daisY - King.Length + 1 + (int)Math.Round((1 - stand) * 2);
            string[] pose = f >= CastAt ? Casting : King, posePaint = f >= CastAt ? CastingPaint : KingPaint;
            for (int r = 0; r < pose.Length; r++)
                if (kingY + r <= daisY)   // still rising: what is below the dais step stays out of view
                    c.Sprite(kx, kingY + r, [pose[r]], [posePaint[r]], Paints);
        }

        // Forked lightning out of the carved clouds, down to the hall floor.
        if (f >= StrikeStart && f < StrikeEnd && (f - StrikeStart) % 4 < 2)
        {
            for (int b = 0; b < 3; b++)
            {
                int x = cx + (b - 1) * backHalf / 2, y = backTop + 1;
                while (y < c.Rows)
                {
                    int step = SplashCanvas.Noise(b * 17 + y, f) > 0.5 ? 1 : -1;
                    c.Put(x, y, step > 0 ? '╲' : '╱', SplashCanvas.Fg(Bolt));
                    if (SplashCanvas.Noise(b + y, f + 5) > 0.82)
                        c.Line(x, y, x + step * 4, y + 2, step > 0 ? '╲' : '╱', SplashCanvas.Fg(Bolt));
                    x += step;
                    y++;
                }
            }
        }

        // The blizzard rolls in from the right over everything.
        if (f >= SnowStart)
        {
            double p = SplashCanvas.Smooth((f - SnowStart) / (double)(SnowFull - SnowStart));
            int front = c.Cols - (int)Math.Round(p * (c.Cols + 12));
            for (int y = top; y < c.Rows; y++)
                for (int x = Math.Max(0, front + (int)(SplashCanvas.Noise(y, 3) * 12)); x < c.Cols; x++)
                    DrawSnow(x, y);
        }

        void DrawSnow(int x, int y)
        {
            double n = SplashCanvas.Noise(x + f * 2, y + f / 2);
            if (n > 0.55) c.Put(x, y, n > 0.85 ? '*' : '·', SplashCanvas.Fg(Snow));
            else c.PutBg(x, y, SplashCanvas.Bg((180, 190, 210), 0.25 + 0.3 * n));
        }
    }
}
