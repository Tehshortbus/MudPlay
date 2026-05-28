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
    /// Known patterns indexed by id. Populated by callers via
    /// <see cref="RegisterPattern"/> (typically the
    /// <see cref="Patterns.DefaultPatterns.Seed"/> bootstrap). Consumers
    /// query this catalog through <see cref="TryGetPattern"/> or subscribe
    /// to a known id via <see cref="Subscribe(string, Action{MatchResult})"/>.
    /// </summary>
    private readonly Dictionary<string, IMessagePattern> _catalog = new();

    /// <summary>
    /// Add <paramref name="pattern"/> to the known-patterns catalog so
    /// later subscribers can find it by id via <see cref="Subscribe(string, Action{MatchResult})"/>
    /// or <see cref="TryGetPattern"/>. Does NOT register a subscription —
    /// no handler fires until something calls <c>Subscribe</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a pattern with the same id is already in the catalog.
    /// </exception>
    public void RegisterPattern(IMessagePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (_catalog.ContainsKey(pattern.Id))
            throw new InvalidOperationException($"Pattern id '{pattern.Id}' is already in the catalog.");
        _catalog.Add(pattern.Id, pattern);
    }

    /// <summary>Look up a previously-registered pattern by id.</summary>
    public bool TryGetPattern(string id, out IMessagePattern pattern)
    {
        bool ok = _catalog.TryGetValue(id, out IMessagePattern? p);
        pattern = p!;
        return ok;
    }

    /// <summary>Diagnostic: how many patterns are in the known-patterns catalog.</summary>
    public int PatternCount => _catalog.Count;

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
    /// Convenience: subscribe by the id of a pattern already in the
    /// catalog (via <see cref="RegisterPattern"/>). Equivalent to
    /// <see cref="Register"/> with the looked-up pattern.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No pattern with the given id is in the catalog.
    /// </exception>
    public IDisposable Subscribe(string id, Action<MatchResult> handler)
    {
        if (!TryGetPattern(id, out IMessagePattern pattern))
            throw new InvalidOperationException(
                $"No catalog pattern with id '{id}' — has DefaultPatterns.Seed run?");
        return Register(pattern, handler);
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
