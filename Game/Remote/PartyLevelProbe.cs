using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// On-demand party-level probe. Asks the whole party "what level are you?" by
/// broadcasting <c>@level</c> to every non-self member and aggregating the
/// replies into a single <see cref="PartyLevelResult"/>. Backs the leader-side
/// level-gate check: when a leader's planned walk crosses a
/// <c>(Level: MIN to MAX)</c> gate, <see cref="Game.Map.PartyLevelGate"/> probes
/// the party and warns / prompts when a member's level falls outside the window
/// (the walker's own <see cref="Game.Map.IRoomFilter"/> already guarantees the
/// leader clears it, so only the followers need checking).
/// </summary>
/// <remarks>
/// <para>
/// The round-trip rides the same plumbing as <see cref="PartyInventoryProbe"/>:
/// the query goes out through <see cref="PartyBroadcaster.Broadcast"/> (one
/// <c>/&lt;given&gt; @level</c> per member) and each member's
/// <see cref="ExperienceQueryHandler.OnLevel"/> replies
/// <c>"Level N, X exp, Y to next level"</c> back over an incoming telepath. We
/// subscribe to <see cref="ChatRouter.EntryClassified"/> and read the level off
/// each reply.
/// </para>
/// <para>
/// Unlike <c>@have</c> there is no per-query parameter to echo, so a level reply
/// from a member satisfies every in-flight query still awaiting that member.
/// Concurrent queries are rare (one per level-gated walk) and simply share
/// replies — the same behaviour the item probe documents for two queries using
/// the same item name. A member that answers <c>"level unknown …"</c> (hasn't
/// parsed a stat screen) counts as replied but contributes no level, so the gate
/// can't check them; a member who never answers falls off with the
/// <see cref="QueryWindow"/>. With no one to ask the task completes synchronously
/// with an empty result.
/// </para>
/// <para>
/// Every parsed level is also the authoritative record of that player's level:
/// the probe forwards it to the <c>recordLevel</c> seam (bound in production to
/// <see cref="Services.PlayerDatabase.RecordLevel"/>), so the exact number
/// supersedes the 5-level band the player's title otherwise implies. This is the
/// one place the app learns a party member's precise level.
/// </para>
/// </remarks>
public sealed partial class PartyLevelProbe : IDisposable
{
    /// <summary>Aggregate outcome of one <c>@level</c> round-trip.</summary>
    /// <param name="Expected">How many members the query was sent to.</param>
    /// <param name="Replied">How many members answered before completion (incl. "level unknown").</param>
    /// <param name="LevelsByMember">
    /// Per-member level keyed by given name (case-insensitive). Only members
    /// that reported a numeric level appear; a non-responder or a
    /// "level unknown" reply is absent.
    /// </param>
    public readonly record struct PartyLevelResult(
        int Expected,
        int Replied,
        IReadOnlyDictionary<string, int> LevelsByMember)
    {
        private static readonly IReadOnlyDictionary<string, int> NoLevels =
            new Dictionary<string, int>();

        /// <summary>An empty result — no party members to ask, or the probe was disposed.</summary>
        public static PartyLevelResult Empty => new(0, 0, NoLevels);

        /// <summary>True when at least one member answered (with a level or "unknown").</summary>
        public bool AnyReplied => Replied > 0;
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
    private bool _disposed;

    /// <summary>
    /// How long to wait for replies before completing with whatever arrived.
    /// Short — the round-trip is a single telepath each way. 4 s tolerates a
    /// laggy BBS without stalling the walk check.
    /// </summary>
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(4);

    // Require the full "Level N, X exp," shape (not just a leading "Level N")
    // so a member's chatter that happens to start "Level 5, …" can't be
    // misread as a level reply while a probe is open.
    [GeneratedRegex(@"^Level (\d+), [\d,]+ exp\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LevelReply();

    [GeneratedRegex(@"^level unknown\b",
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

    /// <summary>
    /// Broadcast <c>@level</c> to the party and complete once every member
    /// replies or <see cref="QueryWindow"/> elapses. Returns an empty result
    /// immediately when the party has no one else to ask.
    /// </summary>
    public Task<PartyLevelResult> QueryAsync()
    {
        if (_disposed) return Task.FromResult(PartyLevelResult.Empty);

        PendingQuery pending = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            pending.Remaining.Add(GivenName(m.Name));
        }
        pending.Expected = pending.Remaining.Count;

        if (pending.Expected == 0)
            return Task.FromResult(PartyLevelResult.Empty);

        _pending.Add(pending);
        _broadcaster.Broadcast("@level");
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
        if (_pending.Count == 0) return;
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;

        bool known = TryParseLevelReply(entry.Message, out int level);
        bool unknown = !known && LevelUnknownReply().IsMatch(entry.Message);
        if (!known && !unknown) return;

        string given = GivenName(entry.Speaker);

        // A parsed level is authoritative for this player — persist it to the
        // players table regardless of which in-flight query (if any) still
        // awaits them, so the exact level supersedes the title-derived band.
        if (known) _recordLevel?.Invoke(given, level);

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

    private static bool TryParseLevelReply(string message, out int level)
    {
        Match m = LevelReply().Match(message);
        if (m.Success)
        {
            level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return true;
        }
        level = 0;
        return false;
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
