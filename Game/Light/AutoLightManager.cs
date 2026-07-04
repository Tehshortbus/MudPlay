using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Light;

// Auto-light need poster. When the server prints a "can't see" room-light line
// (The room is pitch black / The room is very dark - you can't see anything)
// this posts a LightSource need to the NeedsRegistry; auto-get fulfils it by
// grabbing a torch / lantern (or, once a light-spell / +illu-gear path exists,
// equipping / casting). Darkness never halts movement — the walker proceeds with
// the accuracy penalty; this engine only requests the light source.
//
// The predictive V = charIllu + roomLight model is deferred — no parser yet
// produces the character's current illumination, so the exact illu gap can't be
// computed. The live-observed path posts a coarse "need any light source"
// descriptor (AnyLightDescriptor); the precise illu>=N gap lands when a charIllu
// source exists.
//
// Only the two "can't see" lines drive a post. The penalized lines
// (barely visible / dimly lit) still render room contents and have no auto-light
// action without a top-up path, so they aren't matched here.
public sealed class AutoLightManager : IDisposable
{
    // LogService category — [AutoLight] rows on each genuinely-new need post.
    public const string LogCategory = "AutoLight";

    // Coarse need descriptor posted on a live "can't see" line. Means "need any
    // illumination source" — used until a charIllu parser lets us compute the
    // precise illu>=N gap. All dark-room posts dedupe to this single need (you
    // need one light source, period).
    public const string AnyLightDescriptor = "illu>=1";

    private readonly NeedsRegistry _needs;
    private readonly LogService? _log;
    private readonly IDisposable _pitchBlackSub;
    private readonly IDisposable _veryDarkSub;

    private Func<bool>? _isEnabled;
    private bool _disposed;

    public AutoLightManager(MessageRouter router, NeedsRegistry needs, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(needs);
        _needs = needs;
        _log = log;

        _pitchBlackSub = router.Subscribe(KnownPatterns.RoomPitchBlack, OnCantSee);
        _veryDarkSub   = router.Subscribe(KnownPatterns.RoomVeryDark,   OnCantSee);
    }

    // Wire the AutoLight master switch (typically
    // GeneralSettings.AutoMode.AutoLight). When the func is null the engine stays
    // dormant — darkness lines are observed but no need is posted.
    public void SetEnabledToggle(Func<bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        _isEnabled = isEnabled;
    }

    private void OnCantSee(MatchResult _)
    {
        if (_isEnabled?.Invoke() != true) return;

        // The registry dedupes on (kind, descriptor); only log the first
        // post so walking a chain of dark rooms doesn't spam.
        bool alreadyPending = _needs
            .Outstanding(NeedKind.LightSource)
            .Any(n => string.Equals(n.Descriptor, AnyLightDescriptor, StringComparison.Ordinal));
        _needs.Post(NeedKind.LightSource, AnyLightDescriptor, nameof(AutoLightManager));
        if (!alreadyPending)
            _log?.Info(LogCategory, "dark room observed — posted LightSource need");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pitchBlackSub.Dispose();
        _veryDarkSub.Dispose();
    }
}
