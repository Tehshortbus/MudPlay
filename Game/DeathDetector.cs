using System.Text.RegularExpressions;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>
/// Watches inbound lines for the post-suicide / killed-in-combat
/// <c>You now have N lives remaining.</c> message and tells
/// <see cref="RoomTracker.NoteDeath"/> so the room the character died
/// in is captured on the profile and the tracker switches to
/// <see cref="RoomConfidence.PendingRespawn"/> ahead of the respawn
/// room display.
/// </summary>
/// <remarks>
/// <para>
/// MajorMUD emits two phrasings off the same wire shape:
/// </para>
/// <list type="bullet">
///   <item><c>You now have N lives remaining.</c> — after a death
///   (suicide command or combat kill). This counts as death.</item>
///   <item><c>You have N lives left.</c> — after a miracle save. The
///   character survived; not a death.</item>
/// </list>
/// <para>
/// Only the first phrasing fires the detector. Same regex shape
/// <see cref="StatParser"/> uses for its always-on lives-count update,
/// but with the verb tightened to <c>"now have"</c> so the miracle-save
/// line can't trip a phantom death record.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Bind to the per-session <see cref="LineExtractor"/>; called by
    /// <see cref="AppServices"/> when the telnet client connects.
    /// Idempotent — re-attaching to the same extractor is a no-op.
    /// </summary>
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

    /// <summary>Test seam — feed a plain text line.</summary>
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
            $"Death observed: '{line.Text.Trim()}' → {lives} lives remaining.");
        _tracker.NoteDeath(lives, line.Text.Trim(), line.Timestamp);
    }

    /// <summary>
    /// <c>You now have N lives remaining.</c> — the post-death form.
    /// Singular <c>life</c> handled too (1 remaining). The
    /// <c>You have N lives left.</c> miracle-save form is intentionally
    /// not matched here.
    /// </summary>
    [GeneratedRegex(@"^You now have (\d+) (?:lives?|life) remaining\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeathRx();
}
