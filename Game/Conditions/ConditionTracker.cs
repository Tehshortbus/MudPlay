using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Conditions;

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
    public bool IsLosingHp           => ActiveFlags.HasFlag(MessageFlags.LosingHp);
    public bool IsMovementPrevented  => ActiveFlags.HasFlag(MessageFlags.MovementPrevented);
    public bool IsAttackPrevented    => ActiveFlags.HasFlag(MessageFlags.AttackPrevented);
    public bool IsHpRegenerating     => ActiveFlags.HasFlag(MessageFlags.HpRegenerating);
    public bool IsManaRegenerating   => ActiveFlags.HasFlag(MessageFlags.ManaRegenerating);

    // Fires when a record's AppliedMessage matches and the record wasn't already
    // active. Carries the record itself so downstream engines can read its Action
    // and dispatch (RestHp, Run, Hangup, etc.).
    public event Action<MessageRecord>? ConditionApplied;

    // Fires when a previously-active record's AppliedEndsWith matches.
    public event Action<MessageRecord>? ConditionEnded;

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

    // Force-clear all conditions. Wire on disconnect / death / session reset —
    // server state changes reset the truth, our observation log is stale.
    public void ClearAll()
    {
        if (_active.Count == 0) return;
        _active.Clear();
        ActiveFlags = MessageFlags.None;
        _log?.Info(LogCategory, "all conditions cleared (manual)");
    }

    // The store fires one Reset for a bulk (re)load, so this rebuilds once per set
    // switch rather than once per record — see BulkObservableCollection.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildIndex();

    private void RebuildIndex()
    {
        List<(string, MessageRecord)> applied = new();
        List<(string, MessageRecord)> ends = new();
        foreach (MessageRecord r in _messages.Messages)
        {
            if (!string.IsNullOrWhiteSpace(r.AppliedMessage))
                applied.Add((r.AppliedMessage, r));
            if (!string.IsNullOrWhiteSpace(r.AppliedEndsWith))
                ends.Add((r.AppliedEndsWith, r));
        }
        _appliedIndex = applied;
        _endsIndex = ends;
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
                known.Add(r.Id);
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
            endedThisLine.Add(r);
        }

        foreach ((string pattern, MessageRecord r) in _appliedIndex)
        {
            if (!text.Contains(pattern, StringComparison.Ordinal)) continue;
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
    }

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
