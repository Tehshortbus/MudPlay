using MudPlay.Terminal;

namespace MudPlay.Services;

// Central pattern-bus that every line-aware subsystem subscribes to.
// Producers call Dispatch with an emitted line; the router evaluates every
// registered pattern and fires the matching subscribers' handlers — fan-out
// semantics: every matching pattern fires, in priority order.
//
// Threading: handlers run synchronously on the dispatching thread. In
// production that's the UI thread (the upstream LineExtractor already lives
// there); long-running handler work must offload via Task.Run.
//
// Registration returns an IDisposable token; subscribers dispose it to stop
// receiving callbacks (typical pattern for short-lived VM lifetimes that bind
// to long-lived services).
public sealed class MessageRouter
{
    // TieBreak orders subscribers of the SAME pattern (same Pattern.Priority) —
    // higher runs first. Default 0. Almost every subscriber leaves it 0 (order
    // among same-pattern peers doesn't matter); it exists for the rare case where
    // one same-pattern handler must observe a line before another (e.g.
    // RoundDamageTracker must close the round on *Combat Off* before CombatManager
    // reads the round count to tally MaxCasts — report paradigm-20260820-063541).
    private sealed record Subscription(IMessagePattern Pattern, Action<MatchResult> Handler, int TieBreak = 0);

    private readonly List<Subscription> _subs = new();

    // Fires once per Dispatch with the line being routed, before pattern
    // matching. Subscribers that need to track recent lines as raw text (e.g.
    // ChatRouter correlating a telepath-sent confirmation with the user's
    // preceding "/X message" command) hook here instead of registering a
    // catch-all pattern.
    public event Action<Terminal.LineExtractor.EmittedLine>? LineDispatched;

    // Known patterns indexed by id. Populated by callers via RegisterPattern
    // (typically the DefaultPatterns.Seed bootstrap). Consumers query this
    // catalog through TryGetPattern or subscribe to a known id via Subscribe.
    private readonly Dictionary<string, IMessagePattern> _catalog = new();

    // Add pattern to the known-patterns catalog so later subscribers can find
    // it by id via Subscribe or TryGetPattern. Does NOT register a
    // subscription — no handler fires until something calls Subscribe. Throws
    // InvalidOperationException when a pattern with the same id is already in
    // the catalog.
    public void RegisterPattern(IMessagePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (_catalog.ContainsKey(pattern.Id))
            throw new InvalidOperationException($"Pattern id '{pattern.Id}' is already in the catalog.");
        _catalog.Add(pattern.Id, pattern);
    }

    // Look up a previously-registered pattern by id.
    public bool TryGetPattern(string id, out IMessagePattern pattern)
    {
        bool ok = _catalog.TryGetValue(id, out IMessagePattern? p);
        pattern = p!;
        return ok;
    }

    // Diagnostic: how many patterns are in the known-patterns catalog.
    public int PatternCount => _catalog.Count;

    // Read-only probe: would ANY catalog pattern recognize line, without
    // actually dispatching it. Used by Game.MessageCandidateWatcher to exclude
    // already-known line shapes (chat, combat-round text, movement, etc.) from
    // its unrecognized-message capture — deliberately side-effect-free, so it
    // must never call Dispatch or fire LineDispatched (that would double-fire
    // every registered handler for a line the caller is only inspecting).
    public bool AnyPatternMatches(LineExtractor.EmittedLine line)
    {
        foreach (IMessagePattern pattern in _catalog.Values)
            if (pattern.TryMatch(line, out _)) return true;
        return false;
    }

    // Register handler to be invoked whenever pattern matches a dispatched
    // line. Disposing the returned token un-subscribes.
    public IDisposable Register(IMessagePattern pattern, Action<MatchResult> handler, int tieBreak = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        Subscription sub = new(pattern, handler, tieBreak);
        _subs.Add(sub);
        return new SubscriptionToken(this, sub);
    }

    // Subscribe by the id of a pattern already in the catalog (via
    // RegisterPattern). Equivalent to Register with the looked-up pattern.
    // Throws InvalidOperationException when no pattern with the given id is in
    // the catalog.
    public IDisposable Subscribe(string id, Action<MatchResult> handler, int tieBreak = 0)
    {
        if (!TryGetPattern(id, out IMessagePattern pattern))
            throw new InvalidOperationException(
                $"No catalog pattern with id '{id}' — has DefaultPatterns.Seed run?");
        return Register(pattern, handler, tieBreak);
    }

    // Evaluate every registered pattern against line and invoke matching
    // handlers in descending priority order. Subscribers that throw bubble up
    // — wrap in try/catch at the subscriber if the failure shouldn't cancel
    // the rest of the fan-out.
    public void Dispatch(LineExtractor.EmittedLine line)
    {
        LineDispatched?.Invoke(line);

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

        // Descending pattern priority, then descending per-subscription TieBreak so a
        // handler that must observe a line before another same-pattern peer (higher
        // TieBreak) runs first — deterministic where the default equal-priority order
        // is not.
        hits.Sort(static (a, b) =>
        {
            int byPattern = b.Sub.Pattern.Priority.CompareTo(a.Sub.Pattern.Priority);
            return byPattern != 0 ? byPattern : b.Sub.TieBreak.CompareTo(a.Sub.TieBreak);
        });
        foreach ((Subscription sub, MatchResult result) in hits)
        {
            sub.Handler(result);
        }
    }

    // Diagnostic: how many active subscriptions are registered.
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
