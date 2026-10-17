using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// On-demand party-wealth probe. Asks the whole party "how much are you
// carrying?" by broadcasting @wealth to every non-self member and aggregating
// the replies into a single PartyWealthResult. Backs the leader-side toll-gate
// check: a (Toll: N) exit is per-crosser — every member needs N*100 copper-value
// on hand — so before stepping the party through one the leader asks what every
// follower holds. What is done about a member who is short or doesn't reply is
// PartyTollGate's: paid for when the Cash setting allows it and we can spare it,
// the toll not taken otherwise. An NPC's fare and a sailing are asked about the same way.
//
// Same round-trip plumbing as PartyLevelProbe: the query goes out through
// PartyBroadcaster.Broadcast (one /<given> @wealth per member) and each member's
// InventoryQueryHandler.OnWealth replies over an incoming telepath. We subscribe
// to ChatRouter.EntryClassified and read the copper value off each reply.
//
// Reply shapes, mutually exclusive (our own client — see InventoryQueryHandler.OnWealth):
//   - "<coins> (= N copper)"  → carrying coin worth N copper.
//   - "no coins on hand"      → a real empty purse, worth 0 (still gates any toll).
//   - "wealth unknown - …"    → hasn't parsed inventory yet; counts as replied but
//                               carries no value, so the gate can't clear them.
// A party member on a DIFFERENT client answers in that client's own format, which
// carries no "(= N copper)" tally — observed once as "{Wealth: 26 platinum pieces,
// 4792 gold crowns}". We fold that in by scanning for "<count> <denomination>" coin
// phrases and summing via the standard ratio ladder.
// A member that answers "unknown" or never answers contributes no reading: for a
// toll the tracker notes them silent, which counts as unable to pay (user,
// 2026-10-10); for a fare they are simply not counted.
//
// Unlike level, wealth is never persisted to the players table: it drifts
// constantly with loot / spend, so the probe forwards each fresh reading to the
// recordWealth seam (bound to PartyWealthTracker.Record) which timestamps it and
// lets it go stale, rather than caching it as a stable attribute.
public sealed partial class PartyWealthProbe : IDisposable
{
    // Aggregate outcome of one @wealth round-trip. Replied counts members who
    // answered before completion (incl. "wealth unknown"). WealthByMember is the
    // per-member copper value keyed by given name (case-insensitive); only members
    // that reported a usable figure (coins or a real empty purse) appear — a
    // non-responder or "unknown" reply is absent.
    public readonly record struct PartyWealthResult(
        int Expected,
        int Replied,
        IReadOnlyDictionary<string, long> WealthByMember)
    {
        private static readonly IReadOnlyDictionary<string, long> NoWealth =
            new Dictionary<string, long>();

        // An empty result — no party members to ask, or the probe was disposed.
        public static PartyWealthResult Empty => new(0, 0, NoWealth);
    }

    private sealed class PendingQuery
    {
        public int Expected;
        public bool Completed;
        public readonly HashSet<string> Remaining = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> Wealth = new(StringComparer.OrdinalIgnoreCase);
        public readonly TaskCompletionSource<PartyWealthResult> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Told the result where the query completes, on the thread it completes on.
        public Action<PartyWealthResult>? OnComplete;
    }

    private readonly PartyBroadcaster _broadcaster;
    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly Action<Action> _armWindow;
    private readonly Action<string, long>? _recordWealth;
    private readonly LogService? _log;
    private readonly List<PendingQuery> _pending = new();

    // For a bug report: what is asked and not yet answered, or null.
    public string? DescribePending() =>
        _pending.Count == 0 ? null : $"{_pending.Count} wealth query(ies) asked of party members, not yet answered";
    private bool _disposed;

    // How long to wait for replies before completing with whatever arrived.
    // Short — the round-trip is a single telepath each way. 4 s tolerates a
    // laggy BBS without stalling the walk check.
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(4);

    // The consolidated "(= N copper)" tail InventoryQueryHandler.OnWealth appends
    // to a loaded reply. N is emitted with ":N0" thousands-commas, so allow
    // digits + commas and strip them before parsing. Anchored on the literal
    // "(= … copper)" so the coin prefix (which also holds digits) can't be
    // misread as the value.
    [GeneratedRegex(@"\(= ([\d,]+) copper\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WealthReply();

    // A loaded reply for an empty purse — a real zero, distinct from "wealth
    // unknown". The handler emits the bare "no coins on hand" (no "(= …)" tail)
    // in this case.
    [GeneratedRegex(@"^no coins on hand\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmptyPurseReply();

    [GeneratedRegex(@"^wealth unknown\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WealthUnknownReply();

    // Fallback for a party member on another client whose @wealth reply lacks our
    // "(= N copper)" tally — matches each "<count> <denomination>" coin phrase (e.g.
    // "26 platinum", "4,792 gold") so mixed streams like "{Wealth: 26 platinum
    // pieces, 4792 gold crowns}" fold to copper. The denomination noun is the
    // canonical metal word CurrencyHoldings.ToCopper keys on; any trailing coin name
    // ("pieces", "crowns") is ignored.
    [GeneratedRegex(@"([\d,]+)\s+(copper|silver|gold|platinum|runic)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForeignCoinPhrase();

    // The shape of another client's answer: its "Wealth:" label, with or without
    // the brace that client wraps a reply in.
    [GeneratedRegex(@"^\s*\{?\s*Wealth\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForeignWealthReply();

    public PartyWealthProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party,
        Action<string, long>? recordWealth = null, LogService? log = null)
        : this(broadcaster, chat, party, armWindow: null, recordWealth: recordWealth, log: log) { }

    internal PartyWealthProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party,
        Action<Action>? armWindow = null, Action<string, long>? recordWealth = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        _broadcaster = broadcaster;
        _chat = chat;
        _party = party;
        _recordWealth = recordWealth;
        _log = log;
        _armWindow = armWindow ?? DefaultArmWindow;
        _chat.EntryClassified += OnChatEntry;
    }

    // Broadcast @wealth to the party and complete once every member replies or
    // QueryWindow elapses. Returns an empty result immediately when the party
    // has no one else to ask.
    public Task<PartyWealthResult> QueryAsync() => Begin(onComplete: null);

    // The same round-trip for a caller that must act on the answer where it lands
    // (the toll check holds a walk's step for it): onComplete runs once, when every
    // member has replied or QueryWindow elapses, and at once when there is nobody
    // to ask.
    public void Query(Action<PartyWealthResult> onComplete)
    {
        ArgumentNullException.ThrowIfNull(onComplete);
        Begin(onComplete);
    }

    private Task<PartyWealthResult> Begin(Action<PartyWealthResult>? onComplete)
    {
        if (_disposed)
        {
            onComplete?.Invoke(PartyWealthResult.Empty);
            return Task.FromResult(PartyWealthResult.Empty);
        }

        PendingQuery pending = new() { OnComplete = onComplete };
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            pending.Remaining.Add(GivenName(m.Name));
        }
        pending.Expected = pending.Remaining.Count;

        if (pending.Expected == 0)
        {
            onComplete?.Invoke(PartyWealthResult.Empty);
            return Task.FromResult(PartyWealthResult.Empty);
        }

        _pending.Add(pending);
        _broadcaster.Broadcast("@wealth");
        _armWindow(() => Complete(pending));
        return pending.Tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
        PendingQuery[] open = _pending.ToArray();
        _pending.Clear();
        foreach (PendingQuery p in open)
        {
            p.Completed = true;
            p.Tcs.TrySetResult(PartyWealthResult.Empty);
            p.OnComplete?.Invoke(PartyWealthResult.Empty);
        }
    }

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (_pending.Count == 0) return;
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        // A `@loop send` line names a loop and an @events one an event, and either
        // can be named "(= 5 copper)": read as a purse, that would be a member with
        // five copper, and the toll funding hands coin over on these readings.
        if (UserNamedReply.IsMessage(entry.Message)) return;

        bool known = TryParseWealthReply(entry.Message, out long copper);
        bool unknown = !known && WealthUnknownReply().IsMatch(entry.Message);
        if (!known && !unknown) return;

        string given = GivenName(entry.Speaker);

        // Record what we made of each reply — name, interpreted copper value (or
        // "unknown"), and the verbatim reply — so a reader can confirm the parse,
        // especially the fragile foreign-client coin-phrase fallback.
        _log?.Info("PartyWealth", known
            ? $"{given}: interpreted {copper:N0} copper from reply \"{entry.Message}\""
            : $"{given}: wealth unknown (reply \"{entry.Message}\")");

        // Forward each fresh reading to the tracker regardless of which
        // in-flight query (if any) still awaits them; one reply can satisfy
        // several concurrent queries (no echoed parameter to disambiguate).
        if (known) _recordWealth?.Invoke(given, copper);

        // Snapshot: Complete mutates _pending.
        foreach (PendingQuery p in _pending.ToArray())
        {
            if (p.Completed) continue;
            if (!p.Remaining.Remove(given)) continue;   // not expected / already counted
            if (known) p.Wealth[given] = copper;         // "unknown" → replied but unrecorded
            if (p.Remaining.Count == 0) Complete(p);
        }
    }

    private void Complete(PendingQuery p)
    {
        if (p.Completed) return;
        p.Completed = true;
        _pending.Remove(p);

        int replied = p.Expected - p.Remaining.Count;
        PartyWealthResult result = new(
            p.Expected, replied,
            new Dictionary<string, long>(p.Wealth, StringComparer.OrdinalIgnoreCase));
        p.Tcs.TrySetResult(result);
        _log?.Info("PartyWealth",
            $"@wealth — {replied}/{p.Expected} replied, {p.Wealth.Count} wallets known.");
        p.OnComplete?.Invoke(result);
    }

    private void DefaultArmWindow(Action onElapsed)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = QueryWindow };
        timer.Tick += (_, _) => { timer.Stop(); onElapsed(); };
        timer.Start();
    }

    private static bool TryParseWealthReply(string message, out long copper)
    {
        Match m = WealthReply().Match(message);
        if (m.Success)
        {
            copper = long.Parse(
                m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
            return true;
        }
        if (EmptyPurseReply().IsMatch(message))
        {
            copper = 0;   // real empty purse — still gates any positive toll
            return true;
        }

        // Foreign-client fallback: sum every "<count> <denomination>" coin phrase,
        // in a message shaped like the one such answer on record ("{Wealth: …}").
        // A telepath that merely mentions coins ("can you spare 2 gold for the
        // toll") is talk, not an answer: read as a purse, it drew a hand-over.
        // Only treat as a reading when at least one phrase parses.
        if (!ForeignWealthReply().IsMatch(message))
        {
            copper = 0;
            return false;
        }
        long total = 0;
        bool any = false;
        foreach (Match cm in ForeignCoinPhrase().Matches(message))
        {
            if (!long.TryParse(
                    cm.Groups[1].Value.Replace(",", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
                continue;
            total += CurrencyHoldings.ToCopper(cm.Groups[2].Value, count);
            any = true;
        }
        if (any)
        {
            copper = total;
            return true;
        }

        copper = 0;
        return false;
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
