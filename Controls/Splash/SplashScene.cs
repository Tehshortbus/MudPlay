using System;

namespace MudPlay.Controls.Splash;

// One startup splash animation. Stateless: Render must be a pure function of the
// frame index (and the canvas dimensions), so the harness can swap scenes at a
// loop boundary with no carried state and every playback is deterministic.
//
// Seamless-swap contract: frame 0 AND frame (LoopFrames-1) must leave the mud
// band CLEAR (draw nothing below the header). Every scene rises from a clean lens
// and settles back to one, so the harness can hand off from any scene to any
// other with no visible cut — the boundary state is identical for all of them.
public abstract class SplashScene
{
    // Frames in one full play. Scenes may differ; the slower ones just use more.
    public abstract int LoopFrames { get; }

    // Paint frame f (0..LoopFrames-1) into the mud band. The header is already
    // drawn; do not touch rows above SplashCanvas.TitleRows.
    public abstract void Render(SplashCanvas c, int f);

    // The full rotation. One entry per animation; the harness cycles through them
    // one full loop at a time, picking a different one each time (see
    // MudSplashAnimator.NextScene).
    private static readonly Func<SplashScene>[] Factory =
    {
        () => new MudThrowScene(),
        () => new MonsterTruckScene(),
        () => new MudslideScene(),
        () => new MudPieScene(),
        () => new PigWallowScene(),
        () => new MudGeyserScene(),
        () => new MudRainScene(),
        () => new WiperScene(),
        () => new SwampMonsterScene(),
        () => new GoblinSinkScene(),
        () => new SeagullsScene(),
        () => new ChocoboScene(),
        () => new CaveBearScene(),
        () => new ZanthusScene(),
        () => new ChampionOfBloodScene(),
        () => new RedDragonScene(),
        () => new GoruNezarScene(),
        () => new GnajScene(),
        () => new MalivekScene(),
        () => new StormGiantKingScene(),
        () => new DesertPanoramaScene(),
    };

    public static int Count => Factory.Length;

    // A fresh scene at the given index (wraps). The harness draws indices from a
    // shuffle-bag so every scene is shown once before any repeats.
    public static SplashScene At(int index) => Factory[((index % Count) + Count) % Count]();
}
