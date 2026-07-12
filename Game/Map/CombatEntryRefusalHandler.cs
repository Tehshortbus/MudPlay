using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Map;

// Handles the MajorMUD combat-gated-entry refusal. The server rejects a move
// into a room you may not enter while fighting — "You may not enter that room
// while in combat." — WITHOUT ending the fight, so an automated walker/loop
// that just re-sends the same step spins in place forever. Instead we send
// `break` to drop combat, wait a short beat for it to actually end, then revert
// the pending move so the driving engine's own one-shot retry re-issues the
// step once combat has cleared.
//
// Scope guard: only acts while a movement engine is driving
// (MovementControl active) AND a move is actually in flight (RoomTracker
// Pending). A manual player who walks into a gated room mid-fight is left
// alone — we must not silently `break` their combat. The single-refusal
// re-entry latch stops a re-emitted line from stacking breaks/timers.
public sealed partial class CombatEntryRefusalHandler : IDisposable
{
    public const string LogCategory = "CombatEntryRefusal";

    // Beat between `break` and the retry — long enough for the server to end
    // combat so the re-issued move isn't refused a second time.
    private static readonly TimeSpan BreakSettle = TimeSpan.FromSeconds(3);

    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly Func<bool> _isEngineActive;
    private readonly LogService? _log;
    private readonly DispatcherTimer _settle;

    private Action<byte[]>? _wireSender;
    private bool _settling;
    private bool _disposed;

    public CombatEntryRefusalHandler(
        LineExtractor lines,
        RoomTracker tracker,
        Func<bool> isEngineActive,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(isEngineActive);
        _lines = lines;
        _tracker = tracker;
        _isEngineActive = isEngineActive;
        _log = log;
        _settle = new DispatcherTimer { Interval = BreakSettle };
        _settle.Tick += OnSettleElapsed;
        _lines.LineEmitted += OnLineEmitted;
    }

    // Bound in MainWindowViewModel's engine-send block, same as the other
    // wire-driven engines. Until set, the break send is a no-op.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    internal void FeedTestLine(string text) => HandleLine(text);

    // Test seam: run the post-break retry synchronously. The DispatcherTimer
    // tick that drives it in-app isn't pumped in headless xUnit.
    internal void CompleteRetryForTest() => CompleteRetry();

    internal bool IsSettling => _settling;

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        HandleLine(line.Text);
    }

    private void HandleLine(string text)
    {
        if (!CombatGatedEntry().IsMatch(text)) return;
        if (_settling) return;                       // already breaking on this refusal
        if (!_isEngineActive()) return;              // manual player — leave their combat alone
        if (_tracker.State.Confidence != RoomConfidence.Pending) return;   // no engine move in flight

        _settling = true;
        SendBreak();
        _settle.Stop();
        _settle.Start();
        _log?.Info(LogCategory, "combat-gated entry refused — sent break, retrying in 3s");
    }

    private void OnSettleElapsed(object? sender, EventArgs e) => CompleteRetry();

    private void CompleteRetry()
    {
        _settle.Stop();
        if (!_settling) return;
        _settling = false;
        // Revert the refused move so the driving engine's own one-shot retry
        // re-issues the step now that combat should have ended.
        _tracker.NoteMoveBlocked();
    }

    private void SendBreak()
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes("break\r"));
    }

    // Only this exact server line — anchored so a chat line quoting it can't
    // false-trigger.
    [GeneratedRegex(
        @"^\s*You may not enter that room while in combat\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CombatGatedEntry();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lines.LineEmitted -= OnLineEmitted;
        _settle.Stop();
        _settle.Tick -= OnSettleElapsed;
    }
}
