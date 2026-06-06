using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Sniffs outbound user commands going to the wire and notifies
/// <see cref="RoomTracker"/> when one of them affects movement
/// semantics. Specifically:
/// </summary>
/// <list type="bullet">
///   <item><b>Peek</b> — <c>look &lt;dir&gt;</c> / <c>l &lt;dir&gt;</c>
///   commands are previews, not moves. The observer fires
///   <see cref="RoomTracker.NoteLookSent"/> so the next room display is
///   suppressed and doesn't desync the tracker.</item>
///   <item><b>Text-exit movement</b> — verbs like <c>go path</c>,
///   <c>enter portal</c>, <c>climb tree</c>, <c>swim river</c> move
///   the player but don't map to a cardinal <see cref="Direction"/>.
///   The observer fires the string-overload of
///   <see cref="RoomTracker.NoteMoveSent(string, Direction?, DateTimeOffset?)"/>
///   so the step is captured in
///   <see cref="Models.Profile.CharacterProfile.RecentSteps"/> for
///   replay.</item>
/// </list>
/// <remarks>
/// <para>
/// Bare cardinal directions (<c>n</c>, <c>north</c>, etc.) are
/// deliberately NOT announced here. The walker / loop-runner already
/// call <c>NoteMoveSent(Direction)</c> directly before they pump
/// bytes; announcing again from this hook would double-enqueue the
/// move in the tracker's pending queue. For manual cardinal typing,
/// the existing observation path (1-of-1 candidate match) handles the
/// landing correctly without needing a pre-announce.
/// </para>
/// <para>
/// Hooked into the wire-send pipeline by
/// <see cref="ViewModels.MainWindowViewModel.SendUserInput"/>. Same
/// pattern as the trainer-menu / stat-parser / suicide-password
/// observers — short payloads only (anything past ~64 bytes can't be
/// a movement command).
/// </para>
/// </remarks>
public sealed partial class OutboundMovementObserver
{
    private const int MaxBytes = 64;

    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    public OutboundMovementObserver(RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;
    }

    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return;
        string cmd = Encoding.Latin1.GetString(bytes)
            .TrimEnd('\r', '\n', '\0')
            .Trim()
            .ToLowerInvariant();
        if (cmd.Length == 0) return;

        if (LookCommandPattern().IsMatch(cmd))
        {
            _tracker.NoteLookSent();
            return;
        }

        if (TextMovementPattern().IsMatch(cmd))
        {
            _tracker.NoteMoveSent(cmd);
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

    /// <summary>
    /// Peek-direction commands. Accepts <c>look</c>, <c>l</c>,
    /// <c>peek</c>, <c>peer</c> followed by a target. The target is
    /// usually a direction word but we don't gate on it — <c>look at
    /// sign</c> isn't a peek either, but suppressing the next obs
    /// when there's nothing to suppress is harmless (3-s auto-expire).
    /// </summary>
    [GeneratedRegex(
        @"^(l|look|peek|peer)\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LookCommandPattern();

    /// <summary>
    /// Text-exit movement verbs. The follow-up token is the target
    /// (the path / portal / tree / etc.). These move the player but
    /// don't map to a cardinal.
    /// </summary>
    [GeneratedRegex(
        @"^(go|enter|climb|crawl|swim|fly|jump|leap|step|walk|run|ride|sail|board|disembark|embark|exit|leave|cross|descend|ascend)\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextMovementPattern();
}
