using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The desert, through our own eyes from the Dusty Village. It opens in the village
// street, nekojin padding past the mud-brick houses; then the view shoots up over the
// rooftops and we look out over the Scorching Desert, turning our head slowly across
// it: the blue dragons' roost in the sandstone cliffs to the south, a dragon wheeling
// over it, round to the Great Pyramid at the desert's heart, on to the Ancient
// Fortress in the northeast, and back past north to the Bountiful Oasis, where the
// view fades.
//
// The village sits at the desert's western edge, so the places stand at the bearings
// they'd be seen at from there.
public sealed class DesertPanoramaScene : SplashScene
{
    public override int LoopFrames => 220;

    private const int FadeInEnd  = 10;
    private const int StreetEnd  = 50;    // the view starts to rise
    private const int RiseEnd    = 64;    // up over the rooftops, looking out
    private const int PanEnd     = 196;   // facing the oasis
    private const int FadeOutEnd = 214;
    // 214..219: clear.

    private const double FieldOfView = 100;   // degrees across the lens

    // Compass bearings from the village (north 0, east 90), in the order the view turns.
    private const double RoostBearing = 145, PyramidBearing = 95, FortressBearing = 45, OasisBearing = -12;

    private static readonly (byte, byte, byte) SkyC = (120, 164, 206), SkyHot = (232, 216, 172), Sun = (255, 248, 210),
        Sand = (206, 168, 104), SandLt = (226, 192, 128), SandDk = (176, 138, 82), Ridge = (186, 146, 92),
        Wall = (180, 132, 84), WallDk = (130, 92, 56), Door = (60, 40, 26), Fur = (226, 168, 92), FurDk = (170, 112, 56),
        Cloth = (90, 120, 170), CatEye = (90, 200, 90), Stone = (210, 176, 118), StoneDk = (160, 124, 76),
        Cliff = (176, 106, 62), CliffDk = (122, 70, 40), Cave = (50, 30, 22), Dragon = (60, 120, 230),
        Fort = (168, 128, 88), FortDk = (112, 82, 54), Water = (60, 130, 190), Palm = (60, 140, 60), Trunk = (110, 80, 50);

    private static readonly string[] Cat =
    {
        " ▲▲ ",
        "▐••▌",
        " ▓▓ ",
        " ▌▐~",
    };
    private static readonly string[] CatPaint =
    {
        " ff ",
        "feef",
        " cc ",
        " ddt",
    };
    private static readonly string[] PyramidArt =
    {
        "      ▲      ",
        "     ▟█▙     ",
        "    ▟███▙    ",
        "   ▟█████▙   ",
        "  ▟███████▙  ",
        " ▟█████████▙ ",
        "▟███████████▙",
    };
    private static readonly string[] PyramidArtPaint =
    {
        "      s      ",
        "     ssd     ",
        "    sssdd    ",
        "   ssssddd   ",
        "  sssssdddd  ",
        " ssssssddddd ",
        "sssssssdddddd",
    };
    private static readonly string[] RoostArt =
    {
        "   ▄▄▄██▄▄▄▄      ",
        " ▄█▀▀█████▀▀██▄   ",
        "▟██▄▄██▀▀██▄▄███▙ ",
        "█████████████████▙",
    };
    private static readonly string[] RoostArtPaint =
    {
        "   cccccccccc     ",
        " cckkcccccckkccc  ",
        "ccccccckkcccccccc ",
        "CCCCCCCCCCCCCCCCCC",
    };
    private static readonly string[] FortressArt =
    {
        "▙▄▟       ▙▄▟",
        "███▙▄▟▙▄▟▙███",
        "█████▛▀▜█████",
        "█████▌ ▐█████",
    };
    private static readonly string[] FortressArtPaint =
    {
        "fff       fff",
        "fffffffffffff",
        "fffffDDDfffff",
        "fffffD Dfffff",
    };
    private static readonly string[] OasisArt =
    {
        " ╲│╱    ╲│╱ ",
        "  │  ╲│╱ │  ",
        "▄▄│▄▄▄│▄▄│▄▄",
        "≈≈≈≈≈≈≈≈≈≈≈≈",
    };
    private static readonly string[] OasisArtPaint =
    {
        " ppp    ppp ",
        "  t  ppp t  ",
        "PPtPPPtPPtPP",
        "wwwwwwwwwwww",
    };

    public override void Render(SplashCanvas c, int f)
    {
        double light = SplashCanvas.FadeInOut(f, FadeInEnd, PanEnd, FadeOutEnd);
        if (light <= 0) return;
        if (f < RiseEnd) Street(c, f, light);
        else Panorama(c, f, light);
    }

    // The village street, and the view rising up off it.
    private static void Street(SplashCanvas c, int f, double light)
    {
        int top = SplashCanvas.TitleRows;
        double rise = SplashCanvas.Smooth((f - StreetEnd) / (double)(RiseEnd - StreetEnd));
        int lift = (int)Math.Round(rise * (c.Rows - top));     // the street sinks out of view
        int ground = top + (int)Math.Round((c.Rows - top) * 0.55) + lift;

        for (int y = top; y < c.Rows; y++)
        {
            double h = (y - top) / (double)Math.Max(1, ground - top);
            c.FillBg(0, y, c.Cols - 1, y, SplashCanvas.Bg(y < ground ? SplashCanvas.Mix(SkyC, SkyHot, h) : Sand, light));
        }
        for (int x = 0; x < c.Cols; x++)
            if (SplashCanvas.Noise(x, 9) > 0.8) c.Put(x, ground + 1 + (int)(SplashCanvas.Noise(x, 4) * 3), '·', SplashCanvas.Fg(SandDk, light));

        // Mud-brick houses along the far side of the street.
        for (int i = 0, x = 2; x < c.Cols - 6; i++)
        {
            int w = 8 + (int)(SplashCanvas.Noise(i, 2) * 6), h = 4 + (int)(SplashCanvas.Noise(i, 5) * 3);
            c.FillBg(x, ground - h, x + w - 1, ground - 1, SplashCanvas.Bg(SplashCanvas.Noise(i, 7) > 0.5 ? Wall : WallDk, light));
            c.FillBg(x + w / 2, ground - 2, x + w / 2 + 1, ground - 1, SplashCanvas.Bg(Door, light));
            c.Put(x + 2, ground - h + 1, '▪', SplashCanvas.Fg(Door, light));
            x += w + 3;
        }

        // Nekojin padding along the street, each its own way and pace.
        CellAttributes Paints(char k) => k switch
        {
            'e' => SplashCanvas.Fg(CatEye, light), 'c' => SplashCanvas.Fg(Cloth, light),
            'd' => SplashCanvas.Fg(FurDk, light), _ => SplashCanvas.Fg(Fur, light),
        };
        for (int i = 0; i < 4; i++)
        {
            int dir = i % 2 == 0 ? 1 : -1;
            int start = (int)(SplashCanvas.Noise(i, 13) * c.Cols);
            int x = ((start + dir * f / (2 + i % 2)) % (c.Cols + 8) + c.Cols + 8) % (c.Cols + 8) - 4;
            int y = ground + 1 + (i % 2) * 2;
            string[] rows = (string[])Cat.Clone();
            bool stride = (f / 3 + i) % 2 == 0;
            rows[3] = dir > 0 ? (stride ? " ▐▌~" : " ▌▐~") : (stride ? "~▐▌ " : "~▌▐ ");
            string[] paint = (string[])CatPaint.Clone();
            paint[3] = dir > 0 ? " ddt" : "tdd ";
            c.Sprite(x, y, rows, paint, Paints);
        }
    }

    // Looking out over the desert from up high, turning slowly.
    private static void Panorama(SplashCanvas c, int f, double light)
    {
        int top = SplashCanvas.TitleRows;
        int horizon = top + (int)Math.Round((c.Rows - top) * 0.46);
        double heading = HeadingAt((f - RiseEnd) / (double)(PanEnd - RiseEnd));
        double degPerCol = FieldOfView / c.Cols;
        double BearingAt(int x) => heading + (x - c.Cols / 2.0) * degPerCol;
        int ColumnOf(double bearing) => c.Cols / 2 + (int)Math.Round(Turn(bearing - heading) / degPerCol);

        // Sky, paling toward the horizon, and the sun to the east.
        for (int y = top; y < horizon; y++)
            c.FillBg(0, y, c.Cols - 1, y, SplashCanvas.Bg(SplashCanvas.Mix(SkyC, SkyHot, (y - top) / (double)Math.Max(1, horizon - top)), light));
        int sunX = ColumnOf(100);
        c.Put(sunX, top + 1, '●', SplashCanvas.Fg(Sun, light));
        c.Put(sunX - 1, top + 1, '·', SplashCanvas.Fg(Sun, light * 0.7));
        c.Put(sunX + 1, top + 1, '·', SplashCanvas.Fg(Sun, light * 0.7));

        // The far dune line along the horizon, fixed to its bearings as we turn.
        for (int x = 0; x < c.Cols; x++)
        {
            double b = BearingAt(x);
            int h = (int)Math.Round(1 + (Math.Sin(b * 0.09) + 1) * 0.8 + Math.Sin(b * 0.27) * 0.5);
            for (int y = horizon - h; y < horizon; y++) c.PutBg(x, y, SplashCanvas.Bg(Ridge, light));
        }

        // The sand from the horizon to our feet: ripples that slide past as we turn,
        // spaced wider the nearer they are.
        for (int y = horizon; y < c.Rows; y++)
        {
            double near = (y - horizon + 1) / (double)Math.Max(1, c.Rows - horizon);
            for (int x = 0; x < c.Cols; x++)
            {
                double b = BearingAt(x);
                double ripple = Math.Sin(b * (0.9 / (0.3 + near)) + (y - horizon) * 1.7);
                c.PutBg(x, y, SplashCanvas.Bg(ripple > 0.55 ? SandLt : ripple < -0.6 ? SandDk : Sand, light));
            }
        }

        // The places on the skyline, each at its bearing.
        CellAttributes Paints(char k) => k switch
        {
            's' => SplashCanvas.Fg(Stone, light), 'd' => SplashCanvas.Fg(StoneDk, light),
            'c' => SplashCanvas.Fg(Cliff, light), 'C' => SplashCanvas.Fg(CliffDk, light),
            'k' => SplashCanvas.Fg(Cave, light), 'f' => SplashCanvas.Fg(Fort, light),
            'D' => SplashCanvas.Fg(FortDk, light), 'p' => SplashCanvas.Fg(Palm, light),
            't' => SplashCanvas.Fg(Trunk, light), 'w' => SplashCanvas.Fg(Water, light),
            _ => SplashCanvas.Fg(Sand, light),
        };
        void Landmark(double bearing, string[] art, string[] paint, int sink)
        {
            int x = ColumnOf(bearing) - art[0].Length / 2;
            if (x + art[0].Length < 0 || x >= c.Cols) return;
            c.Sprite(x, horizon - art.Length + sink, art, paint, Paints);
        }
        Landmark(RoostBearing, RoostArt, RoostArtPaint, 1);
        Landmark(PyramidBearing, PyramidArt, PyramidArtPaint, 1);
        Landmark(FortressBearing, FortressArt, FortressArtPaint, 1);

        // A blue dragon wheeling over its roost.
        double a = f * 0.22;
        int rx = ColumnOf(RoostBearing) + (int)Math.Round(Math.Cos(a) * 8);
        int ry = horizon - 6 + (int)Math.Round(Math.Sin(a) * 1.5);
        c.Str(rx, ry, Math.Sin(a) > 0 ? "▀▄▀" : "▄▀▄", SplashCanvas.Fg(Dragon, light));

        // The oasis lies nearer, just below the skyline.
        Landmark(OasisBearing, OasisArt, OasisArtPaint, 3);
    }

    // The view's compass heading at p (0..1) of the turn: roost, pyramid, fortress,
    // then back past north to the oasis, each leg eased so it lingers on every place.
    private static double HeadingAt(double p)
    {
        double[] stops = { RoostBearing, PyramidBearing, FortressBearing, OasisBearing };
        double t = Math.Clamp(p, 0, 1) * (stops.Length - 1);
        int leg = Math.Min((int)t, stops.Length - 2);
        return SplashCanvas.Lerp(stops[leg], stops[leg + 1], SplashCanvas.Smooth(t - leg));
    }

    // An angle brought into -180..180.
    private static double Turn(double degrees) => ((degrees % 360) + 540) % 360 - 180;
}
