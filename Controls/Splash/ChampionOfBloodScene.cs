using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// The Dark Temple's Altar of Blood. We walk up the aisle between the black candles
// toward the great stone altar, a horned skull carved into its top, blood pouring
// from the skull's mouth down the altar's face into the basin; the Champion of Blood
// is there from the first, and comes down the aisle to meet us. Face to face, he
// screams to his god, then raises his skull sword and charges, looming until he fills
// the lens, and the blow lands in a red flash.
//
// The room is the temple's altar chamber (7/1285: a city of black candles, the
// horned skull, the stream of blood and its basin); his cry, in a bubble, is the one
// the temple's cultists greet a visitor with.
public sealed class ChampionOfBloodScene : SplashScene
{
    public override int LoopFrames => 130;

    private const int FadeInEnd  = 10;
    private const int WalkEnd    = 52;    // we've met halfway up the aisle
    private const int ScreamAt   = 56;
    private const int ChargeAt   = 80;    // he raises the skull sword and runs at us
    private const int LoomAt     = 92;    // close enough to fill the view
    private const int SlashAt    = 102;   // the blow lands
    private const int FlashEnd   = 122;   // the flash has faded
    // 122..129: clear.

    private const string Scream = "Glory to the Blood God!";

    private static readonly (byte, byte, byte) Wall = (26, 10, 12), WallDk = (16, 6, 8), Mural = (64, 26, 30),
        FloorC = (34, 16, 16), FloorLine = (58, 30, 28), Candle = (20, 18, 20), Flame = (255, 170, 60),
        FlameHot = (255, 230, 140), Skull = (196, 186, 164), SkullDk = (110, 100, 86), AltarStone = (78, 52, 50),
        AltarPanel = (52, 32, 32), BloodC = (170, 14, 22), Armour = (70, 70, 78),
        ArmourLt = (120, 120, 130), Cape = (120, 16, 24), Eye = (255, 40, 40), Blade = (200, 200, 210),
        Text = (230, 70, 70), BubbleFill = (20, 6, 8), BubbleRim = (150, 60, 60);

    // A broad stone altar on a step, the horned skull carved into its top, a channel
    // down its face for the blood, the basin in front.
    private static readonly string[] Altar =
    {
        "           ▄▀  ▄███▄  ▀▄           ",
        "            ▀▄▄█▀█▀█▄▄▀            ",
        "▗▄▄▄▄▄▄▄▄▄▄▄▄▄▄▀███▀▄▄▄▄▄▄▄▄▄▄▄▄▄▄▖",
        "▐███████████████▌ ▐███████████████▌",
        "▐██▓▓▓▓▓▓▓▓▓▓▓▓▓▌ ▐▓▓▓▓▓▓▓▓▓▓▓▓▓██▌",
        "▐███████████████▌ ▐███████████████▌",
        "▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀",
        "         ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄         ",
        "         ▀███████████████▀         ",
    };
    private static readonly string[] AltarPaint =
    {
        "           ss  sssss  ss           ",
        "            ssssdsdssss            ",
        "aaaaaaaaaaaaaaasssssaaaaaaaaaaaaaaa",
        "aaaaaaaaaaaaaaaaa aaaaaaaaaaaaaaaaa",
        "aaapppppppppppppa apppppppppppppaaa",
        "aaaaaaaaaaaaaaaaa aaaaaaaaaaaaaaaaa",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "         aaaaaaaaaaaaaaaaa         ",
        "         bbbbbbbbbbbbbbbbb         ",
    };
    // The altar from the far end of the aisle.
    private static readonly string[] FarAltar =
    {
        "   ▄▀▄   ",
        "▄▄▄▄█▄▄▄▄",
        "█████████",
    };
    private static readonly string[] FarAltarPaint =
    {
        "   sss   ",
        "aaaasaaaa",
        "aaaaaaaaa",
    };
    // The Champion, coming toward us: far, nearer, then face to face. Each size has
    // its legs mid-stride; the stride swaps as he walks.
    private static readonly string[] ChampionFar =
    {
        "▄█▄",
        "███",
        "▌ ▐",
    };
    private static readonly string[] ChampionFarPaint =
    {
        "aAa",
        "cac",
        "a a",
    };
    private static readonly string[] ChampionMid =
    {
        " ▄█▄  ",
        "▐●█●▌╱",
        "▄███▄ ",
        " █ █  ",
        " ▀ ▀  ",
    };
    private static readonly string[] ChampionMidPaint =
    {
        " aAa  ",
        "aeAeaw",
        "caaac ",
        " a a  ",
        " a a  ",
    };
    private static readonly string[] Champion =
    {
        " ▗▄█▄▖  ",
        " ▐●█●▌ ╱",
        "▄██████╱",
        "█▐████▌▌",
        "▐ ████ ▐",
        "  █▌▐█  ",
        " ▀▀  ▀▀ ",
    };
    private static readonly string[] ChampionPaint =
    {
        " aAAAa  ",
        " aeAea w",
        "caaAaaww",
        "caAAAaaa",
        "c aaaa c",
        "  aaaaa ",
        " aa  aa ",
    };

    // Charging with the skull sword raised, its skull at the guard.
    private static readonly string[] Raised =
    {
        "      ║  ",
        "     ▄█▄ ",
        " ▗▄█▄▖█▀ ",
        " ▐●█●▌█  ",
        "▄██████  ",
        "█▐████▌  ",
        "  █▌▐█   ",
        " ▀▀  ▀▀  ",
    };
    private static readonly string[] RaisedPaint =
    {
        "      w  ",
        "     kkk ",
        " aAAAaak ",
        " aeAeaa  ",
        "caaaaaa  ",
        "caAAAaa  ",
        "  aaaa   ",
        " aa  aa  ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f <= 0 || f >= FlashEnd) return;
        int top = SplashCanvas.TitleRows;

        if (f >= SlashAt)
        {
            // The blow: the sword's cut across the lens, in a red flash fading to black.
            double t = (f - SlashAt) / (double)(FlashEnd - SlashAt);
            c.FillBg(0, top, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(BloodC, 1 - SplashCanvas.Smooth(t)));
            if (t < 0.4)
                for (int k = -1; k <= 1; k++)
                    c.Line(c.Cols - 1 + k * 3, top, k * 3, c.Rows - 1, '╱', SplashCanvas.Fg((255, 230, 230), 1 - t * 2));
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, int.MaxValue - 1, int.MaxValue);
        int floorTop = top + (int)Math.Round((c.Rows - top) * 0.62);
        int cx = c.Cols / 2;
        double walk = SplashCanvas.Smooth(f / (double)WalkEnd);

        // The chapel: dark walls with faint twisted murals, a floor whose seams run
        // toward us as we walk.
        c.FillBg(0, top, c.Cols - 1, floorTop - 1, SplashCanvas.Bg(Wall, light));
        for (int x = 0; x < c.Cols; x++)
        {
            if (SplashCanvas.Noise(x, 11) > 0.8) c.PutBg(x, top, SplashCanvas.Bg(WallDk, light));
            if (SplashCanvas.Noise(x, 13) > 0.88)
                c.Put(x, top + 2 + (int)(SplashCanvas.Noise(x, 17) * 3), SplashCanvas.Noise(x, 2) > 0.5 ? '§' : '¥',
                    SplashCanvas.Fg(Mural, light));
        }
        c.FillBg(0, floorTop, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(FloorC, light));
        int seamShift = (int)(walk * 12);
        for (int y = floorTop; y < c.Rows; y++)
            if (((y - floorTop) + seamShift) % 3 == 0)
                for (int x = 0; x < c.Cols; x++) c.Put(x, y, '▁', SplashCanvas.Fg(FloorLine, light));

        // The city of black candles, in rows down both sides of the aisle; walking up
        // the aisle spreads them outward, past the edges of the view.
        for (int i = 0; i < 10; i++)
        {
            double depth = i / 10.0;
            int spread = (int)Math.Round(SplashCanvas.Lerp(6, c.Cols * 0.55, depth * (0.4 + 0.6 * walk) + walk * 0.4));
            int cy = floorTop - 2 + (int)Math.Round(depth * (c.Rows - 3 - floorTop));
            foreach (int side in new[] { -1, 1 })
            {
                int x = cx + side * spread;
                bool hot = SplashCanvas.Noise(i * 3 + side, f / 2) > 0.6;
                c.Put(x, cy - 1, hot ? '♦' : '▲', SplashCanvas.Fg(hot ? FlameHot : Flame, light));
                c.Put(x, cy, '█', SplashCanvas.Fg(Candle, light));
                c.Put(x, cy + 1, '▀', SplashCanvas.Fg(Candle, light));
            }
        }

        // The altar: small at the far end of the aisle, then broad across the back wall
        // as we come up it.
        CellAttributes AltarPaints(char k) => k switch
        {
            's' => SplashCanvas.Fg(Skull, light), 'd' => SplashCanvas.Fg(SkullDk, light),
            'p' => SplashCanvas.Fg(AltarPanel, light), 'b' => SplashCanvas.Fg(BloodC, light),
            _ => SplashCanvas.Fg(AltarStone, light),
        };
        int altarY = floorTop - Altar.Length + 2;
        if (walk < 0.45)
        {
            c.Sprite(cx - FarAltar[0].Length / 2, floorTop - FarAltar.Length + 1, FarAltar, FarAltarPaint, AltarPaints);
            c.Put(cx, floorTop, '▌', SplashCanvas.Fg(BloodC, light));
        }
        else
        {
            c.Sprite(cx - Altar[0].Length / 2, altarY, Altar, AltarPaint, AltarPaints);
            // The stream of blood out of the skull's mouth, down the channel to the basin.
            for (int y = altarY + 3; y < altarY + 8; y++)
                c.Put(cx, y, (f + y) % 3 == 0 ? '║' : '│', SplashCanvas.Fg(BloodC, light));
        }

        // The Champion: there from the start, coming down the aisle to meet us, larger
        // as the gap closes, then screaming, shaking with it.
        CellAttributes ChampionPaints(char k) => k switch
        {
            'A' => SplashCanvas.Fg(ArmourLt, light), 'e' => SplashCanvas.Fg(Eye, light),
            'w' => SplashCanvas.Fg(Blade, light), 'c' => SplashCanvas.Fg(Cape, light),
            'k' => SplashCanvas.Fg(Skull, light), _ => SplashCanvas.Fg(Armour, light),
        };
        int champY = c.Rows - 1 - Champion.Length + 1;
        double meet = SplashCanvas.Smooth(f / (double)WalkEnd);
        bool walking = f < WalkEnd;
        bool stride = walking && (f / 3) % 2 == 1;
        int feet = (int)Math.Round(SplashCanvas.Lerp(floorTop + 1, c.Rows - 1, meet));
        string[] body = meet < 0.35 ? ChampionFar : meet < 0.7 ? ChampionMid : Champion;
        string[] paint = meet < 0.35 ? ChampionFarPaint : meet < 0.7 ? ChampionMidPaint : ChampionPaint;
        if (stride)
        {
            // The other leg forward: the legs' two rows shift a cell toward each other.
            body = (string[])body.Clone();
            int legs = body.Length - (body == ChampionFar ? 1 : 2);
            for (int r = legs; r < body.Length; r++) body[r] = " " + body[r][..^1];
        }
        int shake = f >= ScreamAt && f < ScreamAt + 16 && (f & 1) == 1 ? 1 : 0;
        if (f >= LoomAt)
        {
            // Right on us: twice the size, the sword high, his feet below the lens.
            c.SpriteDoubled(cx - Raised[0].Length, c.Rows - Raised.Length * 2 + 2, Raised, RaisedPaint, ChampionPaints);
        }
        else if (f >= ChargeAt)
        {
            // The charge: the skull sword up, bobbing as he runs.
            c.Sprite(cx - Raised[0].Length / 2, c.Rows - Raised.Length - (f / 2) % 2, Raised, RaisedPaint, ChampionPaints);
        }
        else
        {
            c.Sprite(cx - body[0].Length / 2 + shake, feet - body.Length + 1, body, paint, ChampionPaints);
        }

        if (f >= ScreamAt && f < ChargeAt)
        {
            // Beside him, level with his chest.
            int left = Math.Min(cx + 7, Math.Max(1, c.Cols - Scream.Length - 5));
            c.SpeechBubble(left, champY + 1, Scream.Length + 4, Scream, (f - ScreamAt) * 2,
                SplashCanvas.Fg(BubbleRim, light), SplashCanvas.Fg(Text, light), SplashCanvas.Bg(BubbleFill, light),
                (cx + 4, champY + 2));
        }
    }
}
