using MudPlay.Controls.Splash;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The startup splash hands off from one scene to the next at a loop boundary with no
// cut, which holds only while every scene starts and ends on a clear lens: frame 0
// and its last frame draw nothing. Every frame of every scene also has to draw at any
// terminal size the splash plays at, from its smallest to a big window, without
// stepping outside the grid.
public sealed class SplashSceneTests
{
    [Fact]
    public void EveryScene_StartsAndEndsOnAClearLens()
    {
        for (int i = 0; i < SplashScene.Count; i++)
        {
            SplashScene scene = SplashScene.At(i);
            foreach (int frame in new[] { 0, scene.LoopFrames - 1 })
            {
                TerminalScreen screen = new(80, 25);
                scene.Render(new SplashCanvas(screen), frame);
                for (int y = 0; y < screen.Rows; y++)
                    for (int x = 0; x < screen.Cols; x++)
                        Assert.True(screen[x, y] == Cell.Blank,
                            $"{scene.GetType().Name} frame {frame} drew '{screen[x, y].Char}' at {x},{y}");
            }
        }
    }

    [Theory]
    [InlineData(40, 15)]
    [InlineData(160, 50)]
    public void EveryFrame_DrawsAtAnySizeTheSplashPlaysAt(int cols, int rows)
    {
        for (int i = 0; i < SplashScene.Count; i++)
        {
            SplashScene scene = SplashScene.At(i);
            TerminalScreen screen = new(cols, rows);
            SplashCanvas canvas = new(screen);
            for (int f = 0; f < scene.LoopFrames; f++)
            {
                canvas.BeginFrame();
                scene.Render(canvas, f);
            }
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < SplashCanvas.TitleRows; y++)
                    Assert.True(screen[x, y] == Cell.Blank, $"{scene.GetType().Name} drew into the header at {x},{y}");
        }
    }
}
