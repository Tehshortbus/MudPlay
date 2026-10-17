using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// On-demand party-level probe. Asks the whole party "what level are you?" by
// broadcasting @level to every non-self member and aggregating the replies into a
// single PartyLevelResult. Backs the leader-side level gate: PartyLevelTracker
// reads the recorded levels for MovementFilter, which routes a following party
// around a (Level: MIN to MAX) gate a member can't clear, and asks again through
// here when a level the route depends on is stale (the walker's own IRoomFilter
// already guarantees the leader clears it, so only the followers need checking).
//
// The round-trip rides the same plumbing as PartyInventoryProbe: the query goes
// out through PartyBroadcaster.Broadcast (one /<given> @level per member) and each
// member's ExperienceQueryHandler.OnLevel replies "Level N, X exp, Y to next
// level" back over an incoming telepath. We subscribe to
// ChatRouter.EntryClassified and read the level off each reply.
//
// Unlike @have there is no per-query parameter to echo, so a level reply from a
// member satisfies every in-flight query still awaiting that member. Concurrent
// queries are rare (one per level-gated walk) and simply share replies — the same
// behaviour the item probe documents for two queries using the same item name. A
// member that answers "level unknown …" (hasn't parsed a stat screen) counts as
// replied but contributes no level, so the gate can't check them; a member who
// never answers falls off with the QueryWindow. With no one to ask the task
// completes synchronously with an empty result.
//
// Every parsed level is also the authoritative record of that player's level: the
// probe forwards it to the recordLevel seam (bound in production to
// PlayerDatabase.RecordLevel), so the exact number supersedes the 5-level band
// the player's title otherwise implies. This is the one place the app learns a
// party member's precise level.
public sealed partial class PartyLevelProbe : IDisposable
{
    // Aggregate outcome of one @level round-trip. Replied counts members who
    // answered before completion (incl. "level unknown"). LevelsByMember is the
    // per-member level keyed by given name (case-insensitive); only members that
    // reported a numeric level appear, a non-responder or "level unknown" reply is
    // absent.
    public readonly record struct PartyLevelResult(
        int Expected,
        int Replied,
        IReadOnlyDictionary<string, int> LevelsByMember)
    {
        private static readonly IReadOnlyDictionary<string, int> NoLevels =
            new Dictionary<string, int>();

        // An empty result — no party members to ask, or the probe was disposed.
        public static PartyLevelResult Empty => new(0, 0, NoLevels);
    }

    private sealed class PendingQuery
    {
        public int Expected;
        public bool Completed;
        public readonly HashSet<string> Remaining = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Levels = new(StringComparer.OrdinalIgnoreCase);
        public readonly TaskCompletionSource<PartyLevelResult> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly PartyBroadcaster _broadcaster;
    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly Action<Action> _armWindow;
    private readonly Action<string, int>? _recordLevel;
    private readonly LogService? _log;
    private readonly List<PendingQuery> _pending = new();

    // For a bug report: what is asked and not yet answered, or null.
    public string? DescribePending() =>
        _pending.Count == 0 ? null : $"{_pending.Count} @level query(ies) asked of party members, not yet answered";
    private bool _disposed;

    // How long to wait for replies before completing with whatever arrived.
    // Short — the round-trip is a single telepath each way. 4 s tolerates a
    // laggy BBS without stalling the walk check.
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(4);

    // Require the full "Level N, X exp," shape (not just a leading "Level N")
    // so a member's chatter that happens to start "Level 5, …" can't be
    // misread as a level reply while a probe is open. The optional leading `{`
    // tolerates the brace-wrap the engine adds at SendReply time, so a reply
    // from another MudPlay client ("{Level 12, …}") parses too — without it a
    // peer-to-peer @level round-trip never recorded.
    [GeneratedRegex(@"^\{?Level (\d+), ([\d,]+) exp\b(?:, ([\d,]+) to next level)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LevelReply();

    // MegaMUD's @level reply: "{Level: 1  Needed: 1,000  Will level in: ?}" — Needed
    // is the exp still to go, "Will level in" its own time estimate at its own rate
    // ("?" until it has one). Once it counts past the next level the figure carries a
    // "(L60)" tag and the estimate reads "+2 in 2-3 days". Anchored on "Level: N
    // Needed: N" for the same chatter guard as the MudPlay shape.
    [GeneratedRegex(@"^\{?Level:\s*(\d+)\s+Needed:\s*([\d,]+)(?:\s*\(L(\d+)[^)]*\))?(?:\s+Will level in:\s*([^}]*?))?\s*\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MegaMudLevelReply();

    // An @exp reply — MudPlay's "Made: N  Needed: N (L2, +0.38 lvls)  Rate: … Will level
    // in: 35m" or MegaMUD's "Made: N  Needed: N  Rate: ? k/hr  Will level in: ?". Its
    // Needed is exp to the next level; MudPlay's "(L<n>)" tag names that level.
    [GeneratedRegex(@"^\{?Made:\s*[\d,]+\s+Needed:\s*([\d,]+)(?:\s*\(L(\d+)[^)]*\))?.*?(?:Will level in:\s*([^}]*?))?\s*\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExpReply();

    // Raised for every parsed level reply (probe in flight or not): the member's
    // given name, level, exp still needed for the next level (null when the reply
    // didn't carry it), the sender's own "will level in" text (MegaMUD only; null
    // otherwise or when it's "?"), its total exp (MudPlay's reply only) and the level
    // the needed figure counts toward when the reply names one in its "(L<n>)" tag (0
    // when it doesn't). An @exp reply states no level, so there the tag is only a hint
    // at one. Feeds the Party window's leveling line.
    public event Action<string, int, long?, string?, long?, int>? ProgressObserved;

    [GeneratedRegex(@"^\{?level unknown\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LevelUnknownReply();

    public PartyLevelProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party,
        Action<string, int>? recordLevel = null, LogService? log = null)
        : this(broadcaster, chat, party, armWindow: null, recordLevel: recordLevel, log: log) { }

    internal PartyLevelProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party,
        Action<Action>? armWindow = null, Action<string, int>? recordLevel = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        _broadcaster = broadcaster;
        _chat = chat;
        _party = party;
        _recordLevel = recordLevel;
        _log = log;
        _armWindow = armWindow ?? DefaultArmWindow;
        _chat.EntryClassified += OnChatEntry;
    }

    // Broadcast @level to the party and complete once every member replies or
    // QueryWindow elapses. Returns an empty result immediately when the party
    // has no one else to ask.
    public Task<PartyLevelResult> QueryAsync()
    {
        if (_disposed) return Task.FromResult(PartyLevelResult.Empty);

        PendingQuery pending = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            // An invited-not-joined member can't reply to @level (they're not in
            // the party yet), so don't count them as an expected replier — else
            // the window always waits the full QueryWindow for a reply that can't
            // come. The broadcast below skips them for the same reason.
            if (m.IsInvited) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            pending.Remaining.Add(GivenName(m.Name));
        }
        pending.Expected = pending.Remaining.Count;

        if (pending.Expected == 0)
            return Task.FromResult(PartyLevelResult.Empty);

        _pending.Add(pending);
        _broadcaster.Broadcast("@level", skipInvited: true);
        _armWindow(() => Complete(pending));
        return pending.Tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
        foreach (PendingQuery p in _pending.ToArray())
        {
            p.Completed = true;
            p.Tcs.TrySetResult(PartyLevelResult.Empty);
        }
        _pending.Clear();
    }

    private void OnChatEntry(ChatLogEntry entry)
    {
        // Telepath, or a DIRECTED say — `.@level` asks over say and the reply comes back
        // `Raijin says (to you) "{Level 1, …}"`. Either way it's the speaker's own level.
        bool directedSay = entry.Channel == ChatChannel.Local && entry.DirectedTo is not null;
        if (entry.Channel != ChatChannel.TelepathIncoming && !directedSay) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;

        // An @exp reply isn't a level reading for the probe, but its exp-to-next still
        // feeds the Party window. It never states a level: its tag names the level the
        // exp figure counts toward, which is past the member's own once they have exp
        // banked to train, so it goes along as a target, never as the member's level.
        if (TryParseExpReply(entry.Message, out int expToLevel, out long expNeeded, out string? expEta))
        {
            ProgressObserved?.Invoke(GivenName(entry.Speaker), 0, expNeeded, expEta, null, expToLevel);
            return;
        }

        bool known = TryParseLevelReply(entry.Message, out int level, out long? needed, out string? willLevelIn, out long? totalExp, out int toLevel);
        bool unknown = !known && LevelUnknownReply().IsMatch(entry.Message);
        if (!known && !unknown) return;

        string given = GivenName(entry.Speaker);

        // A parsed level is authoritative for this player — persist it to the
        // players table even when no probe is still awaiting them, so a reply
        // that lands after the query window closed (a slow telepath round-trip)
        // still supersedes the title-derived band. Previously this ran only
        // while a probe was in flight, so a late reply was dropped and a narrow
        // level gate (e.g. "10 to 10") stayed stuck on the coarse title range.
        if (known)
        {
            _recordLevel?.Invoke(given, level);
            _log?.Info("PartyLevel", $"recorded {given} = level {level}");
            ProgressObserved?.Invoke(given, level, needed, willLevelIn, totalExp, toLevel);
        }

        // Pending-query bookkeeping only applies while a probe is awaiting
        // replies; a level that arrives outside a window is still recorded above.
        if (_pending.Count == 0) return;

        // Snapshot: Complete mutates _pending. One reply can satisfy several
        // in-flight queries (no echoed parameter to disambiguate); recording
        // the level for each still-awaiting query is correct.
        foreach (PendingQuery p in _pending.ToArray())
        {
            if (p.Completed) continue;
            if (!p.Remaining.Remove(given)) continue;   // not expected / already counted
            if (known) p.Levels[given] = level;         // "unknown" → replied but unrecorded
            if (p.Remaining.Count == 0) Complete(p);
        }
    }

    private void Complete(PendingQuery p)
    {
        if (p.Completed) return;
        p.Completed = true;
        _pending.Remove(p);

        int replied = p.Expected - p.Remaining.Count;
        p.Tcs.TrySetResult(new PartyLevelResult(
            p.Expected, replied,
            new Dictionary<string, int>(p.Levels, StringComparer.OrdinalIgnoreCase)));
        _log?.Info("PartyLevel",
            $"@level — {replied}/{p.Expected} replied, {p.Levels.Count} levels known.");
    }

    private void DefaultArmWindow(Action onElapsed)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = QueryWindow };
        timer.Tick += (_, _) => { timer.Stop(); onElapsed(); };
        timer.Start();
    }

    // Both reply shapes: MudPlay's "Level N, X exp, Y to next level" and MegaMUD's
    // "Level: N  Needed: Y  Will level in: T". toLevel is the level the needed figure
    // counts toward, from MegaMUD's "(L<n>)" tag; 0 for MudPlay's shape or no tag.
    internal static bool TryParseLevelReply(
        string message, out int level, out long? needed, out string? willLevelIn, out long? totalExp, out int toLevel)
    {
        toLevel = 0;
        needed = null;
        willLevelIn = null;
        totalExp = null;
        string text = message.Trim();
        Match m = LevelReply().Match(text);
        if (m.Success)
        {
            level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            totalExp = ParseCount(m.Groups[2].Value);
            if (m.Groups[3].Success) needed = ParseCount(m.Groups[3].Value);
            return true;
        }
        m = MegaMudLevelReply().Match(text);
        if (m.Success)
        {
            level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            needed = ParseCount(m.Groups[2].Value);
            if (m.Groups[3].Success) toLevel = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            string eta = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "";
            willLevelIn = eta.Length == 0 || eta == "?" ? null : eta;
            return true;
        }
        level = 0;
        return false;
    }

    // toLevel is the "(L<n>)" tag's number — the level the needed figure counts toward
    // (0 when the reply has none).
    internal static bool TryParseExpReply(string message, out int toLevel, out long needed, out string? willLevelIn)
    {
        toLevel = 0;
        needed = 0;
        willLevelIn = null;
        Match m = ExpReply().Match(message.Trim());
        if (!m.Success || ParseCount(m.Groups[1].Value) is not { } n) return false;
        needed = n;
        if (m.Groups[2].Success)
            int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out toLevel);
        string eta = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";
        willLevelIn = eta.Length == 0 || eta == "?" ? null : eta;
        return true;
    }

    private static long? ParseCount(string text) =>
        long.TryParse(text.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
