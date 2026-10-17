using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Tests;

// Stands in for the dispatcher's timers. A component given Schedule (an
// Action<TimeSpan, Action>) or Arm (a Func<TimeSpan, Action, IDisposable>) as its
// scheduler hands its ticks here, and a tick runs only when the test says the
// interval is up. Disposing what Arm returned cancels that tick, as stopping the
// timer would. Advance runs them on a clock of their own instead, each at its
// interval after it was armed.
internal sealed class ManualTimers
{
    private readonly List<Tick> _armed = new();
    private TimeSpan _elapsed;

    public void Schedule(TimeSpan delay, Action tick) => _armed.Add(new Tick(delay, _elapsed + delay, tick));

    public IDisposable Arm(TimeSpan delay, Action tick)
    {
        Tick t = new(delay, _elapsed + delay, tick);
        _armed.Add(t);
        return t;
    }

    // Time passes: every tick that comes due within it runs at its moment, earliest
    // first, those its ticks arm among them.
    public void Advance(TimeSpan by)
    {
        TimeSpan until = _elapsed + by;
        while (_armed.Where(t => !t.Cancelled && t.DueAt <= until).MinBy(t => t.DueAt) is { } next)
        {
            _armed.Remove(next);
            _elapsed = next.DueAt;
            next.Cancelled = true;
            next.Run();
        }
        _armed.RemoveAll(t => t.Cancelled);
        _elapsed = until;
    }

    // Ticks armed and neither run nor cancelled.
    public int Pending => _armed.Count(t => !t.Cancelled);

    // The intervals of the ticks still pending, in the order they were armed.
    public IReadOnlyList<TimeSpan> PendingDelays => _armed.Where(t => !t.Cancelled).Select(t => t.Delay).ToList();

    // Every tick pending now runs, oldest first; one a tick arms waits for the next call.
    public void RunAll()
    {
        List<Tick> due = _armed.Where(t => !t.Cancelled).ToList();
        _armed.Clear();
        foreach (Tick t in due)
        {
            if (t.Cancelled) continue;
            t.Cancelled = true;
            t.Run();
        }
    }

    // The ticks pending now that were armed for this interval run, oldest first: for
    // a component that keeps more than one timer, the interval tells them apart.
    public void RunDue(TimeSpan delay)
    {
        List<Tick> due = _armed.Where(t => !t.Cancelled && t.Delay == delay).ToList();
        foreach (Tick t in due)
        {
            _armed.Remove(t);
            if (t.Cancelled) continue;
            t.Cancelled = true;
            t.Run();
        }
    }

    // The oldest pending tick runs. False when nothing is pending.
    public bool RunNext()
    {
        Tick? next = _armed.FirstOrDefault(t => !t.Cancelled);
        if (next is null) return false;
        _armed.Remove(next);
        next.Cancelled = true;
        next.Run();
        return true;
    }

    private sealed class Tick(TimeSpan delay, TimeSpan dueAt, Action run) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public TimeSpan DueAt { get; } = dueAt;
        public Action Run { get; } = run;
        public bool Cancelled { get; set; }
        public void Dispose() => Cancelled = true;
    }
}
