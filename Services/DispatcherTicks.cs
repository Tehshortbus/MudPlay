using Avalonia.Threading;

namespace MudPlay.Services;

// The dispatcher-backed default behind the schedulers components take as an
// optional constructor parameter (a Func<TimeSpan, Action, IDisposable>): the tick
// runs once on the UI thread after the delay, and disposing the handle first
// cancels it. A component that ticks repeatedly arms the next tick from inside the
// one running.
public static class DispatcherTicks
{
    public static IDisposable Once(TimeSpan delay, Action tick) =>
        DispatcherTimer.RunOnce(tick, delay, DispatcherPriority.Background);
}
