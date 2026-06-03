using System.Text;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Drives the walker's <c>sea &lt;dir&gt;</c> retry loop for
/// <see cref="RoomExitHint.SearchableHidden"/> exits. Mirrors
/// <see cref="DoorOpenManager"/>'s shape — one request in flight,
/// FIFO queue, single terminal <see cref="HiddenSearchResult"/>
/// callback per request — but with a different revelation signal:
/// the watcher subscribes to <see cref="RoomTracker.StateChanged"/>
/// and inspects the new current room's exit map for the searched
/// direction. When the direction appears, the exit is "revealed"
/// and the manager fires <see cref="HiddenSearchResult.Revealed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per user direction this is the targeted reveal loop (separate
/// from the future no-arg-<c>sea</c> auto-search-room feature). The
/// attempt cap reads live from Settings.Other.MaxHiddenSearchAttempts
/// on each retry so the user can tune mid-session.
/// </para>
/// </remarks>
public sealed class HiddenExitRevealManager : IDisposable
{
    private readonly RoomTracker _tracker;
    private readonly Func<int> _maxAttemptsProvider;
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    private readonly Queue<HiddenRequest> _queue = new();
    private HiddenRequest? _current;
    private int _attempts;

    /// <summary>Direction of the in-flight request, or null when idle.</summary>
    public string? CurrentDirection => _current is { } cur
        ? DirectionShort(cur.Direction)
        : null;

    /// <summary>Outstanding queue depth.</summary>
    public int QueueDepth => _queue.Count;

    /// <summary>True when a search is in flight (sent sea, awaiting room obs).</summary>
    public bool IsBusy => _current is not null;

    public HiddenExitRevealManager(
        RoomTracker tracker,
        Func<int> maxAttemptsProvider,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(maxAttemptsProvider);
        _tracker = tracker;
        _maxAttemptsProvider = maxAttemptsProvider;
        _log = log;
        _tracker.StateChanged += OnTrackerStateChanged;
    }

    /// <summary>Bind the wire-sender — same shape as the rest of the engine-side handlers.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Test seam — bytes the manager asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
    }

    /// <summary>Queue a hidden-reveal request. Callback fires once on terminal state.</summary>
    public void Enqueue(
        Direction direction,
        string sender,
        Action<HiddenSearchResult> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        _queue.Enqueue(new HiddenRequest(direction, sender, reply));
        _log?.Info("Hidden",
            $"reveal {DirectionShort(direction)} queued (sender={sender}, depth={_queue.Count}).");
        TryStartNext();
    }

    /// <summary>Abort the in-flight request + drain the queue.</summary>
    public void StopAll()
    {
        if (_current is { } cur)
        {
            cur.Reply(new HiddenSearchResult.Failed("hidden search stopped"));
            _current = null;
        }
        while (_queue.Count > 0)
        {
            HiddenRequest q = _queue.Dequeue();
            q.Reply(new HiddenSearchResult.Failed("hidden search stopped"));
        }
        _attempts = 0;
        _log?.Info("Hidden", "Hidden-reveal flow stopped — queue drained.");
    }

    private void TryStartNext()
    {
        if (_current is not null) return;
        if (_queue.Count == 0) return;
        _current = _queue.Dequeue();
        _attempts = 0;
        SendSea();
    }

    private void SendSea()
    {
        if (_current is not { } cur) return;
        _attempts++;
        SendWire($"sea {DirectionShort(cur.Direction)}");
        _log?.Info("Hidden",
            $"sea {DirectionShort(cur.Direction)} (attempt {_attempts}/{_maxAttemptsProvider()}).");
    }

    private void OnTrackerStateChanged(RoomTransition transition)
    {
        if (_current is not { } cur) return;

        // Check if the searched direction now appears in the
        // tracker's current room. The trigger is broad — any state
        // change while we're in-flight prompts a re-check, including
        // a "same room redisplay after sea".
        Room? room = transition.NewRoom;
        if (room is not null && room.Exits.ContainsKey(cur.Direction))
        {
            _log?.Info("Hidden",
                $"reveal {DirectionShort(cur.Direction)} succeeded on attempt {_attempts}.");
            cur.Reply(HiddenSearchResult.Revealed.Instance);
            Reset();
            return;
        }

        // Still not visible — retry or exhaust.
        if (_attempts >= _maxAttemptsProvider())
        {
            cur.Reply(new HiddenSearchResult.Failed(
                $"exit {DirectionShort(cur.Direction)} never revealed after {_attempts} sea attempts"));
            Reset();
            return;
        }
        SendSea();
    }

    private void Reset()
    {
        _current = null;
        _attempts = 0;
        TryStartNext();
    }

    private void SendWire(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
    }

    private static string DirectionShort(Direction d) => d switch
    {
        Direction.N => "n",
        Direction.S => "s",
        Direction.E => "e",
        Direction.W => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U => "u",
        Direction.D => "d",
        _ => "?",
    };

    private sealed record HiddenRequest(
        Direction Direction,
        string Sender,
        Action<HiddenSearchResult> Reply);
}
