using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// On-demand party-inventory probe. Asks the whole party "do you have this item,
// and how many?" by broadcasting @have <item> to every non-self member and
// aggregating their replies into a single PartyItemResult. Backs the Settings →
// Other "defer to party inventory" affordance: when a walk-route crosses an
// (Item: N) / (Ticket: N) gate whose per-member item we lack, PartyPathItemGate
// probes the party first and — if a member has a spare — has them hand it over
// instead of posting a search / shop need.
//
// The round-trip rides the existing remote-command plumbing: the query goes out
// through PartyBroadcaster.Broadcast (one /<given> @have <item> per member), and
// each member's InventoryQueryHandler.OnHave replies "yes - Nx matching '<q>'" /
// "no - nothing matching '<q>'" back over an incoming telepath. We subscribe to
// ChatRouter.EntryClassified and correlate each reply by the echoed
// matching '<q>' substring, so several per-item queries can be in flight at once
// without their replies crossing wires.
//
// A query completes early once every expected responder has replied, or when the
// QueryWindow elapses (a member who never answers — offline, no ExecuteCommands
// round-trip, hasn't parsed inventory — simply doesn't count toward the total).
// The expected-responder set mirrors PartyBroadcaster's own filter (non-self,
// named), so we never wait on a member the broadcast skipped. With no one to ask
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

        // An empty result — no party members to ask, or the probe was disposed.
        public static PartyItemResult Empty(int itemId) => new(itemId, 0, 0, 0, NoCounts);

        // True when at least one member reported holding the item.
        public bool AnyHeld => TotalCount > 0;
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
    private bool _disposed;

    // How long to wait for replies before completing a query with whatever
    // arrived. Short — the round-trip is a single telepath each way. 4 s is a safe
    // default that tolerates a laggy BBS without stalling the walk.
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(4);

    // `cur` count is always positive; the echoed query is captured greedily so
    // an item name with a trailing space still round-trips. IgnoreCase because
    // @have echoes the sender's verbatim casing while we normalize both sides.
    [GeneratedRegex(@"^yes - (\d+)x matching '(.+)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HaveYesReply();

    [GeneratedRegex(@"^no - nothing matching '(.+)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HaveNoReply();

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
            if (string.IsNullOrEmpty(m.Name)) continue;
            pending.Remaining.Add(GivenName(m.Name));
        }
        pending.Expected = pending.Remaining.Count;

        if (pending.Expected == 0)
            return Task.FromResult(PartyItemResult.Empty(itemId));

        _pending.Add(pending);
        _broadcaster.Broadcast($"@have {itemName}");
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
        if (!TryParseHaveReply(entry.Message, out int count, out string echoedQuery)) return;

        string given = GivenName(entry.Speaker);
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
            new Dictionary<string, int>(p.Counts, StringComparer.OrdinalIgnoreCase)));
        _log?.Info("PartyInventory",
            $"@have '{p.Query}' — {replied}/{p.Expected} replied, {total} held across party.");
    }

    private void DefaultArmWindow(Action onElapsed)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = QueryWindow };
        timer.Tick += (_, _) => { timer.Stop(); onElapsed(); };
        timer.Start();
    }

    private static bool TryParseHaveReply(string message, out int count, out string query)
    {
        Match yes = HaveYesReply().Match(message);
        if (yes.Success)
        {
            count = int.Parse(yes.Groups[1].Value, CultureInfo.InvariantCulture);
            query = yes.Groups[2].Value;
            return true;
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

    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
