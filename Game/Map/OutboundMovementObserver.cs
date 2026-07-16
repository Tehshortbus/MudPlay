using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Sniffs outbound user commands going to the wire and notifies RoomTracker
// when one of them affects movement semantics. Specifically:
//   - Peek — "look <dir>" / "l <dir>" commands are previews, not moves. The
//     observer fires RoomTracker.NoteLookSent so the next room display is
//     suppressed and doesn't desync the tracker.
//   - Text-exit movement — verbs like "go path", "enter portal", "climb tree",
//     "swim river" move the player but don't map to a cardinal Direction. The
//     observer fires the debounced string-overload of NoteMoveSent so a
//     manually-typed step is captured in CharacterProfile.RecentSteps for replay.
//   - Bare cardinal movement — the observer fires the debounced Direction
//     overload so manually-typed cardinals still update the tracker (needed to
//     walk a chain of same-named rooms, e.g. Stone Street corridors, where
//     ReconcileFromConfirmed would otherwise read the redisplay as "no move").
//
// Both movement paths route through the tracker's echo-debounce
// (NoteMoveSentByObserver). The walker / loop-runner already call NoteMoveSent
// directly before they pump bytes; those bytes then flow back here, so a raw
// re-announce would double-enqueue the step in the tracker's pending queue —
// which for text exits strands a phantom cardinal-less move that stalls the
// walk. The debounce (matching command / direction within 100 ms) drops the
// engine's own echo while letting genuine manual typing through.
//
// Hooked into the wire-send pipeline by MainWindowViewModel.SendUserInput.
// Same pattern as the trainer-menu / stat-parser / suicide-password
// observers — short payloads only (anything past ~64 bytes can't be a movement
// command).
public sealed partial class OutboundMovementObserver
{
    private const int MaxBytes = 64;

    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private bool _suppressNextCommand;

    public OutboundMovementObserver(RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;
    }

    // Drop the very next observed command instead of interpreting it as a
    // move / peek. MainMenuEntryAutomation calls this right before pumping the
    // realm-entry keystroke (default "E"), which collides with cardinal East
    // yet is a menu selection, not a step. The send path is synchronous, so the
    // next ObserveOutbound call is guaranteed to be that keystroke — without
    // this it would fabricate an East move and desync the just-hydrated login
    // room (Confirmed → Pending → Suspect ×3 → Lost).
    public void SuppressNextMove() => _suppressNextCommand = true;

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim()
            .ToLowerInvariant();
        if (cmd.Length == 0) return;

        if (_suppressNextCommand)
        {
            _suppressNextCommand = false;
            _log?.Info("OutboundMovement", $"Suppressed realm-entry command '{cmd}' (menu selection, not a move).");
            return;
        }

        if (LookCommandPattern().IsMatch(cmd))
        {
            _tracker.NoteLookSent();
            return;
        }

        if (TextMovementPattern().IsMatch(cmd))
        {
            // Debounced overload: the walker / loop-runner (SpecialExitDispatch)
            // already announced this text exit WITH its cardinal before pumping
            // the bytes that land here. Announcing again would double-enqueue the
            // step and stall the walk. Manual text typing (no engine pre-announce)
            // still lands as a real move.
            _tracker.NoteMoveSentByObserver(cmd);
            _log?.Info("OutboundMovement", $"Text-exit move announced: '{cmd}'.");
            return;
        }

        // Bare cardinal — announce so the tracker can predict the
        // landing room. Walker + loop-runner also call NoteMoveSent
        // directly before pumping bytes; the tracker's debounce
        // (matching direction within 100 ms) drops the duplicate.
        // Without this, manual cardinal typing leaves the tracker
        // stuck on the source room when the player walks through a
        // chain of same-named rooms (e.g. Stone Street corridors) —
        // ReconcileFromConfirmed assumes "obs matches current name"
        // means "server redisplay, no movement."
        if (TryParseCardinal(cmd, out Direction d))
        {
            _tracker.NoteMoveSentByObserver(d);
            _log?.Info("OutboundMovement", $"Cardinal move announced: '{cmd}' → {d}.");
        }
    }

    private static bool TryParseCardinal(string cmd, out Direction d)
    {
        switch (cmd)
        {
            case "n":  case "north":     d = Direction.N;  return true;
            case "s":  case "south":     d = Direction.S;  return true;
            case "e":  case "east":      d = Direction.E;  return true;
            case "w":  case "west":      d = Direction.W;  return true;
            case "ne": case "northeast": d = Direction.NE; return true;
            case "nw": case "northwest": d = Direction.NW; return true;
            case "se": case "southeast": d = Direction.SE; return true;
            case "sw": case "southwest": d = Direction.SW; return true;
            case "u":  case "up":        d = Direction.U;  return true;
            case "d":  case "down":      d = Direction.D;  return true;
            default:   d = default;      return false;
        }
    }

    // Peek-direction commands. Accepts every prefix of "look" the game honours
    // (l / lo / loo / look) plus peek / peer, followed by a DIRECTION word.
    // MajorMUD prefix-matches verbs, so "lo w" is a look-west peek just like
    // "l w" or "look w" — omitting the middle prefixes let "lo w" fall through to
    // the cardinal path and desync the tracker onto the peeked room. The verb's
    // trailing \s+ still anchors the alternative to a whole token, so "lock door"
    // / "load" don't false-match.
    //
    // The target MUST be a direction: only a "look <dir>" renders an adjacent
    // room, which is the display the suppression exists to drop. A "look
    // <player>" / "look <item>" / "look at sign" renders a description, never a
    // room — arming suppression for those is NOT harmless, it's actively wrong:
    // if a real move's confirming room render lands inside the 3-s window it gets
    // swallowed, stranding the pending move until a manual re-display. That's the
    // party-splitting-teleport stall — TrapDelegationManager's "look <member>"
    // race probe on a re-join raced the walker's next step and ate its landing.
    [GeneratedRegex(
        @"^(l|lo|loo|look|peek|peer)\s+(northeast|northwest|southeast|southwest|north|south|east|west|up|down|ne|nw|se|sw|n|s|e|w|u|d)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LookCommandPattern();

    // Text-exit movement verbs. The follow-up token is the target (the path /
    // portal / tree / etc.). These move the player but don't map to a cardinal.
    [GeneratedRegex(
        @"^(go|enter|climb|crawl|swim|fly|jump|leap|step|walk|run|ride|sail|board|disembark|embark|exit|leave|cross|descend|ascend)\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextMovementPattern();
}
