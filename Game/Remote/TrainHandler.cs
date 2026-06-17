using System;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for <c>@train</c> — a
/// permitted party member asks us to train. Unlike the local Train Now / armed
/// auto-train, this never walks: it assumes we're already standing in a training
/// room and just drives <see cref="TrainerWalkManager.TrainInPlace"/>, which
/// sends <c>train</c> and (when Auto-train-stats is on + a plan row exists)
/// applies the CP plan for the new level.
/// </summary>
/// <remarks>
/// <see cref="PlayerRemoteControls.ExecuteCommands"/>-gated per the catalog —
/// it's a "do something on my behalf" action, like <c>@do</c> / <c>@kill</c>.
/// A request while a train run is already in flight is declined (reply gated on
/// <see cref="RemoteCommandManager.WarnOnDenial"/>).
/// </remarks>
public sealed class TrainHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly TrainerWalkManager _coordinator;
    private bool _disposed;

    public TrainHandler(RemoteCommandManager engine, TrainerWalkManager coordinator)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(coordinator);
        _engine = engine;
        _coordinator = coordinator;

        if (!RemoteCommandCatalog.TryGetCategory("@train", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@train'.");
        _engine.RegisterHandler("@train", category, OnTrain);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@train");
    }

    private void OnTrain(RemoteCommandContext ctx)
    {
        if (_coordinator.IsBusy)
        {
            if (_engine.WarnOnDenial) ctx.Reply("busy training");
            return;
        }
        // The status report is delivered by the coordinator once the (possibly
        // multi-level) run settles — capture the reply sink for that deferred call.
        _coordinator.TrainInPlace(ctx.Reply);
    }
}
