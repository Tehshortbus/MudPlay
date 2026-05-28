using FujinTerm.Terminal;

namespace FujinTerm.Services;

/// <summary>
/// Central pattern-bus that every line-aware subsystem subscribes to.
/// Producers call <see cref="Dispatch"/> with an emitted line; the router
/// evaluates every registered pattern and fires the matching subscribers'
/// handlers — fan-out semantics: <i>every</i> matching pattern fires, in
/// priority order.
/// </summary>
/// <remarks>
/// <para>
/// Threading: handlers run synchronously on the dispatching thread.
/// In production that's the UI thread (the upstream
/// <see cref="LineExtractor"/> already lives there); long-running handler
/// work must offload via <see cref="Task.Run"/>.
/// </para>
/// <para>
/// Registration returns an <see cref="IDisposable"/> token; subscribers
/// dispose it to stop receiving callbacks (typical pattern for short-lived
/// VM lifetimes that bind to long-lived services).
/// </para>
/// </remarks>
public sealed class MessageRouter
{
    private sealed record Subscription(IMessagePattern Pattern, Action<MatchResult> Handler);

    private readonly List<Subscription> _subs = new();

    /// <summary>
    /// Register <paramref name="handler"/> to be invoked whenever
    /// <paramref name="pattern"/> matches a dispatched line. Disposing the
    /// returned token un-subscribes.
    /// </summary>
    public IDisposable Register(IMessagePattern pattern, Action<MatchResult> handler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        Subscription sub = new(pattern, handler);
        _subs.Add(sub);
        return new SubscriptionToken(this, sub);
    }

    /// <summary>
    /// Evaluate every registered pattern against <paramref name="line"/> and
    /// invoke matching handlers in descending priority order. Subscribers
    /// that throw bubble up — wrap in <c>try/catch</c> at the subscriber
    /// if the failure shouldn't cancel the rest of the fan-out.
    /// </summary>
    public void Dispatch(LineExtractor.EmittedLine line)
    {
        // Snapshot the matching set first so a handler that calls Register
        // (or disposes its own token) during dispatch doesn't mutate the
        // list we're iterating.
        List<(Subscription Sub, MatchResult Result)>? hits = null;

        foreach (Subscription sub in _subs)
        {
            if (sub.Pattern.TryMatch(line, out MatchResult result))
            {
                hits ??= new List<(Subscription, MatchResult)>();
                hits.Add((sub, result));
            }
        }

        if (hits is null) return;

        hits.Sort(static (a, b) => b.Sub.Pattern.Priority.CompareTo(a.Sub.Pattern.Priority));
        foreach ((Subscription sub, MatchResult result) in hits)
        {
            sub.Handler(result);
        }
    }

    /// <summary>Diagnostic: how many active subscriptions are registered.</summary>
    public int SubscriptionCount => _subs.Count;

    private sealed class SubscriptionToken(MessageRouter owner, Subscription sub) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._subs.Remove(sub);
        }
    }
}
