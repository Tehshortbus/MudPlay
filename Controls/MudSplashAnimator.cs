using System;
using System.Collections.Generic;
using Avalonia.Threading;
using MudPlay.Controls.Splash;
using MudPlay.Terminal;

namespace MudPlay.Controls;

// Startup "attract" splash drawn on the terminal until a session begins. Owns a
// STANDALONE TerminalScreen (never the session emulator, so nothing here reaches
// the scrollback), a frame timer, and the always-on "MudPlay / Created By Fujin /
// hint" header. The actual animation is a swappable SplashScene: a random one
// plays through one full loop, then a DIFFERENT one is picked for the next loop,
// so each launch cycles through the collection. Every scene begins and ends on a
// clear lens, so the hand-off between them is seamless.
//
// When animation is disabled the header still shows on its own (a static branding
// screen) and the frame timer never runs.
public sealed class MudSplashAnimator : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);
    private const int TitleRows = SplashCanvas.TitleRows;

    private static readonly CellAttributes Title  = SplashCanvas.Rgb(222, 190, 120);
    private static readonly CellAttributes Byline = SplashCanvas.Rgb(150, 128, 90);
    private static readonly CellAttributes Hint   = SplashCanvas.Rgb(120, 120, 120);
    // Red filled tag (bright text on a red bar) for the "update available" banners
    // that flank the title when a newer build has been detected.
    private static readonly CellAttributes UpdateBanner =
        SplashCanvas.Rgb(255, 236, 236).WithBackground(TerminalColor.Rgb(176, 32, 32));
    private const string UpdateBannerText = " UPDATE AVAILABLE!!! ";

    public TerminalScreen Screen { get; private set; }
    public bool IsPlaying { get; private set; }

    // When true, two red "UPDATE AVAILABLE!!!" banners flank the title (a newer
    // build is available). Set from the update service via TerminalControl;
    // toggling it redraws the header immediately.
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set
        {
            if (_updateAvailable == value) return;
            _updateAvailable = value;
            RenderFrame(_frame);
            FrameAdvanced?.Invoke();
        }
    }
    private bool _updateAvailable;

    // Whether the scenes animate. When false, only the header renders and the
    // frame timer never runs.
    public bool Animate { get; }

    // Raised on the UI thread after each frame is drawn — the control invalidates.
    public event Action? FrameAdvanced;

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    // Shuffle-bag of scene indices: each scene is drawn once (in random order)
    // before any repeats; refilled when empty.
    private readonly List<int> _bag = new();
    private int _lastIndex = -1;
    private SplashScene _scene;
    private SplashCanvas _canvas;
    private int _frame;
    private bool _disposed;

    public MudSplashAnimator(int cols, int rows, bool animate)
    {
        Animate = animate;
        Screen = new TerminalScreen(Clamp(cols, 40, 400), Clamp(rows, 15, 200));
        _canvas = new SplashCanvas(Screen);
        _scene = NextScene();
        // Background priority: the attract splash is the lowest-value work on the
        // UI thread, so it must yield to input and session rendering. On a slow
        // machine this drops frames (the animation just gets choppier) instead of
        // "lagging out" the client while it plays.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
        _timer.Tick += OnTick;
        RenderFrame(0);
    }

    // Draw the next scene from the shuffle-bag, refilling (and reshuffling) once
    // every scene has been shown. Avoids an immediate repeat across a refill.
    private SplashScene NextScene()
    {
        if (_bag.Count == 0)
        {
            for (int i = 0; i < SplashScene.Count; i++) _bag.Add(i);
            for (int i = _bag.Count - 1; i > 0; i--)   // Fisher–Yates
            {
                int j = _rng.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
            if (SplashScene.Count > 1 && _bag[^1] == _lastIndex)
                (_bag[^1], _bag[0]) = (_bag[0], _bag[^1]);
        }
        _lastIndex = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        return SplashScene.At(_lastIndex);
    }

    public void Resize(int cols, int rows)
    {
        int c = Clamp(cols, 40, 400), r = Clamp(rows, 15, 200);
        if (Screen.Cols == c && Screen.Rows == r) return;
        Screen = new TerminalScreen(c, r);
        _canvas = new SplashCanvas(Screen);
        RenderFrame(_frame);
    }

    public void Start()
    {
        if (IsPlaying || _disposed) return;
        IsPlaying = true;
        if (Animate) _timer.Start();
        else RenderFrame(0);        // static header only
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _frame++;
        if (_frame >= _scene.LoopFrames)
        {
            // One full loop done — hand off to the next scene from the shuffle-bag.
            // Both the frame we just drew (LoopFrames-1) and frame 0 of the next
            // scene are a clear lens, so the swap is invisible.
            _frame = 0;
            _scene = NextScene();
        }
        RenderSceneFrame(_frame);
        FrameAdvanced?.Invoke();
    }

    // Full render — clear the whole grid, draw the static header once, then the
    // scene. Used on init / resize where nothing prior is on screen, so there's no
    // previous frame to un-draw (the footprint is reset).
    private void RenderFrame(int f)
    {
        Screen.ClearAll(SplashCanvas.BgAttr);
        DrawHeader(Screen);
        _canvas.ResetFootprint();
        if (Animate) _scene.Render(_canvas, f);
        Screen.Bump();
    }

    // Per-tick render — the header and background persist; clear only the previous
    // frame's scene footprint and re-draw the scene, avoiding the whole-grid
    // ClearAll and header redraw every 80ms.
    private void RenderSceneFrame(int f)
    {
        _canvas.BeginFrame();
        _scene.Render(_canvas, f);
        Screen.Bump();
    }

    private void DrawHeader(TerminalScreen s)
    {
        const string title  = "M u d P l a y";
        const string byline = "Created By Fujin";
        const string hint   = "Load a profile or connect to a BBS to begin";
        int titleX = (s.Cols - title.Length) / 2;
        PutStr(s, titleX, 0, title, Title);
        PutStr(s, (s.Cols - byline.Length) / 2, 1, byline, Byline);
        PutStr(s, (s.Cols - hint.Length) / 2, 3, hint, Hint);

        if (_updateAvailable) DrawUpdateBanners(s, titleX, title.Length);
    }

    // Two red "UPDATE AVAILABLE!!!" tags flanking the title on its row — an
    // at-a-glance "a newer build is available; use Help → Update the Client". Each is
    // drawn with an opaque red background (spaces included) so it reads as a solid
    // banner; skipped when the terminal is too narrow to fit either without colliding
    // with the title.
    private static void DrawUpdateBanners(TerminalScreen s, int titleX, int titleLen)
    {
        const int gap = 2;
        int leftEnd = titleX - gap;                     // last column before the left banner's gap
        int leftStart = leftEnd - UpdateBannerText.Length;
        int rightStart = titleX + titleLen + gap;
        if (leftStart >= 0)
            PutBanner(s, leftStart, 0, UpdateBannerText);
        if (rightStart + UpdateBannerText.Length <= s.Cols)
            PutBanner(s, rightStart, 0, UpdateBannerText);
    }

    // Like PutStr but writes EVERY cell (spaces included) so the red background forms
    // an unbroken bar.
    private static void PutBanner(TerminalScreen s, int x, int y, string text)
    {
        if (y < 0 || y >= s.Rows) return;
        for (int i = 0; i < text.Length; i++)
        {
            int cx = x + i;
            if (cx < 0 || cx >= s.Cols) continue;
            s.Put(cx, y, new Cell(text[i], UpdateBanner));
        }
    }

    private static void PutStr(TerminalScreen s, int x, int y, string text, CellAttributes attr)
    {
        if (y < 0 || y >= s.Rows) return;
        for (int i = 0; i < text.Length; i++)
        {
            int cx = x + i;
            if (cx < 0 || cx >= s.Cols) continue;
            if (text[i] != ' ') s.Put(cx, y, new Cell(text[i], attr));
        }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
