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

    // The command the server echoed on the prompt row directly before the line
    // now being dispatched, or null when that line didn't follow an echo. A
    // generic reply such as "Your command had no effect." names nothing, so the
    // echo ahead of it is the only way to tell which command drew it: the game
    // prints "[HP=..]:swan" and then the reply. LineExtractor splits that row into
    // the prompt and its trailing text, both carrying the row's timestamp. A
    // custom statline it can't split leaves this null, and callers then treat the
    // reply as they did before.
    public string? CommandEchoedBeforeLine { get; private set; }

    // Whether the line now being dispatched is itself such an echo: the trailing
    // text of a prompt row. For a reader that has to remember an echo for longer than
    // the one line CommandEchoedBeforeLine keeps it (CommandOffProbe).
    public bool LineIsCommandEcho { get; private set; }

    private string? _echoOnPreviousLine;
    private bool _previousLineWasPrompt;
    private DateTimeOffset _previousPromptAt;

    private void TrackCommandEcho(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine)
        {
            _previousLineWasPrompt = true;
            _previousPromptAt = line.Timestamp;
            _echoOnPreviousLine = null;
            CommandEchoedBeforeLine = null;
            LineIsCommandEcho = false;
            return;
        }

        bool isEcho = _previousLineWasPrompt && line.Timestamp == _previousPromptAt;
        _previousLineWasPrompt = false;
        CommandEchoedBeforeLine = _echoOnPreviousLine;
        _echoOnPreviousLine = isEcho ? line.Text.Trim() : null;
        LineIsCommandEcho = isEcho;
    }

    // True when the line being dispatched answers an echoed command that doesn't
    // name target — the reply belongs to something else we (or the user) sent, such
    // as a between-round cast, and says nothing about target. False when no echo was
    // read, so an unattributable reply keeps its old meaning.
    public bool ReplyIsForCommandNotNaming(string target)
        => CommandEchoedBeforeLine is { Length: > 0 } echo
           && !echo.EndsWith(target, StringComparison.OrdinalIgnoreCase);

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

    // Read-only probe: would ANY catalog pattern recognize line, without
    // actually dispatching it — so a caller can tell known line shapes (chat,
    // combat-round text, movement, etc.) from unrecognized ones. Deliberately
    // side-effect-free, so it must never call Dispatch or fire LineDispatched
    // (that would double-fire every registered handler for a line the caller is
    // only inspecting).
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
        // A chat line (LineExtractor set IsChat) is another player's typed text — it
        // may only reach the conversation.* patterns that exist to parse chat. Any
        // other pattern (combat, conditions, movement, items …) and every raw
        // LineDispatched observer (combat classifier, room-entry scan, sweep …) must
        // not see it, or a player could quote server text in a gossip / broadcast and
        // trigger that handler as if the server had sent it.
        bool chatLine = line.IsChat;

        if (!chatLine) TrackCommandEcho(line);

        if (!chatLine) LineDispatched?.Invoke(line);

        // Snapshot the matching set first so a handler that calls Register
        // (or disposes its own token) during dispatch doesn't mutate the
        // list we're iterating.
        List<(Subscription Sub, MatchResult Result)>? hits = null;

        foreach (Subscription sub in _subs)
        {
            if (chatLine && !IsConversationPattern(sub.Pattern)) continue;
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

    private static bool IsConversationPattern(IMessagePattern pattern)
        => pattern.Id.StartsWith(Patterns.KnownPatterns.ConversationPrefix, StringComparison.Ordinal);

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
