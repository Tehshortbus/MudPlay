using System;
using System.Text;

namespace FujinTerm.Game.Map;

/// <summary>
/// Outcome of <see cref="SpecialExitDispatch.TrySendSynchronous"/>.
/// </summary>
internal enum SpecialExitSend
{
    /// <summary>
    /// The exit isn't a synchronous special exit. The caller handles it
    /// itself — async door/hidden FSMs, or a plain cardinal move.
    /// </summary>
    NotHandled,

    /// <summary>
    /// The helper emitted the crossing bytes and notified the tracker /
    /// recovery gate. The caller does nothing further for this step.
    /// </summary>
    Sent,

    /// <summary>
    /// The exit is a special exit but its game-data is invalid (see the
    /// out reason). The caller should fail its walk / loop.
    /// </summary>
    Failed,
}

/// <summary>
/// Shared emission logic for the <b>synchronous</b> special exits —
/// <see cref="RoomExitHint.Text"/>, <see cref="RoomExitHint.Teleport"/>,
/// and same-room <see cref="RoomExitHint.MultiActionHidden"/>. Both
/// <see cref="AutoWalkManager"/> (one-shot walks) and
/// <see cref="LoopRunner"/> (loop circuits) cross these exits the same
/// way, so the byte construction + tracker bookkeeping lives here once
/// rather than being duplicated per engine.
/// </summary>
/// <remarks>
/// The two <b>asynchronous</b> special exits — door-open and hidden-exit
/// reveal — are deliberately excluded: their FSMs (await the server's
/// reply, then continue) differ per engine and stay owned by the caller.
/// This helper only covers the cases that complete in a single send.
/// </remarks>
internal static class SpecialExitDispatch
{
    /// <summary>
    /// Cross <paramref name="exit"/> in <paramref name="direction"/> when
    /// it is a synchronous special exit. Returns <see cref="SpecialExitSend.NotHandled"/>
    /// for ordinary passages and for the async door/hidden hints so the
    /// caller can fall through to its own handling.
    /// </summary>
    /// <param name="exit">The resolved exit being crossed.</param>
    /// <param name="direction">The cardinal the path assigned to this exit.</param>
    /// <param name="sourceRoom">Current room — needed to resolve teleport keywords.</param>
    /// <param name="tracker">Shared room tracker (notified of the move).</param>
    /// <param name="recovery">Shared recovery gate (notified of the engine step), or null.</param>
    /// <param name="emitMove">
    /// Sends the move-<i>completing</i> bytes (the Text/Teleport command, or
    /// the post-multi-action cardinal). Callers fire their pre-move hook
    /// here so stealth lands on the actual move, mirroring the walker.
    /// </param>
    /// <param name="writeAux">
    /// Sends fire-and-forget prerequisite bytes (multi-action commands, the
    /// teleport party-relay) — no pre-move hook.
    /// </param>
    /// <param name="teleportResolver">(source, dest) → keyword, or null when unwired.</param>
    /// <param name="isLeaderWithFollowers">True when the local character should relay the teleport keyword to followers.</param>
    /// <param name="failReason">Populated when the return value is <see cref="SpecialExitSend.Failed"/>.</param>
    public static SpecialExitSend TrySendSynchronous(
        RoomExit exit,
        Direction direction,
        Room? sourceRoom,
        RoomTracker tracker,
        EngineRecoveryGate? recovery,
        Action<byte[], string> emitMove,
        Action<byte[], string> writeAux,
        Func<RoomKey, RoomKey, string?>? teleportResolver,
        Func<bool>? isLeaderWithFollowers,
        out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(emitMove);
        ArgumentNullException.ThrowIfNull(writeAux);
        failReason = null;

        // MultiActionHidden — `(Hidden, Needs N Actions, ...)`. Execute the
        // prerequisite commands in StepNumber order, then send the cardinal.
        // Same-room actions only; cross-room remote actions fail with a
        // clear reason (the cross-room expander is a separate follow-up).
        if (exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is { } maData)
        {
            if (maData.HasRemoteActions)
            {
                failReason = "multi-action exit requires actions in a different room — cross-room expander not yet wired";
                return SpecialExitSend.Failed;
            }
            if (maData.Actions.Count < maData.RequiredActionCount)
            {
                failReason = $"multi-action exit needs {maData.RequiredActionCount} action(s) but data has {maData.Actions.Count}";
                return SpecialExitSend.Failed;
            }

            foreach (ExitAction action in maData.Actions)
            {
                if (action.Commands.Count == 0) continue;
                string cmd = action.Commands[0];
                writeAux(Encoding.Latin1.GetBytes(cmd + "\r"), $"multi-action #{action.StepNumber}: '{cmd}'");
            }
            tracker.NoteMoveSent(direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(AutoWalkManager.EncodeMove(direction), $"move {direction} (post-multi-action)");
            return SpecialExitSend.Sent;
        }

        // Text exits — `(Text: cmd1, cmd2, ...)`. Any one alternative moves
        // the player (no follow-up cardinal). Send the first.
        if (exit.Hint == RoomExitHint.Text && exit.TextCommands is { Count: > 0 } cmds)
        {
            string textCmd = cmds[0];
            tracker.NoteMoveSent(textCmd, cardinal: direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(Encoding.Latin1.GetBytes(textCmd + "\r"), $"text-exit '{textCmd}' → {exit.Target}");
            return SpecialExitSend.Sent;
        }

        // Teleport exits — `(Item: N)` on a room whose CMD indexes a TBInfo
        // action chain. The resolver maps (source, dest) → the keyword the
        // player types. Party-breaking: a leader relays `.@party <kw>` so
        // followers come along before the leader teleports.
        if (exit.Hint == RoomExitHint.Teleport)
        {
            string? keyword = (sourceRoom is not null && teleportResolver is not null)
                ? teleportResolver(sourceRoom.Key, exit.Target)
                : null;
            if (keyword is null)
            {
                failReason = "no teleport keyword resolved (TBInfo entry missing or not for this destination)";
                return SpecialExitSend.Failed;
            }

            if (isLeaderWithFollowers?.Invoke() == true)
            {
                writeAux(Encoding.Latin1.GetBytes($".@party {keyword}\r"), $"teleport party-relay '.@party {keyword}'");
            }

            tracker.NoteMoveSent(keyword, cardinal: direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(Encoding.Latin1.GetBytes(keyword + "\r"), $"teleport '{keyword}' → {exit.Target}");
            return SpecialExitSend.Sent;
        }

        return SpecialExitSend.NotHandled;
    }
}
