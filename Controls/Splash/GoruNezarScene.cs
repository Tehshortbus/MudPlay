using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The royal palace in the Ancient Ruins, its kitchen made over into a war room: a
// heavy table ringed by over-large chairs, a crude map of the city tacked to it.
// Goru-Nezar rises behind the table and roars, grips his dragonclaw staff until its
// orb blazes, and chain lightning (the spell the staff casts) forks out across the
// lens and flashes away.
//
// The room is his own (3/668); his words, in a bubble, are from his greeting.
public sealed class GoruNezarScene : SplashScene
{
    public override int LoopFrames => 140;

    private const int FadeInEnd = 10;
    private const int RiseEnd   = 26;    // he's up behind the table
    private const int RoarAt    = 28;
    private const int GripAt    = 70;    // he grips the staff; the orb starts to glow
    private const int BoltStart = 96;
    private const int FlashAt   = 112;
    private const int FlashEnd  = 132;
    // 132..139: clear.

    private const string Taunt = "So the gods have sent puny mortals to finish what they could not five hundred years ago!";

    private static readonly (byte, byte, byte) Stone = (46, 42, 38), StoneDk = (32, 29, 26), Flagstone = (36, 32, 30), Table = (94, 62, 36),
        TableDk = (66, 42, 24), Chair = (110, 76, 44), MapC = (200, 186, 150), MarkRed = (190, 40, 40),
        MarkBlue = (50, 80, 190), Ogre = (104, 128, 78), OgreDk = (70, 90, 52), OgreEye = (255, 90, 40),
        Tusk = (236, 228, 200), Robe = (70, 40, 90), Staff = (122, 82, 48), Orb = (110, 170, 255),
        OrbHot = (220, 240, 255), Bolt = (190, 220, 255), Quote = (60, 200, 90), BubbleFill = (16, 14, 12),
        BubbleRim = (150, 140, 120);

    private static readonly string[] Goru =
    {
        "     ▄▄▄▄▄          ",
        "   ▐█●▀▀▀●█▌        ",
        "   ▐██▄▀▀▄██▌       ",
        "   ▐▀▀▀▀▀▀▀▀▌       ",
        " ▄████████████▄▄▄   ",
        "▐███▓▓▓▓▓▓▓▓███▌    ",
        "▐███▓▓▓▓▓▓▓▓███     ",
        " ███▓▓▓▓▓▓▓▓███     ",
    };
    private static readonly string[] GoruPaint =
    {
        "     ooooo          ",
        "   oooedddeooo      ",
        "   oootddtoooo      ",
        "   odddddddddo      ",
        " oooooooooooooooo   ",
        "ooooorrrrrrrroooo   ",
        "ooooorrrrrrrrooo    ",
        " ooorrrrrrrrooo     ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= FlashEnd) return;
        int top = SplashCanvas.TitleRows;

        if (f >= FlashAt)
        {
            double t = (f - FlashAt) / (double)(FlashEnd - FlashAt);
            c.FillBg(0, top, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(OrbHot, 1 - SplashCanvas.Smooth(t)));
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);
        int cx = c.Cols / 2;
        int tableY = top + (int)Math.Round((c.Rows - top) * 0.62);

        // Stone walls of the old palace down to the floor the table stands on.
        c.FillBg(0, top, c.Cols - 1, tableY - 1, SplashCanvas.Bg(Stone, light));
        // Big dressed blocks: a mortar course every third row, joints staggered.
        for (int y = top + 2; y < tableY - 1; y += 3)
        {
            for (int x = 0; x < c.Cols; x++) c.Put(x, y, '─', SplashCanvas.Fg(StoneDk, light));
            for (int x = ((y - top) / 3 % 2) * 6; x < c.Cols; x += 12)
                for (int dy = 1; dy <= 2 && y + dy < tableY; dy++) c.Put(x, y + dy, '│', SplashCanvas.Fg(StoneDk, light));
        }
        c.FillBg(0, tableY, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(Flagstone, light));

        // Goru-Nezar, rising up behind the table, the staff in his right hand.
        double rise = SplashCanvas.Smooth((f - FadeInEnd / 2) / (double)(RiseEnd - FadeInEnd / 2));
        int goruX = cx - Goru[0].Length / 2 - 2;
        int goruY = tableY - Goru.Length + 1 + (int)Math.Round((1 - rise) * Goru.Length);
        double glow = f < GripAt ? 0 : SplashCanvas.Smooth((f - GripAt) / 20.0);
        CellAttributes Paints(char k) => k switch
        {
            'd' => SplashCanvas.Fg(OgreDk, light), 'e' => SplashCanvas.Fg(OgreEye, light),
            't' => SplashCanvas.Fg(Tusk, light), 'r' => SplashCanvas.Fg(Robe, light),
            _ => SplashCanvas.Fg(Ogre, light),
        };
        for (int r = 0; r < Goru.Length; r++)
            if (goruY + r < tableY)
                c.Sprite(goruX, goruY + r, [Goru[r]], [GoruPaint[r]], Paints);
        int staffX = goruX + 17;
        int orbY = goruY - 1;
        for (int y = orbY + 1; y < tableY; y++) c.Put(staffX, y, '║', SplashCanvas.Fg(Staff, light));
        if (orbY + 1 < tableY)
        {
            c.Put(staffX - 1, orbY, '╲', SplashCanvas.Fg(Staff, light));
            c.Put(staffX + 1, orbY, '╱', SplashCanvas.Fg(Staff, light));
            double pulse = glow * (0.75 + 0.25 * Math.Sin(f * 0.8));
            c.Put(staffX, orbY, '●', SplashCanvas.Fg(SplashCanvas.Mix(Orb, OrbHot, pulse), light));
            if (glow > 0.3)
                foreach ((int dx, int dy) in new[] { (-2, 0), (2, 0), (0, -1), (-1, -1), (1, -1) })
                    if (SplashCanvas.Noise(dx + f, dy) > 0.4) c.Put(staffX + dx, orbY + dy, '·', SplashCanvas.Fg(OrbHot, glow * light));
        }

        // The heavy table, its chairs, and the crude map with its red and blue marks.
        c.FillBg(2, tableY, c.Cols - 3, tableY + 1, SplashCanvas.Bg(Table, light));
        c.FillBg(2, tableY + 2, c.Cols - 3, tableY + 2, SplashCanvas.Bg(TableDk, light));
        for (int x = 4; x < c.Cols - 4; x += 9)
            c.Str(x, tableY + 3, "╨  ╨", SplashCanvas.Fg(Chair, light));
        c.FillBg(cx - 8, tableY, cx + 8, tableY + 1, SplashCanvas.Bg(MapC, light));
        foreach ((int dx, int dy, bool red) in new[] { (-6, 0, true), (-1, 1, false), (3, 0, true), (6, 1, false) })
            c.Put(cx + dx, tableY + dy, '×', SplashCanvas.Fg(red ? MarkRed : MarkBlue, light));

        if (f >= RoarAt && f < BoltStart)
        {
            int left = Math.Min(staffX + 4, Math.Max(1, c.Cols - 26));
            c.SpeechBubble(left, goruY - 2, Math.Min(32, c.Cols - left - 1), Taunt, (f - RoarAt) * 3,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Quote, light), SplashCanvas.Bg(BubbleFill, light),
                (goruX + 13, goruY + 2));
        }

        // Chain lightning: jagged forks from the orb, more of them each frame, out to
        // every edge of the lens.
        if (f >= BoltStart)
        {
            int bolts = 2 + (f - BoltStart) / 2;
            for (int b = 0; b < bolts; b++)
            {
                int x = staffX, y = orbY;
                double heading = -Math.PI * 0.95 + (Math.PI * 1.9) * SplashCanvas.Noise(b, 101);
                for (int step = 0; step < c.Cols; step++)
                {
                    double jag = (SplashCanvas.Noise(b * 31 + step, f / 2) - 0.5) * 1.6;
                    double dir = heading + jag;
                    int nx = x + (int)Math.Round(Math.Cos(dir) * 2), ny = y + (int)Math.Round(Math.Sin(dir));
                    int dx = nx - x, dy = ny - y;
                    char seg = dy == 0 ? '─' : dx == 0 ? '│' : (dx > 0) == (dy < 0) ? '╱' : '╲';
                    c.Line(x, y, nx, ny, seg, SplashCanvas.Fg(step < 3 ? OrbHot : Bolt));
                    x = nx;
                    y = ny;
                    if (x < 0 || x >= c.Cols || y < top || y >= c.Rows) break;
                }
            }
        }
    }
}
