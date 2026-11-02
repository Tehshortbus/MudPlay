using System;
using MudPlay.Terminal;

namespace MudPlay.Controls.Splash;

// A fresh adventurer in padded armour, club in hand (the gear a new MajorMUD
// character starts with), faces a cave bear in its Small Cavern, staged like the
// face-off of an old handheld monster game's opening: the hero hops on the spot, the
// bear rears and swipes, the hero springs back, crouches, then leaps in with the club
// raised, and the blow lands in a white flash that fades back to a clear lens.
//
// The cavern is from the room the bear lairs in (1/2156: gouged walls, chewed bones,
// a nest in the far corner). No words: the fight is the scene.
public sealed class CaveBearScene : SplashScene
{
    public override int LoopFrames => 100;

    private const int FadeInEnd  = 10;
    private const int RearStart  = 34;   // the bear rises on its hind legs
    private const int SwipeAt    = 46;   // claws come down; the hero springs back
    private const int CrouchAt   = 54;   // the hero gathers for the leap
    private const int LeapStart  = 64;
    private const int HitAt      = 76;   // the club lands: flash
    private const int FlashEnd   = 92;   // the flash has faded to black
    // 92..99: clear.

    private static readonly (byte, byte, byte) Wall = (36, 29, 24), WallLit = (52, 42, 34),
        Gouge = (78, 64, 52), Floor = (64, 51, 38), FloorDk = (48, 38, 29), Bone = (214, 204, 178),
        Nest = (112, 96, 52), Fur = (98, 68, 42), FurLt = (132, 94, 60), FurDk = (62, 42, 26),
        Snout = (40, 28, 20), BearEye = (240, 206, 120), Claw = (232, 228, 212),
        Helm = (150, 118, 76), Vest = (178, 144, 96), Skin = (222, 180, 140), Pants = (118, 94, 64),
        Boot = (70, 52, 36), Club = (128, 86, 44), ClubHead = (98, 64, 32), Eye = (36, 30, 26);

    private static readonly string[] Hero =
    {
        "  ▄▄▄   ▄▄",
        " ▐█▀•▌  ██",
        " ▄▓▓▓▄ ▐▀ ",
        "▐▓▓▓▓▓▀▀  ",
        " ▓▓▓▓▓    ",
        " ▐█ █▌    ",
        " ▀▀ ▀▀    ",
    };
    private static readonly string[] HeroPaint =
    {
        "  hhh   kk",
        " hhseh  kk",
        " vvvvv cc ",
        "vvvvvvss  ",
        " vvvvv    ",
        " pp pp    ",
        " bb bb    ",
    };
    private static readonly string[] Leap =
    {
        "     ▄██▄ ",
        "  ▄▄▄ ▐▀  ",
        " ▐█▀•▌▄▀  ",
        " ▄▓▓▓▓▀   ",
        "▐▓▓▓▓▓    ",
        " ▐█  ▀█▄  ",
        "▀▀      ▀▀",
    };
    private static readonly string[] LeapPaint =
    {
        "     kkkk ",
        "  hhh cc  ",
        " hhsehss  ",
        " vvvvvs   ",
        "vvvvvv    ",
        " pp  ppp  ",
        "bb      bb",
    };
    private static readonly string[] Bear =
    {
        "  ▄▄ ▄▄          ",
        " ▐█●██●▌▄▄▄▄▄▄   ",
        "▐▀▀██████████████▄",
        " ▀▄▄█████████████▌",
        "    ████████████▌ ",
        "    ██▌ ███  ███▌ ",
        "   ▀▀▀  ▀▀▀  ▀▀▀  ",
    };
    private static readonly string[] BearPaint =
    {
        "  ff ff          ",
        " fFeFFeffffffff  ",
        "nnnFFFFFFFFFFFFFFf",
        " nffFFFFFFFFFFFFFf",
        "    ffffffffffffd ",
        "    ddd ddd  dddd ",
        "   ccc  ccc  ccc  ",
    };
    private static readonly string[] Rearing =
    {
        "▗▖              ",
        "▐█▖  ▄▄ ▄▄      ",
        " ▜█▖▐█●██●▌     ",
        "  ▜█▀▀██████▌   ",
        "    ▀▄▄██████▌  ",
        "     ▐████████▌ ",
        "      ████████▌ ",
        "      ▐██████▌  ",
        "       ██▌ ██▌  ",
        "      ▀▀▀  ▀▀▀  ",
    };
    private static readonly string[] RearingPaint =
    {
        "cc              ",
        "cFf  ff ff      ",
        " fFffFeFFeff    ",
        "  fFnnFFFFFFf   ",
        "    nffFFFFFFf  ",
        "     ffFFFFFFFf ",
        "      ffFFFFFFf ",
        "      dffffffd  ",
        "       ddd ddd  ",
        "      ccc  ccc  ",
    };

    public override void Render(SplashCanvas c, int f)
    {
        if (f >= FlashEnd || f <= 0) return;

        if (f >= HitAt)
        {
            // The blow lands: the whole lens flashes white and fades to black.
            double t = (f - HitAt) / (double)(FlashEnd - HitAt);
            c.FillBg(0, SplashCanvas.TitleRows, c.Cols - 1, c.Rows - 1,
                SplashCanvas.Bg((255, 255, 255), 1 - SplashCanvas.Smooth(t)));
            return;
        }

        double light = SplashCanvas.FadeInOut(f, FadeInEnd, HitAt, HitAt + 1);
        CellAttributes Paint(char key) => key switch
        {
            'h' => SplashCanvas.Fg(Helm, light), 's' => SplashCanvas.Fg(Skin, light),
            'e' => SplashCanvas.Fg(Eye, light), 'v' => SplashCanvas.Fg(Vest, light),
            'p' => SplashCanvas.Fg(Pants, light), 'b' => SplashCanvas.Fg(Boot, light),
            'c' => SplashCanvas.Fg(Club, light), 'k' => SplashCanvas.Fg(ClubHead, light),
            'f' => SplashCanvas.Fg(Fur, light), 'F' => SplashCanvas.Fg(FurLt, light),
            'd' => SplashCanvas.Fg(FurDk, light), 'n' => SplashCanvas.Fg(Snout, light),
            _ => SplashCanvas.Fg(Claw, light),
        };
        CellAttributes BearPaints(char key) => key == 'e' ? SplashCanvas.Fg(BearEye, light)
            : key == 'c' ? SplashCanvas.Fg(Claw, light) : Paint(key);

        int floorY = c.Rows - 5;
        int feetY = c.Rows - 3;

        // The cavern: dark gouged walls, a dirt floor littered with bones, the nest.
        c.FillBg(0, SplashCanvas.TitleRows, c.Cols - 1, floorY - 1, SplashCanvas.Bg(Wall, light));
        for (int x = 0; x < c.Cols; x++)
        {
            int drop = (int)(SplashCanvas.Noise(x, 7) * 3);
            for (int y = SplashCanvas.TitleRows; y < SplashCanvas.TitleRows + drop; y++)
                c.PutBg(x, y, SplashCanvas.Bg(WallLit, light));
        }
        // Deep gouges: claw marks raked into the walls in threes.
        for (int i = 0; i < 6; i++)
        {
            int gx = (int)(SplashCanvas.Noise(i, 19) * (c.Cols - 6));
            int gy = SplashCanvas.TitleRows + 3 + (int)(SplashCanvas.Noise(i, 23) * Math.Max(1, floorY - SplashCanvas.TitleRows - 7));
            for (int k = 0; k < 3; k++)
                c.Line(gx + k * 2, gy, gx + k * 2 + 2, gy + 2, '╲', SplashCanvas.Fg(Gouge, light));
        }
        c.FillBg(0, floorY, c.Cols - 1, c.Rows - 1, SplashCanvas.Bg(Floor, light));
        for (int x = 0; x < c.Cols; x++)
        {
            if (SplashCanvas.Noise(x, 41) > 0.7) c.PutBg(x, c.Rows - 1, SplashCanvas.Bg(FloorDk, light));
            if (SplashCanvas.Noise(x, 43) > 0.9)
                c.Put(x, floorY + (int)(SplashCanvas.Noise(x, 47) * 3), SplashCanvas.Noise(x, 3) > 0.5 ? '═' : '°',
                    SplashCanvas.Fg(Bone, light));
        }
        for (int x = c.Cols - 9; x < c.Cols - 1; x++) c.Put(x, floorY, '≈', SplashCanvas.Fg(Nest, light));

        int heroHome = (int)Math.Round(c.Cols * 0.22);
        int bearX = (int)Math.Round(c.Cols * 0.58);

        // The bear: on all fours, then reared up with its claws high, then the swipe.
        if (f < RearStart)
        {
            c.Sprite(bearX, feetY - Bear.Length + 1, Bear, BearPaint, BearPaints);
        }
        else
        {
            c.Sprite(bearX, feetY - Rearing.Length + 1, Rearing, RearingPaint, BearPaints);
            if (f >= SwipeAt && f < SwipeAt + 4)
                for (int i = 0; i < 3; i++)
                    c.Line(bearX - 4 + i * 2, feetY - 7, bearX - 2 + i * 2, feetY - 5, '╲', SplashCanvas.Fg(Claw, light));
        }

        // The hero: hops on the spot, springs back from the swipe, crouches, leaps.
        int heroX = heroHome, heroY = feetY - Hero.Length + 1;
        string[] sprite = Hero, paint = HeroPaint;
        if (f < SwipeAt)
        {
            heroY -= (f / 4) % 2;                                   // the hop
        }
        else if (f < CrouchAt)
        {
            double p = (f - SwipeAt) / (double)(CrouchAt - SwipeAt);
            heroX = heroHome - (int)Math.Round(p * 4);              // springs back
            heroY -= (int)Math.Round(Math.Sin(p * Math.PI) * 2);
        }
        else if (f < LeapStart)
        {
            heroX = heroHome - 4;
            heroY += 1;                                              // crouched
        }
        else
        {
            double p = (f - LeapStart) / (double)(HitAt - LeapStart);
            heroX = (int)Math.Round(SplashCanvas.Lerp(heroHome - 4, bearX - 7, p));
            heroY -= (int)Math.Round(Math.Sin(p * Math.PI) * 5);
            sprite = Leap;
            paint = LeapPaint;
        }
        c.Sprite(heroX, heroY, sprite, paint, Paint);
    }
}
