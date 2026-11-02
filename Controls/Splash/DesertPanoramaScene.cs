using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The desert, through our own eyes from the Dusty Village. It opens in the village
// street, nekojin padding past the mud-brick houses; then the view shoots up over the
// rooftops and we look out east over the Scorching Desert to the Great Pyramid at its
// heart, then turn our head across it: right past the Saracen raiders' fortress and
// the canyon to the blue dragons' roost in the sandstone cliffs, dragons wheeling over
// it; back left past the pyramid to the Ancient Fortress far off in the northeast
// corner; and round to the two oases in the north, the Golden Palace behind them,
// where the view fades. Dust devils
// spin over the sand, raiders ride out, and a giant roc flies between us and the
// pyramid.
//
// The village sits at the desert's western edge; each place stands at the bearing and
// rough distance the desert map puts it from there (user, 2026-11-02), nearer ones
// lower in the view, the farthest small on the skyline.
public sealed class DesertPanoramaScene : SplashScene
{
    public override int LoopFrames => 360;

    private const int FadeInEnd  = 10;
    private const int StreetEnd  = 50;    // the view starts to rise
    private const int RiseEnd    = 64;    // up over the rooftops, looking out
    private const int PanEnd     = 336;   // facing the oases
    private const int FadeOutEnd = 354;
    // 354..359: clear.

    // Degrees across the lens: narrow, as an eye fixed on the horizon is, so places a
    // few degrees apart on the map stand well apart in the view.
    private const double FieldOfView = 40;

    // Compass bearings from the village (north 0, east 90), in the order the view turns.
    private const double RoostBearing = 149, CanyonBearing = 129, RaiderBearing = 109, PyramidBearing = 89,
        FortressBearing = 68, EastOasisBearing = 34, PalaceBearing = 5, WestOasisBearing = 5;

    private static readonly (byte, byte, byte) SkyC = (120, 164, 206), SkyHot = (232, 216, 172), Sun = (255, 248, 210),
        Sand = (206, 168, 104), SandLt = (226, 192, 128), SandDk = (176, 138, 82), Ridge = (186, 146, 92),
        Wall = (180, 132, 84), WallDk = (130, 92, 56), Door = (60, 40, 26), Fur = (226, 168, 92), FurDk = (170, 112, 56),
        Cloth = (90, 120, 170), CatEye = (90, 200, 90), Stone = (210, 176, 118), StoneDk = (160, 124, 76),
        Cliff = (176, 106, 62), CliffDk = (122, 70, 40), Cave = (50, 30, 22), Dragon = (60, 120, 230),
        Fort = (168, 128, 88), FortDk = (112, 82, 54), Water = (60, 130, 190), Palm = (60, 140, 60), Trunk = (110, 80, 50),
        Gorge = (120, 76, 44), GorgeDk = (70, 40, 24), Tent = (214, 196, 160), Banner = (180, 40, 40), Stockade = (124, 92, 60),
        Gold = (226, 184, 64), GoldDk = (164, 122, 34), Dust = (196, 160, 110), Turban = (232, 228, 214), Horse = (118, 78, 48), Roc = (104, 70, 40), RocLt = (150, 108, 64);

    // A nekojin close by in the street, walking right: cat ears, whiskered face, a robe,
    // the tail trailing behind. Mirrored for one walking left; the last row is the legs,
    // swapped each stride.
    private static readonly string[] Cat =
    {
        "  ▲   ▲ ",
        " ▐█▄▄▄█▌",
        " █ ●▄● █",
        " ▐▄▄▀▄▄▌",
        "~▐▓▓▓▓▓▌",
        " ▐▓▓▓▓▓▌",
        "  ▐▌ ▐▌ ",
    };
    private static readonly string[] CatPaint =
    {
        "  f   f ",
        " fffffff",
        " f eFe f",
        " fffdfff",
        "tccccccc",
        " ccccccc",
        "  dd dd ",
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
    // The Ancient Fortress, far off: small on the skyline.
    private static readonly string[] FortressArt =
    {
        "▙▟   ▙▟",
        "██▙▄▟██",
        "███▀███",
    };
    private static readonly string[] FortressArtPaint =
    {
        "ff   ff",
        "fffffff",
        "fffDfff",
    };
    // The Golden Palace: gilded domes over a colonnade.
    private static readonly string[] PalaceArt =
    {
        "  ▲    ▲    ▲  ",
        " ▟█▙  ▟█▙  ▟█▙ ",
        "▐█████████████▌",
        "█▌▐█▌▐█▌▐█▌▐█▌█",
    };
    private static readonly string[] PalaceArtPaint =
    {
        "  Y    Y    Y  ",
        " YYY  YYY  YYY ",
        "YYYYYYYYYYYYYYY",
        "YyYYyYYyYYyYYyY",
    };
    // The canyon: a gorge cut down into the sand, its walls falling away to shadow.
    private static readonly string[] CanyonArt =
    {
        "▄▄▄▄▄▖     ▗▄▄▄▄▄",
        "██████▙▖ ▗▟██████",
        "████████▙▟███████",
    };
    private static readonly string[] CanyonArtPaint =
    {
        "gggggg     gggggg",
        "ggggggGG GGgggggg",
        "ggggggGGGGGgggggg",
    };
    // The Saracen raiders' fortress: a stockade with their tents and banners in it.
    private static readonly string[] RaiderArt =
    {
        " ▲  ▲  ▲  ▲ ",
        "▐█▄▟█▙▟█▙▄█▌",
        "████████████",
    };
    private static readonly string[] RaiderArtPaint =
    {
        " r  r  r  r ",
        "TTTTTTTTTTTT",
        "kkkkkkkkkkkk",
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
        // The desert is out there from the first frame: down in the street its far
        // landmarks show over the rooftops, and rising up uncovers the rest.
        Panorama(c, f, light);
        if (f < RiseEnd) Street(c, f, light);
    }

    // The village street in front of the desert view, and the view rising up off it:
    // the street and its houses sink out of the bottom of the lens.
    private static void Street(SplashCanvas c, int f, double light)
    {
        int top = SplashCanvas.TitleRows;
        double rise = SplashCanvas.Smooth((f - StreetEnd) / (double)(RiseEnd - StreetEnd));
        int lift = (int)Math.Round(rise * (c.Rows - top));     // the street sinks out of view
        int ground = top + (int)Math.Round((c.Rows - top) * 0.55) + lift;

        c.FillBg(0, ground, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(Sand, light));
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

        // Nekojin padding along the street in front of the houses, each its own way and
        // pace, the nearer ones lower.
        CellAttributes Paints(char k) => k switch
        {
            'e' => SplashCanvas.Fg(CatEye, light), 'c' => SplashCanvas.Fg(Cloth, light),
            'd' => SplashCanvas.Fg(FurDk, light), 'F' => SplashCanvas.Fg(FurDk, light),
            _ => SplashCanvas.Fg(Fur, light),
        };
        int width = Cat[0].Length;
        for (int i = 0; i < 4; i++)
        {
            int dir = i % 2 == 0 ? 1 : -1;
            int start = (int)(SplashCanvas.Noise(i, 13) * c.Cols);
            int span = c.Cols + width * 2;
            int x = ((start + dir * f / (2 + i % 2)) % span + span) % span - width;
            int feet = ground + 2 + (i % 2) * 2;
            string[] rows = (string[])Cat.Clone(), paint = (string[])CatPaint.Clone();
            if ((f / 3 + i) % 2 == 0) rows[^1] = "  ▐▌▐▌  ";
            if (dir < 0)
                for (int r = 0; r < rows.Length; r++)
                {
                    rows[r] = Mirror(rows[r]);
                    paint[r] = new string(paint[r].Reverse().ToArray());
                }
            c.Sprite(x, feet - rows.Length + 1, rows, paint, Paints);
        }
    }

    // A row of a sprite facing the other way.
    private static string Mirror(string row) =>
        new string(row.Reverse().Select(ch => ch switch
        {
            '▌' => '▐', '▐' => '▌', '▖' => '▗', '▗' => '▖', '▘' => '▝', '▝' => '▘',
            '▙' => '▟', '▟' => '▙', '▛' => '▜', '▜' => '▛', '╱' => '╲', '╲' => '╱',
            _ => ch,
        }).ToArray());

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
            'g' => SplashCanvas.Fg(Gorge, light), 'G' => SplashCanvas.Fg(GorgeDk, light),
            'T' => SplashCanvas.Fg(Tent, light), 'r' => SplashCanvas.Fg(Banner, light),
            'K' or 'k' => SplashCanvas.Fg(Stockade, light), 'Y' => SplashCanvas.Fg(Gold, light),
            'y' => SplashCanvas.Fg(GoldDk, light), _ => SplashCanvas.Fg(Sand, light),
        };
        void Landmark(double bearing, string[] art, string[] paint, int sink)
        {
            int x = ColumnOf(bearing) - art[0].Length / 2;
            if (x + art[0].Length < 0 || x >= c.Cols) return;
            c.Sprite(x, horizon - art.Length + sink, art, paint, Paints);
        }
        Landmark(FortressBearing, FortressArt, FortressArtPaint, 0);
        Landmark(RaiderBearing, RaiderArt, RaiderArtPaint, 0);
        Landmark(PyramidBearing, PyramidArt, PyramidArtPaint, 1);
        Landmark(CanyonBearing, CanyonArt, CanyonArtPaint, 1);
        Landmark(RoostBearing, RoostArt, RoostArtPaint, 2);

        // Blue dragons wheeling over their roost, each on its own circle.
        for (int d = 0; d < 3; d++)
        {
            double a = f * (0.2 + d * 0.05) + d * 2.1;
            int rx = ColumnOf(RoostBearing) + (int)Math.Round(Math.Cos(a) * (6 + d * 3));
            int ry = horizon - 5 - d + (int)Math.Round(Math.Sin(a) * 1.5);
            c.Str(rx, ry, Math.Sin(a) > 0 ? "▀▄▀" : "▄▀▄", SplashCanvas.Fg(Dragon, light));
        }

        // Dust devils spinning over the sand, drifting as they go.
        for (int d = 0; d < 2; d++)
        {
            double bearing = (d == 0 ? 120 : 52) + Math.Sin(f * 0.03 + d) * 5;
            int bx = ColumnOf(bearing), by = horizon + 3 + d;
            for (int k = 0; k < 6; k++)
            {
                int w = k / 2, sway = (int)Math.Round(Math.Sin(f * 0.5 + k) * 0.8) + k / 3;
                for (int i = -w; i <= w; i++)
                    c.Put(bx + i + sway, by - k, (f + k + i) % 2 == 0 ? '(' : ')', SplashCanvas.Fg(Dust, light * (1 - k * 0.1)));
            }
        }

        // Saracen raiders riding out from their fortress, small with the distance.
        for (int r = 0; r < 3; r++)
        {
            double bearing = RaiderBearing - 3 - (f * 0.05 + r * 3) % 14;
            int rx = ColumnOf(bearing), ry = horizon + 1;
            c.Put(rx, ry - 1, '▲', SplashCanvas.Fg(Turban, light));
            c.Str(rx - 1, ry, "▄█▀", SplashCanvas.Fg(Horse, light));
            c.Str(rx - 1, ry + 1, (f / 2 + r) % 2 == 0 ? "▌ ▐" : " ▌▐", SplashCanvas.Fg(Horse, light));
        }

        // A giant roc, wings beating, out over the sand between us and the pyramid.
        {
            double bearing = PyramidBearing + 9 * Math.Sin(f * 0.02);
            int rx = ColumnOf(bearing) - 5, ry = horizon - 10 + (int)Math.Round(Math.Sin(f * 0.07));
            bool up = (f / 3) % 2 == 0;
            if (up) c.Str(rx, ry, "▀▄▄     ▄▄▀", SplashCanvas.Fg(RocLt, light));
            c.Str(rx, ry + 1, up ? "   ▀▄█▄▀   " : "▀▀▀▀▄█▄▀▀▀▀", SplashCanvas.Fg(Roc, light));
        }

        // The Golden Palace to the north, and the two oases nearer, in front of it.
        Landmark(PalaceBearing, PalaceArt, PalaceArtPaint, 1);
        Landmark(EastOasisBearing, OasisArt, OasisArtPaint, 3);
        Landmark(WestOasisBearing, OasisArt, OasisArtPaint, 5);
    }

    // The view's compass heading at p (0..1) of the look round. Rising up facing east,
    // the pyramid is the first thing seen; the view pans right past the raiders'
    // fortress to the dragons' roost, back left past the pyramid to the Ancient
    // Fortress, and on round to the oases in the north (user, 2026-11-02). Each leg is
    // eased so the view lingers on every stop.
    private static double HeadingAt(double p)
    {
        double[] stops = { PyramidBearing, RaiderBearing, RoostBearing, FortressBearing,
            (EastOasisBearing + WestOasisBearing) / 2 };
        double t = Math.Clamp(p, 0, 1) * (stops.Length - 1);
        int leg = Math.Min((int)t, stops.Length - 2);
        return SplashCanvas.Lerp(stops[leg], stops[leg + 1], SplashCanvas.Smooth(t - leg));
    }

    // An angle brought into -180..180.
    private static double Turn(double degrees) => ((degrees % 360) + 540) % 360 - 180;
}
