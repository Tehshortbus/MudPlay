using System.Text;
using System.Threading.Tasks;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Turns the passive SysRoomStatusParser into a request/response: send a sysop
// status and await the room block. Callers (location recovery, Roomba recon)
// want to ask, not to overhear. Same split as AbilBreakdownParser (parses) and
// ManaRegenReroller (sends `abil 145` and consumes the result).
//
// Capability is two-layered, because sysop access can't be detected reliably:
//
//   1. BbsCredentials.HasSysopPowers — the character's existing per-BBS
//      "I have sysop / goto powers" flag. Default off; while it is, nothing is
//      ever sent.
//   2. Auto-disable — a probe that produces no parseable block within the
//      timeout switches the capability off for the rest of the session. This is
//      deliberately NOT a match on a denial message: the wording a denied sysop
//      command produces is unconfirmed, and a timeout is the right answer
//      whatever the server said. Every caller treats a null result as normal and
//      falls back to what it did before.
//
// Only the no-argument form is implemented. `sysop status room <n>` would let us
// read any room without travelling (boss presence, for one), but the game's help
// describes its argument as a bare room number while rooms are map/room pairs
// here, and that wire format is unconfirmed — so it waits for its own design
// rather than being guessed at.
public sealed class SysStatusProbe : IDisposable
{
    private const string LogCategory = "SysStatus";
    private const string StatusCommand = "sys st";

    private readonly SysRoomStatusParser _parser;
    private readonly Func<bool> _capabilityEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private TaskCompletionSource<SysRoomStatus?>? _pending;
    private bool _disposed;

    // How long to wait for the block before giving up and auto-disabling.
    // Generous: the dump is several lines and a busy BBS can be slow.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(6);

    // Test seam — lets a test drive the timeout without real time.
    public Func<TimeSpan, Task> DelayProvider { get; set; } = Task.Delay;

    // True once a probe timed out this session. Cleared by ResetAutoDisable.
    public bool AutoDisabled { get; private set; }

    // Whether a probe would actually be sent right now.
    public bool Available => !_disposed && !AutoDisabled && _wireSender is not null && _capabilityEnabled();

    public SysStatusProbe(SysRoomStatusParser parser, Func<bool> capabilityEnabled, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(capabilityEnabled);
        _parser = parser;
        _capabilityEnabled = capabilityEnabled;
        _log = log;
        _parser.StatusParsed += OnStatusParsed;
    }

    // Bind the wire-sender. MainWindowViewModel supplies SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Clear a session's auto-disable — called on profile load, so switching
    // characters doesn't inherit a previous session's failed probe.
    public void ResetAutoDisable()
    {
        if (!AutoDisabled) return;
        AutoDisabled = false;
        _log?.Log(LogSeverity.Info, LogCategory, "Auto-disable cleared.");
    }

    // Send a sysop status for the current room and await the parsed block.
    // Returns null when the capability is off, a probe is already in flight and
    // its own wait times out, or nothing parseable came back. Null is an
    // ordinary outcome; callers fall through to their existing behaviour.
    public async Task<SysRoomStatus?> QueryAsync()
    {
        if (!Available)
        {
            _log?.Log(LogSeverity.Debug, LogCategory, "Probe skipped — capability unavailable.");
            return null;
        }

        // One probe at a time — the wire serialises anyway, and a second
        // in-flight send would race two blocks onto one completion source.
        if (_pending is { Task: { IsCompleted: false } inFlight })
            return await inFlight.ConfigureAwait(false);

        TaskCompletionSource<SysRoomStatus?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        _log?.Log(LogSeverity.Info, LogCategory, $"Probing with `{StatusCommand}`.");
        _wireSender?.Invoke(Encoding.Latin1.GetBytes(StatusCommand + "\r\n"));

        Task completed = await Task.WhenAny(tcs.Task, DelayProvider(Timeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            _pending = null;
            AutoDisabled = true;
            _log?.Log(LogSeverity.Info, LogCategory,
                $"No room block within {Timeout.TotalSeconds:0}s — sysop status auto-disabled for this session.");
            return null;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private void OnStatusParsed(SysRoomStatus status)
    {
        TaskCompletionSource<SysRoomStatus?>? pending = _pending;
        _pending = null;
        pending?.TrySetResult(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.StatusParsed -= OnStatusParsed;
        _pending?.TrySetResult(null);
        _pending = null;
    }
}
