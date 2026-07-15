using System;
using System.Text;

namespace FujinTerm.Game.Map;

// Outcome of SpecialExitDispatch.TrySendSynchronous.
internal enum SpecialExitSend
{
    // The exit isn't a synchronous special exit. The caller handles it itself —
    // async door/hidden FSMs, or a plain cardinal move.
    NotHandled,

    // The helper emitted the crossing bytes and notified the tracker / recovery
    // gate. The caller does nothing further for this step.
    Sent,

    // The exit is a special exit but its game-data is invalid (see the out
    // reason). The caller should fail its walk / loop.
    Failed,
}

// Shared emission logic for the synchronous special exits — Text, Teleport, and
// same-room MultiActionHidden. Both AutoWalkManager (one-shot walks) and
// LoopRunner (loop circuits) cross these exits the same way, so the byte
// construction + tracker bookkeeping lives here once rather than being
// duplicated per engine.
//
// The two asynchronous special exits — door-open and hidden-exit reveal — are
// deliberately excluded: their FSMs (await the server's reply, then continue)
// differ per engine and stay owned by the caller. This helper only covers the
// cases that complete in a single send.
internal static class SpecialExitDispatch
{
    // Cross exit in direction when it is a synchronous special exit. Returns
    // NotHandled for ordinary passages and for the async door/hidden hints so
    // the caller can fall through to its own handling.
    //
    // sourceRoom is the current room (needed to resolve teleport keywords);
    // tracker and recovery are notified of the move / engine step (recovery may
    // be null). emitMove sends the move-completing bytes (the Text/Teleport
    // command, or the post-multi-action cardinal); callers fire their pre-move
    // hook here so stealth lands on the actual move, mirroring the walker.
    // writeAux sends fire-and-forget prerequisite bytes (multi-action commands,
    // the teleport party-relay) — no pre-move hook. teleportResolver maps
    // (source, dest) → keyword, or null when unwired. isLeaderWithFollowers is
    // true when the local character should relay the teleport keyword to
    // followers. onLeaderPartySplitTeleport (optional) fires right after a
    // leader crosses a party-splitting CMD teleport (chime-style): the relayed
    // `.@party <kw>` sends every follower through, but teleporting dissolves the
    // follow chain, so the party engine must re-invite to reform. failReason is
    // populated when the return value is Failed.
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
        out string? failReason,
        Action? onLeaderPartySplitTeleport = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(emitMove);
        ArgumentNullException.ThrowIfNull(writeAux);
        failReason = null;

        // MultiActionHidden — `(Hidden, Needs N Actions, ...)`. Execute the
        // prerequisite commands in StepNumber order, then send the cardinal.
        // Same-room actions only. Cross-room remote actions are pre-linearized
        // into an explicit walk/act/walk-back detour by RemoteActionPathExpander
        // (the walker), so they never reach here as a single MultiActionHidden
        // step; the loop runner doesn't expand them, so this stays a clear-fail
        // safety net for that path.
        if (exit.Hint == RoomExitHint.MultiActionHidden && exit.MultiAction is { } maData)
        {
            if (maData.HasRemoteActions)
            {
                failReason = "multi-action exit requires actions in a different room — not supported on loop circuits";
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

        // Teleport exits. Two shapes reach here: a synthesised Direction.Teleport
        // edge carries its crossing keyword in TextCommands (baked in at graph
        // build so no lookup is needed), while a cardinal slot re-hinted Teleport
        // (a CMD teleport shadowing a Door/KeyLocked exit) carries none and
        // resolves (source, dest) → keyword through the resolver. Party-breaking:
        // a leader relays `.@party <kw>` so followers come along before the
        // leader teleports.
        if (exit.Hint == RoomExitHint.Teleport)
        {
            string? keyword = exit.TextCommands is { Count: > 0 } teleCmds
                ? teleCmds[0]
                : (sourceRoom is not null && teleportResolver is not null)
                    ? teleportResolver(sourceRoom.Key, exit.Target)
                    : null;
            if (keyword is null)
            {
                failReason = "no teleport keyword resolved (TBInfo entry missing or not for this destination)";
                return SpecialExitSend.Failed;
            }

            bool leaderRelay = isLeaderWithFollowers?.Invoke() == true;
            if (leaderRelay)
            {
                writeAux(Encoding.Latin1.GetBytes($".@party {keyword}\r"), $"teleport party-relay '.@party {keyword}'");
            }

            tracker.NoteMoveSent(keyword, cardinal: direction);
            recovery?.NoteEngineStepSent(direction);
            emitMove(Encoding.Latin1.GetBytes(keyword + "\r"), $"teleport '{keyword}' → {exit.Target}");

            // The teleport dissolved the follow chain even though every follower
            // was relayed through — reform the party once we land.
            if (leaderRelay) onLeaderPartySplitTeleport?.Invoke();
            return SpecialExitSend.Sent;
        }

        return SpecialExitSend.NotHandled;
    }
}
