using System.Collections.Specialized;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Tracks active conditions on the local character by matching inbound lines
// against MessageRecord.AppliedMessage / MessageRecord.AppliedEndsWith pairs in
// the active MessageStore. Exposes the resulting MessageFlags bitfield as an
// observable so engines (CastingDirector's Tier-2 cure path, HealthManager's
// rest gating, etc.) can read it without re-scanning text.
//
// User-extensible by design: the user defines what lines map to what condition
// effects via the Game Data Browser → Messages tab. No hardcoded ailment names
// live in this class — every flag bit comes from a record's Flags value, which
// means a realm with a unique status effect "Cursed" just needs a Messages-tab
// entry, not engine code.
//
// Matching is case-sensitive substring containment — verbatim text from the
// record. An empty AppliedMessage skips the record entirely; an empty
// AppliedEndsWith means "no auto-clear" (caller must explicitly clear via
// ClearAll on disconnect / death).
//
// Index rebuild fires on every MessageStore.Messages collection change so the
// user's live edits in the Messages tab take effect immediately without a
// session restart.
public sealed partial class ConditionTracker : ObservableObject, IDisposable
{
    // LogService category — appears as [Condition] rows per applied + ended.
    public const string LogCategory = "Condition";

    private readonly MessageStore _messages;
    private readonly LogService? _log;

    // Records keyed by ID currently active on us (their AppliedMessage fired and
    // the matching EndsWith hasn't).
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    // Built from MessageStore on every CollectionChanged. Maps an applied-message
    // string to the records that carry it (multiple records can share text —
    // realm variants).
    private List<(string Pattern, MessageRecord Record)> _appliedIndex = new();

    // Built from MessageStore on every CollectionChanged. Maps an ends-with
    // string to records — only records whose EndsWith is non-empty get indexed.
    private List<(string Pattern, MessageRecord Record)> _endsIndex = new();

    // Built alongside the indexes: an exact AppliedMessage string to every record
    // that carries it. Records sharing an applied line are indistinguishable
    // aliases of one effect (realm variants — see _appliedIndex), so they latch
    // together on that shared line and must clear together. The game emits shared
    // generic wear-offs too (e.g. one "The effects of confusion wear off!" ends
    // any confusion source), so ending any alias ends the whole group — see OnLine.
    private Dictionary<string, List<MessageRecord>> _appliedAliases = new(StringComparer.Ordinal);

    // Normalized confusion-fumble wordings pulled from every Confused record's
    // ConfuseFumbleLine (one per line). A fumbled MOVE reverts on any of these — see
    // MovementRefusalDetector, which queries IsConfuseFumbleLine instead of hardcoding
    // the wordings. Rebuilt with the other indexes on every MessageStore change.
    private List<string> _confuseFumbleIndex = new();

    private LineExtractor? _lines;
    private bool _disposed;

    [ObservableProperty]
    [field: Owner(typeof(ConditionTracker))]
    private MessageFlags _activeFlags;

    // ----- Per-flag computed helpers — keep CastingDirector & friends
    // readable without sprinkling .HasFlag() everywhere.

    public bool IsBlinded            => ActiveFlags.HasFlag(MessageFlags.Blinded);
    public bool IsConfused           => ActiveFlags.HasFlag(MessageFlags.Confused);
    public bool IsPoisoned           => ActiveFlags.HasFlag(MessageFlags.Poisoned);
    public bool IsDiseased           => ActiveFlags.HasFlag(MessageFlags.Diseased);
    public bool IsMovementPrevented  => ActiveFlags.HasFlag(MessageFlags.MovementPrevented);
    public bool IsAttackPrevented    => ActiveFlags.HasFlag(MessageFlags.AttackPrevented);

    // Fires when a record's AppliedMessage matches and the record wasn't already
    // active. Carries the record itself so downstream engines can read its Action
    // and dispatch (RestHp, Run, Hangup, etc.).
    public event Action<MessageRecord>? ConditionApplied;

    // Fires when a previously-active record's AppliedEndsWith matches.
    public event Action<MessageRecord>? ConditionEnded;

    // Fires per matching line for a record carrying LastActionFailed — the
    // transient "the action you just sent didn't take, resend it" outcome (a
    // fizzle, an interrupted/eaten command), NOT a lasting condition and NOT
    // confusion-specific. (Confusion's fumbled MOVE revert rides the separate
    // ConfuseFumbleLine path; a fumble line merely happens to be one thing that
    // can carry this flag.) Unlike ConditionApplied this is NOT deduped by the
    // active set: the same failure line can recur action after action, and combat
    // must re-send its lost swing on EVERY occurrence, not just the first. Fires
    // at most once per line.
    public event Action<MessageRecord>? ActionFailed;

    public ConditionTracker(MessageStore messages, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
        _log = log;

        RebuildIndex();
        _messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    // Bind to the per-session LineExtractor so every inbound line is scanned.
    // Idempotent — re-attaching to the same extractor is a no-op.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // True when the given record is currently active on us (its applied message
    // fired without a matching ends-with).
    public bool IsActive(MessageRecord r) => _active.Contains(r.Id);

    // True when any currently-active record's Name matches name
    // (case-insensitive). Lets CastingDirector ask "is the 'bless' buff still on
    // me?" without holding a record reference. Matches by name rather than by
    // content-hash Id because the user may have multiple realm variants of the
    // same spell + the name is what the Spells settings tab stores.
    public bool IsActiveByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (MessageRecord r in _messages.Messages)
        {
            if (!_active.Contains(r.Id)) continue;
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // True when text is one of the confusion-fumble wordings configured on a Confused
    // record's ConfuseFumbleLine — the WHOLE line must match (ignoring surrounding
    // whitespace, a trailing '.'/'!', and case) so a chat line quoting the phrase can't
    // false-trigger. MovementRefusalDetector calls this to revert a fumbled move without
    // hardcoding the wordings.
    public bool IsConfuseFumbleLine(string text)
    {
        if (_confuseFumbleIndex.Count == 0 || string.IsNullOrEmpty(text)) return false;
        string norm = NormalizeFumbleLine(text);
        if (norm.Length == 0) return false;
        foreach (string wording in _confuseFumbleIndex)
            if (string.Equals(norm, wording, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Trim surrounding whitespace and one trailing sentence terminator so a stored
    // "You fumble in confusion!" matches the wire "You fumble in confusion." and vice
    // versa — the same tolerance the old anchored regexes carried.
    private static string NormalizeFumbleLine(string s)
    {
        s = s.Trim();
        if (s.Length > 0 && (s[^1] == '.' || s[^1] == '!')) s = s[..^1].TrimEnd();
        return s;
    }

    // Force-clear all conditions. Wire on disconnect / death / session reset —
    // server state changes reset the truth, our observation log is stale. This is
    // a safe over-clear: the tracker is an observation log, so any condition still
    // true after the reset re-latches on its next matching server line. reason is
    // the program-log breadcrumb naming what triggered the clear.
    public void ClearAll(string reason = "reset")
    {
        if (_active.Count == 0) return;
        _active.Clear();
        ActiveFlags = MessageFlags.None;
        _log?.Info(LogCategory, $"all conditions cleared ({reason})");
    }

    // The store fires one Reset for a bulk (re)load, so this rebuilds once per set
    // switch rather than once per record — see BulkObservableCollection.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildIndex();

    private void RebuildIndex()
    {
        List<(string, MessageRecord)> applied = new();
        List<(string, MessageRecord)> ends = new();
        Dictionary<string, List<MessageRecord>> aliases = new(StringComparer.Ordinal);
        List<string> fumbles = new();
        foreach (MessageRecord r in _messages.Messages)
        {
            // 'Disabled (don't use)' — ignore the record wholesale: it never indexes,
            // so it can't latch a condition, set a flag, contribute a fumble wording,
            // or fire ActionFailed. Any active copy is dropped in the prune below.
            if (r.Flags.HasFlag(MessageFlags.Disabled)) continue;

            // A slot holding a {null}/{void}/{empty} sentinel means "this spell has no such
            // line" — treat it as absent so it never compiles into a matcher (IsBlankOrAbsent),
            // exactly as an empty slot would. Only real wording indexes.
            if (!MessageRecord.IsBlankOrAbsent(r.AppliedMessage))
            {
                applied.Add((r.AppliedMessage, r));
                if (!aliases.TryGetValue(r.AppliedMessage, out List<MessageRecord>? group))
                    aliases[r.AppliedMessage] = group = new List<MessageRecord>();
                group.Add(r);
            }
            if (!MessageRecord.IsBlankOrAbsent(r.AppliedEndsWith))
                ends.Add((r.AppliedEndsWith, r));
            if (r.Flags.HasFlag(MessageFlags.Confused) && !MessageRecord.IsBlankOrAbsent(r.ConfuseFumbleLine))
                foreach (string wording in r.ConfuseFumbleLine.Split('\n'))
                {
                    if (MessageRecord.IsAbsentSentinel(wording)) continue;
                    string norm = NormalizeFumbleLine(wording);
                    if (norm.Length > 0) fumbles.Add(norm);
                }
        }
        _appliedIndex = applied;
        _endsIndex = ends;
        _appliedAliases = aliases;
        _confuseFumbleIndex = fumbles;
        _log?.Debug(LogCategory,
            $"index built — applied={applied.Count} endsWith={ends.Count} totalRecords={_messages.Messages.Count}");

        // A rebuilt index may have dropped records that were active;
        // prune _active accordingly. We don't recompute flags here on
        // applied-side (no new matches without a line emit), but we do
        // drop stale active entries.
        if (_active.Count > 0)
        {
            HashSet<string> known = new(StringComparer.Ordinal);
            foreach (MessageRecord r in _messages.Messages)
                if (!r.Flags.HasFlag(MessageFlags.Disabled)) known.Add(r.Id);
            _active.RemoveWhere(id => !known.Contains(id));
            RecomputeFlags();
        }
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text;
        if (string.IsNullOrEmpty(text)) return;

        // EndsWith first — if a condition both starts and ends with
        // overlapping text on the same line (rare but possible), the
        // end side wins so we don't latch a phantom-applied state.
        // Flags are recomputed BEFORE the events fire so subscribers
        // (CastingDirector's cure path) read the post-transition
        // state when they re-evaluate.
        List<MessageRecord> endedThisLine = new();
        List<MessageRecord> appliedThisLine = new();

        foreach ((string pattern, MessageRecord r) in _endsIndex)
        {
            if (!text.Contains(pattern, StringComparison.Ordinal)) continue;
            if (!_active.Remove(r.Id)) continue;
            endedThisLine.Add(r);   // PRIMARY: its OWN wear-off line fired

            // Collateral clears below drop co-latched siblings from _active so the
            // recomputed flags (and the nav pause they drive) stay honest — but they
            // are NOT added to endedThisLine, so ConditionEnded does NOT fire for them.
            // That event's only consumer is the self-buff recast timers, which anchor
            // on each buff's OWN 4-letter cast code: a sibling that merely shares an
            // applied line (the 5 spells that all emit "You feel protected!") wearing
            // off must not tear down the timer of a DIFFERENT buff we actually cast.
            // Flags/ailments key off _active, never this event (a confusion record maps
            // to no cast code), so clearing _active without the event keeps confusion
            // and every other flag behaving exactly as before.

            // Group clear: records that share r's exact applied line were latched
            // together on that shared line, so a wear-off for any of them ends the
            // whole group — otherwise a sibling carrying its own specific wear-off is
            // stranded active when the shared generic wear-off fires (the confusion
            // flag / nav pause sticking).
            if (!string.IsNullOrEmpty(r.AppliedMessage)
                && _appliedAliases.TryGetValue(r.AppliedMessage, out List<MessageRecord>? group))
            {
                foreach (MessageRecord alias in group)
                    if (alias.Id != r.Id) _active.Remove(alias.Id);
            }

            // Every flag is a single toggle state, not a per-source stack: any
            // wear-off clears EVERY other latched record sharing that flag, not
            // just this record's applied-line aliases. A source-specific wear-off
            // (e.g. "black curse"'s own "Your vision returns to normal.") thus
            // also releases a co-latched sibling that shares the flag through a
            // different, ambiguous applied line (the generic "You are blind."
            // wording shared by several unrelated effects) whose own wear-off
            // text never arrives this session — instead of leaving the flag, and
            // whatever it gates (a nav pause, an auto-cure loop), stuck forever.
            // Originally scoped to Confused only (report -092219: a monster
            // confuse's death-dog-shriek wear-off had to also release a
            // co-latched generic fumble record); generalized after the identical
            // symptom reproduced for Blinded (report paradigm-20260904-214452:
            // 8 "You are blind." aliases latched on one shaman's "black curse",
            // only the curse's own wear-off matched, and the other 7 spent every
            // remaining combat round re-casting cure blindness forever).
            if (r.Flags != MessageFlags.None)
            {
                foreach (MessageRecord other in _messages.Messages)
                    if ((other.Flags & r.Flags) != MessageFlags.None) _active.Remove(other.Id);
            }
        }

        // A "You feel X! (Ns)" line is Paradigm's `stat` status readout of an ALREADY-up
        // effect (trailing remaining-time), NOT a fresh cast. Its effect text is shared
        // across many records (one line names 11), so it can neither identify which buff
        // is up nor legitimately "apply" one — matching it falsely latched buffs on login
        // and suppressed the real cast's confirm. Buff timers anchor on the typed cast
        // code instead; a genuine fresh-cast effect line carries no parenthetical.
        MessageRecord? actionFailed = null;
        if (!StatusEffectReadout().IsMatch(text))
            foreach ((string pattern, MessageRecord r) in _appliedIndex)
            {
                if (!text.Contains(pattern, StringComparison.Ordinal)) continue;
                // Capture a LastActionFailed match BEFORE the active-set dedup below —
                // a failure line can recur while its record stays "applied" only once,
                // so ActionFailed must ride the raw line, not the deduped apply. First
                // match only; alias records sharing the line must not double the retry.
                if (actionFailed is null && r.Flags.HasFlag(MessageFlags.LastActionFailed))
                    actionFailed = r;
                if (!_active.Add(r.Id)) continue;
                appliedThisLine.Add(r);
            }

        if (endedThisLine.Count > 0 || appliedThisLine.Count > 0)
            RecomputeFlags();

        // Log the batch collapsed, then fan out the per-record events unchanged —
        // downstream (CastingDirector's buff-timer path) still needs to see every
        // record so it can pick out the one that maps to a cast code.
        LogBatch("ended", endedThisLine);
        foreach (MessageRecord r in endedThisLine)
            ConditionEnded?.Invoke(r);

        LogBatch("applied", appliedThisLine);
        foreach (MessageRecord r in appliedThisLine)
            ConditionApplied?.Invoke(r);

        if (actionFailed is { } af)
            ActionFailed?.Invoke(af);
    }

    // Matches a trailing remaining-time parenthetical — "(411s)", "(6m 51s)", "(1h)" —
    // the tell of a `stat` status readout of an already-active effect. A fresh-cast
    // effect line ("You feel lucky!") has none, so this never suppresses a real cast.
    [GeneratedRegex(@"\(\d+[dhms]( \d+[dhms])*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusEffectReadout();

    // A single game line can match many catalogue records that share the same
    // effect text — every bless-proc item plus the bless spell all emit "You feel
    // lucky!", so one self-cast would otherwise spam an Info row per record.
    // Collapse a multi-record batch to one summary row (naming the effects, OR'ing
    // their flags); a lone match keeps the detailed per-record form.
    private void LogBatch(string verb, List<MessageRecord> records)
    {
        if (_log is null || records.Count == 0) return;
        if (records.Count == 1)
        {
            MessageRecord only = records[0];
            _log.Info(LogCategory, $"condition {verb} name='{only.Name}' flags={only.Flags}");
            return;
        }
        MessageFlags flags = MessageFlags.None;
        foreach (MessageRecord r in records) flags |= r.Flags;
        string names = string.Join(", ", records.Select(r => r.Name));
        _log.Info(LogCategory,
            $"condition {verb} — {records.Count} records matched one line (names: {names}) flags={flags}");
    }

    private void RecomputeFlags()
    {
        MessageFlags flags = MessageFlags.None;
        foreach (MessageRecord r in _messages.Messages)
        {
            if (!_active.Contains(r.Id)) continue;
            flags |= r.Flags;
        }
        if (flags == ActiveFlags) return;
        ActiveFlags = flags;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messages.Messages.CollectionChanged -= OnMessagesChanged;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
