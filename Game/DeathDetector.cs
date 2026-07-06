using System.Text.RegularExpressions;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

// Watches inbound lines for the post-death lives-count readout and tells
// RoomTracker.NoteDeath so the room the character died in is captured on the
// profile and the tracker switches to PendingRespawn ahead of the respawn room
// display.
//
// MajorMUD emits the lives count in two phrasings, and BOTH are real deaths:
//   "You now have N lives remaining." — after a suicide command or a slain/killed
//     death that doesn't print the miracle flavor.
//   "You have N lives left."          — the tail of the miracle-save sequence
//     ("You have been killed! / But, due to a miracle, you have been saved. /
//     You have N lives left."). Despite the "saved" wording this IS a death: a
//     life is spent (N is the post-death count), non-loyal items drop, and the
//     character is teleported to the graveyard — the miracle line is just flavor
//     that accompanies dying while you still have lives. Only at 0 lives does the
//     engine force-exit instead, so this readout never appears on permadeath.
//
// An earlier version matched only the "remaining" form on the belief the
// miracle-save meant the character survived; that silently dropped every
// miracle-save death from the recovery history. Both readouts now fire — each
// carries the correct post-death lives count, so NoteDeath gets accurate data.
public sealed partial class DeathDetector : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    public DeathDetector(RoomTracker tracker, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _log = log;
    }

    // Bind to the per-session LineExtractor; called by AppServices when the
    // telnet client connects. Idempotent — re-attaching to the same extractor is
    // a no-op.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    public void Dispose()
    {
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }

    // Test seam — feed a plain text line.
    internal void FeedTestLine(string text, DateTimeOffset? when = null)
    {
        OnLine(new LineExtractor.EmittedLine(
            text, [], when ?? DateTimeOffset.UtcNow, false));
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        Match m = DeathRx().Match(line.Text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].ValueSpan, out int lives)) return;
        _log?.Info("DeathDetector",
            $"Death observed: '{line.Text.Trim()}' → {lives} lives left.");
        _tracker.NoteDeath(lives, line.Text.Trim(), line.Timestamp);
    }

    // Both post-death lives readouts: "You now have N lives remaining." (suicide /
    // plain death) and "You have N lives left." (miracle-save death). Singular
    // "life" handled too (1 remaining / 1 left). The only inputs the diagonal
    // cross-forms would add ("now have … left", "have … remaining") don't exist in
    // the game, so the liberal alternation can't produce a false positive.
    [GeneratedRegex(@"^You (?:now have|have) (\d+) (?:lives?|life) (?:remaining|left)\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeathRx();
}
