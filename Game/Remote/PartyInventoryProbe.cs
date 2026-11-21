using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// On-demand party-inventory probe. Asks the whole party "do you have this item,
// and how many?" by broadcasting @have <item> to every non-self member and
// aggregating their replies into a single PartyItemResult. Backs the party stage
// of the path-item pipeline: when a walk-route crosses an
// (Item: N) / (Ticket: N) gate whose per-member item we lack, PartyPathItemGate
// probes the party first and — if a member has a spare — has them hand it over
// instead of posting a search / shop need.
//
// The round-trip rides the existing remote-command plumbing: the query goes out
// through PartyBroadcaster.Broadcast (one /<given> @have <item> per member), and
// each member's InventoryQueryHandler.OnHave replies "yes - Nx '<q>'" /
// "no - nothing matching '<q>'" back over an incoming telepath. We subscribe to
// ChatRouter.EntryClassified and correlate each reply by the echoed '<q>' item
// name, so several per-item queries can be in flight at once
// without their replies crossing wires.
//
// A query completes early once every expected responder has replied, or when the
// QueryWindow elapses. A member who never answers (offline, no QueryInventory
// grant for us, hasn't parsed inventory) adds nothing to the total and is named in
// the result's Unanswered: still a member, taken to hold none.
// The expected-responder set mirrors PartyBroadcaster's own filter (non-self,
// named, joined), so we never wait on a member the broadcast skipped. With no one to ask
// the task completes synchronously with an empty result, letting the caller fall
// straight through to its fallback.
public sealed partial class PartyInventoryProbe : IDisposable
{
    // Aggregate outcome of one @have round-trip. CountsByMember is the per-member
    // match count keyed by given name (case-insensitive); only members that
    // replied appear, a non-responder is absent, not zero.
    public readonly record struct PartyItemResult(
        int ItemId,
        int TotalCount,
        int Expected,
        int Replied,
        IReadOnlyDictionary<string, int> CountsByMember)
    {
        private static readonly IReadOnlyDictionary<string, int> NoCounts =
            new Dictionary<string, int>();

        // The joined members who were asked and never answered, by given name. They
        // are part of the party all the same, and are taken to hold none (user,
        // 2026-10-09). Every reader of a count takes its pool from here and from
        // CountsByMember, so the route card and the walk can't disagree about it.
        public IReadOnlyCollection<string> Unanswered { get; init; } = Array.Empty<string>();

        // An empty result — no party members to ask, or the probe was disposed.
        public static PartyItemResult Empty(int itemId) => new(itemId, 0, 0, 0, NoCounts);

        // True when at least one member reported holding the item.
        public bool AnyHeld => TotalCount > 0;

        // The members who didn't answer, when they alone are short of one each:
        // everyone who answered (or was credited) holds one. Empty otherwise.
        public IReadOnlyList<string> SilentAloneShort() =>
            Unanswered is { Count: > 0 } silent && CountsByMember.Values.All(n => n >= 1)
                ? silent.ToList() : Array.Empty<string>();
    }

    private sealed class PendingQuery
    {
        public int ItemId;
        public string Query = string.Empty;   // normalized echoed substring to match
        public int Expected;
        public bool Completed;
        public readonly HashSet<string> Remaining = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);
        public readonly TaskCompletionSource<PartyItemResult> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly PartyBroadcaster _broadcaster;
    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly Action<Action> _armWindow;
    private readonly LogService? _log;
    private readonly List<PendingQuery> _pending = new();

    // For a bug report: what is asked and not yet answered, or null.
    public string? DescribePending() =>
        _pending.Count == 0 ? null : $"{_pending.Count} inventory query(ies) asked of party members, not yet answered";
    private bool _disposed;

    // How long to wait for replies before completing a query with whatever
    // arrived. Short — the round-trip is a single telepath each way. 4 s is a safe
    // default that tolerates a laggy BBS without stalling the walk.
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(4);

    // `cur` count is always positive; the echoed query is captured greedily so
    // an item name with a trailing space still round-trips. IgnoreCase because
    // @have echoes the sender's verbatim casing while we normalize both sides.
    [GeneratedRegex(@"^yes - (\d+)x '(.+)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HaveYesReply();

    [GeneratedRegex(@"^no - nothing matching '(.+)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HaveNoReply();

    // A MegaMUD member answers `@have` with a bare count and no item echo, with or
    // without a closing full stop (`{yes: 1}`, `{Yes: 1.}`).
    [GeneratedRegex(@"^yes:\s*(\d+)\s*\.?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HaveCountOnlyReply();

    public PartyInventoryProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party, LogService? log = null)
        : this(broadcaster, chat, party, armWindow: null, log) { }

    internal PartyInventoryProbe(
        PartyBroadcaster broadcaster, ChatRouter chat, PartyState party,
        Action<Action>? armWindow, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        _broadcaster = broadcaster;
        _chat = chat;
        _party = party;
        _log = log;
        _armWindow = armWindow ?? DefaultArmWindow;
        _chat.EntryClassified += OnChatEntry;
    }

    // Broadcast @have <itemName> to the party and complete once every member
    // replies or QueryWindow elapses. Returns an empty result immediately when the
    // party has no one else to ask.
    public Task<PartyItemResult> QueryAsync(int itemId, string itemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        if (_disposed) return Task.FromResult(PartyItemResult.Empty(itemId));

        PendingQuery pending = new() { ItemId = itemId, Query = Normalize(itemName) };
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            // Invited and not yet joined: they can't answer, and waiting on them
            // ran every count to the end of its window. The broadcast skips them too.
            if (m.IsInvited) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            pending.Remaining.Add(GivenName(m.Name));
        }
        pending.Expected = pending.Remaining.Count;

        if (pending.Expected == 0)
            return Task.FromResult(PartyItemResult.Empty(itemId));

        _pending.Add(pending);
        _broadcaster.Broadcast($"@have {itemName}", skipInvited: true);
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
            p.Tcs.TrySetResult(PartyItemResult.Empty(p.ItemId));
        }
        _pending.Clear();
    }

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (_pending.Count == 0) return;
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        string given = GivenName(entry.Speaker);
        if (!TryParseHaveReply(entry.Message, out int count, out string? echoedQuery))
        {
            // A member still owed an answer whose reply isn't read is counted as
            // holding none, so name the line: an unread reply shape looks just
            // like a member who never answered. Quoted in the game's own
            // `X telepaths: …` shape, which the bug report's chat censor masks.
            if (_pending.Any(p => !p.Completed && p.Remaining.Contains(given)))
                _log?.Info("PartyInventory",
                    $"An @have count waits on {given}, and this answer is not read as a count: {entry.Speaker} telepaths: {entry.Message}");
            return;
        }

        if (echoedQuery is null)
        {
            // No echo to match by name: credit the oldest query still waiting on
            // this member, on the reading that a member answers in the order asked.
            PendingQuery? oldest = _pending.FirstOrDefault(p => !p.Completed && p.Remaining.Contains(given));
            if (oldest is null) return;
            oldest.Remaining.Remove(given);
            oldest.Counts[given] = count;
            if (oldest.Remaining.Count == 0) Complete(oldest);
            return;
        }

        string norm = Normalize(echoedQuery);

        // Snapshot: Complete mutates _pending. A reply satisfies more than one
        // in-flight query only when two queries used the same item name — rare,
        // and recording the count for each is correct.
        foreach (PendingQuery p in _pending.ToArray())
        {
            if (p.Completed) continue;
            if (!p.Query.Equals(norm, StringComparison.OrdinalIgnoreCase)) continue;
            if (!p.Remaining.Remove(given)) continue;   // not expected / already counted
            p.Counts[given] = count;
            if (p.Remaining.Count == 0) Complete(p);
        }
    }

    private void Complete(PendingQuery p)
    {
        if (p.Completed) return;
        p.Completed = true;
        _pending.Remove(p);

        int total = 0;
        foreach (int c in p.Counts.Values) total += c;
        int replied = p.Expected - p.Remaining.Count;

        p.Tcs.TrySetResult(new PartyItemResult(
            p.ItemId, total, p.Expected, replied,
            new Dictionary<string, int>(p.Counts, StringComparer.OrdinalIgnoreCase))
        {
            Unanswered = p.Remaining.ToArray(),
        });
        _log?.Info("PartyInventory",
            $"@have '{p.Query}' — {replied}/{p.Expected} replied, {total} held across party.");
    }

    private void DefaultArmWindow(Action onElapsed)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = QueryWindow };
        timer.Tick += (_, _) => { timer.Stop(); onElapsed(); };
        timer.Start();
    }

    // Every remote-command reply travels brace-wrapped (`{yes - 1x 'rope'}`), so
    // the braces come off before the body is matched.
    private static bool TryParseHaveReply(string message, out int count, out string? query)
    {
        message = message.Trim();
        if (message.Length >= 2 && message[0] == '{' && message[^1] == '}') message = message[1..^1].Trim();

        // A count too big for an int is unread, not thrown: a throw here would
        // cancel every handler still due on the line.
        Match countOnly = HaveCountOnlyReply().Match(message);
        if (countOnly.Success)
        {
            query = null;
            return TryCount(countOnly.Groups[1].Value, out count);
        }
        Match yes = HaveYesReply().Match(message);
        if (yes.Success)
        {
            query = yes.Groups[2].Value;
            return TryCount(yes.Groups[1].Value, out count);
        }
        Match no = HaveNoReply().Match(message);
        if (no.Success)
        {
            count = 0;
            query = no.Groups[1].Value;
            return true;
        }
        count = 0;
        query = string.Empty;
        return false;
    }

    private static bool TryCount(string digits, out int count) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count);

    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
