using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Tracks active conditions on the local character by matching inbound lines
// against MessageRecord.AppliedMessage / MessageRecord.AppliedEndsWith pairs in
// the active MessageStore. Exposes the resulting MessageFlags bitfield as an
// observable so engines (CastingDirector's cure picks, HealthManager's
// rest gating, etc.) can read it without re-scanning text.
//
// User-extensible by design: the user defines what lines map to what condition
// effects via the Game Data Browser → Messages tab. No hardcoded ailment names
// live in the text matching — flags come from the records or an authoritative
// server status. A realm with a unique status effect "Cursed" needs a Messages-tab
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

    // When each record last latched and the line that latched it. Read only for the
    // records still in _active: an ailment held with no wear-off ever seen has the
    // line that set it named in the bug report, which the program log has long
    // since dropped by then (report paradigm-20261022-164244: Confused held for 23
    // minutes, set before the log's window opened).
    private readonly Dictionary<string, (DateTimeOffset At, string Line)> _latchedBy = new(StringComparer.Ordinal);

    // ----- Ailments that outlive their spell ----------------------------
    // An ailment whose wear-off line never comes ends here once its spell's time is
    // up (Client policy, user 2026-10-23): Confused was held for 23 minutes on a
    // spell of seconds, and the walk sat paused on it (report
    // paradigm-20261022-164244). The applied line arms it again, so an ailment cast
    // anew stays. The seconds are the record's spell duration from the game data,
    // or PartyAilmentTracker.FallbackDurationSeconds when none resolves (the cap the
    // party's chips use). Unset, nothing expires. Swept on each line, prompts
    // included, by the line's own time.
    public Func<MessageRecord, double?>? AilmentSeconds { get; set; }

    private const MessageFlags AilmentFlags = MessageFlags.Blinded | MessageFlags.Confused | MessageFlags.Poisoned
        | MessageFlags.Fear | MessageFlags.MovementPrevented | MessageFlags.AttackPrevented | MessageFlags.Diseased;

    private readonly Dictionary<string, DateTimeOffset> _expiresAt = new(StringComparer.Ordinal);

    private void ArmExpiry(MessageRecord r, DateTimeOffset at)
    {
        if (AilmentSeconds is not { } seconds || (r.Flags & AilmentFlags) == MessageFlags.None) return;
        double secs = seconds(r) is > 0 and double s ? s : PartyAilmentTracker.FallbackDurationSeconds;
        _expiresAt[r.Id] = at + TimeSpan.FromSeconds(secs);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        if (_expiresAt.Count == 0) return;
        List<string>? due = null;
        foreach ((string id, DateTimeOffset until) in _expiresAt)
            if (until <= now) (due ??= new()).Add(id);
        if (due is null) return;
        bool ended = false;
        foreach (string id in due)
        {
            _expiresAt.Remove(id);
            if (!_active.Remove(id)) continue;
            ended = true;
            MessageRecord? r = null;
            foreach (MessageRecord m in _messages.Messages)
                if (m.Id == id) { r = m; break; }
            string since = _latchedBy.TryGetValue(id, out (DateTimeOffset At, string Line) by)
                ? $" {(now - by.At).TotalSeconds:0}s after it was set" : "";
            _log?.Info(LogCategory,
                $"condition expired name='{r?.Name ?? id}' flags={r?.Flags}{since}: its spell's time is up and no wear-off line came");
        }
        if (ended) RecomputeFlags();
    }

    // Built from MessageStore on every CollectionChanged. Maps an applied-message
    // string to the records that carry it (multiple records can share text —
    // realm variants).
    private List<(string Pattern, MessageRecord Record)> _appliedIndex = new();
    // Applied text → the longer applied texts that hold it (BuildLongerApplied).
    private Dictionary<string, string[]> _longerApplied = new(StringComparer.Ordinal);
    // Applied record → the cast lines of other spells that hold its applied text
    // (BuildCastLinesHolding). Keyed by the record and not its text: which lines
    // are another spell's depends on the record's own spells.
    private Dictionary<MessageRecord, HoldingLine[]> _castLinesHolding = new(ReferenceEqualityComparer.Instance);

    // Who could have cast a spell where the character stands. Unset, every record a
    // line matches is latched.
    private CastReach? _castReach;

    // Record name → the records of that name, to say which spell a negated cast was.
    private Dictionary<string, List<MessageRecord>> _recordsByName = new(StringComparer.OrdinalIgnoreCase);

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
    // ConfuseFumbleLine (one per line), paired with the record it came from. A
    // fumbled MOVE reverts on any of these — see MovementRefusalDetector, which
    // queries IsConfuseFumbleLine instead of hardcoding the wordings. A match ALSO
    // fires ActionFailed (see OnLine): a confusion fumble IS an eaten command, the
    // exact thing ActionFailed exists to signal, so combat can re-send the lost
    // swing without requiring a second, separately LastActionFailed-flagged record
    // duplicating the same wording — no shipped record has ever carried that flag,
    // which left a fumbled swing during confusion/convulsions unattacked until the
    // user manually re-sent it (report paradigm-20260908-211659: "sit here and get
    // beat on"). Rebuilt with the other indexes on every MessageStore change.
    private List<(string Norm, MessageRecord Record)> _confuseFumbleIndex = new();

    // Several spells can print the same applied line and differ in what they do:
    // "You are enveloped in darkness" is globe of darkness (which takes the light
    // away, and is no blindness) and blind globe (which is one). Read off the
    // applied line alone, all of them latched, and the globe set Blinded and had
    // the party asked to cure a blindness nobody had. The spell's own cast line,
    // printed just ahead, says which it was. Indexed only for records whose shared
    // applied text carries more than one set of flags; everything else latches as
    // it always did.
    private List<(CasterMessageMatcher? Template, string Literal, string RecordId)> _castLineIndex = new();
    private HashSet<string> _ambiguousApplied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _castSeenAt = new(StringComparer.Ordinal);
    // The one caster the last such cast line named, as the line shows it.
    private (string Name, DateTimeOffset At)? _namedCaster;
    // The cast lines and the applied line arrive in one burst.
    private static readonly TimeSpan CastLineWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NarrowingRepeatWindow = TimeSpan.FromSeconds(60);

    private LineExtractor? _lines;
    private bool _disposed;

    [ObservableProperty]
    [field: Owner(typeof(ConditionTracker))]
    private MessageFlags _activeFlags;
    // The server's status flags, which override the records' for the conditions it
    // reports (GmcpMask); null while the server doesn't own the status.
    private MessageFlags? _gmcpFlags;
    private const MessageFlags GmcpMask =
        MessageFlags.Blinded | MessageFlags.Confused | MessageFlags.Poisoned | MessageFlags.Diseased;

    public void ApplyGmcpFlags(MessageFlags? flags)
    {
        if (_gmcpFlags == flags) return;
        _gmcpFlags = flags;
        RecomputeFlags();
    }

    // ----- Per-flag computed helpers — keep CastingDirector & friends
    // readable without sprinkling .HasFlag() everywhere.

    public bool IsBlinded            => ActiveFlags.HasFlag(MessageFlags.Blinded);
    public bool IsConfused           => ActiveFlags.HasFlag(MessageFlags.Confused);
    public bool IsFeared             => ActiveFlags.HasFlag(MessageFlags.Fear);
    public bool IsPoisoned           => ActiveFlags.HasFlag(MessageFlags.Poisoned);
    public bool IsDiseased           => ActiveFlags.HasFlag(MessageFlags.Diseased);
    public bool IsMovementPrevented  => ActiveFlags.HasFlag(MessageFlags.MovementPrevented);
    public bool IsAttackPrevented    => ActiveFlags.HasFlag(MessageFlags.AttackPrevented);

    // Fires when a record's AppliedMessage matches and the record wasn't already
    // active. Carries the record itself so downstream engines can read which
    // effect it is (its Name, Flags and Links).
    public event Action<MessageRecord>? ConditionApplied;

    // Fires when a previously-active record's AppliedEndsWith matches.
    public event Action<MessageRecord>? ConditionEnded;

    // Fires per matching line for either (a) a record carrying LastActionFailed
    // or (b) a line matching some Confused record's ConfuseFumbleLine — both are
    // the same transient "the action you just sent didn't take, resend it"
    // outcome (a fizzle, an interrupted/eaten command), NOT a lasting condition.
    // A confusion fumble IS an eaten command, so it fires this on its own; a
    // dedicated LastActionFailed record exists for any OTHER, non-confusion
    // source of the same outcome. Unlike ConditionApplied this is NOT deduped by
    // the active set: the same failure line can recur action after action, and
    // combat must re-send its lost swing on EVERY occurrence, not just the first. Fires
    // at most once per line.
    public event Action<MessageRecord>? ActionFailed;

    // Fires when a watched cast line (BuildCastLineIndex) names who cast it, with the
    // name and the line. Its caster is in the room: one the roster doesn't list means
    // the roster is short, and a shared applied line must not be read by it until the
    // room is read again (RoomEntityClassifier.NoteCasterNamed).
    public event Action<string, string>? CasterNamed;

    public ConditionTracker(MessageStore messages, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
        _log = log;

        RebuildIndex();
        _messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    // Whether the line being read is part of a `stat` screen. Stock lists each active
    // effect there as its bare applied line, word for word the fresh-cast text, so
    // only the screen around it tells a readout from a cast (GAME_MECHANICS "The
    // `stat` screen's buff readout is never a fresh cast"). Unbound, nothing counts
    // as a readout but Paradigm's countdown form.
    private Func<bool>? _inStatScreen;
    public void SetStatScreenProbe(Func<bool> inStatScreen) => _inStatScreen = inStatScreen;

    // Several spells print one applied line, and all of their records used to latch
    // on it whichever was cast. With this set, a record whose spell only monsters and
    // rooms cast, none of them here, is left out when another of the matched records
    // is one the room or a monster in it does cast (KeepWhatCouldBeCastHere).
    public void SetCastReach(CastReach reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        _castReach = reach;
    }

    // One applied line KeepWhatCouldBeCastHere left records out of: the records
    // cast here, with what casts them, and the ones left out, with their flags.
    public sealed record Narrowing(DateTimeOffset At, string Line, string Kept, string LeftOut);

    // The last such line, for the bug report; null until one is narrowed.
    public Narrowing? LastNarrowed { get; private set; }

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

    // True when any currently-active record satisfies match.
    public bool AnyActive(Func<MessageRecord, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        foreach (MessageRecord r in _messages.Messages)
            if (_active.Contains(r.Id) && match(r)) return true;
        return false;
    }

    // The records active on us, each with when it latched and the line that latched
    // it (null for both when that went unrecorded) and, for an ailment, when it
    // expires with no wear-off seen, for the bug report.
    public IReadOnlyList<(MessageRecord Record, DateTimeOffset? At, string? Line, DateTimeOffset? Expires)> ActiveRecords()
    {
        List<(MessageRecord, DateTimeOffset?, string?, DateTimeOffset?)> records = new();
        foreach (MessageRecord r in _messages.Messages)
        {
            if (!_active.Contains(r.Id)) continue;
            DateTimeOffset? expires = _expiresAt.TryGetValue(r.Id, out DateTimeOffset until) ? until : null;
            records.Add(_latchedBy.TryGetValue(r.Id, out (DateTimeOffset At, string Line) by)
                ? (r, by.At, by.Line, expires) : (r, null, null, expires));
        }
        return records;
    }

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
        if (string.IsNullOrEmpty(text)) return false;
        string norm = NormalizeFumbleLine(text);
        return norm.Length > 0 && (MatchConfuseFumble(norm) is not null || _realmFailure?.Invoke(text) is not null);
    }

    // The set's own failure lines (RealmEventLines.FailureOf): a set exported with a
    // Messages table names every effect's failure wording ("You are fast asleep.",
    // "You are too afraid to do that!"), where the catalogue's Confused records know
    // a few. Each is an eaten command like a confusion fumble, and is re-sent the same
    // way (user, 2026-11-14: "re-send every time").
    private Func<string, MessageRecord?>? _realmFailure;
    public void SetRealmFailureLines(Func<string, MessageRecord?> failureOf) => _realmFailure = failureOf;

    // True when text is the applied line of a hold we are under. Paradigm answers a
    // held character's move by printing the hold's own line again ("You are too
    // scared to flee!"), so a repeat of it is a move refusal as well as a status
    // (GAME_MECHANICS "Moving while held"). Whole line only, as for a fumble line.
    public bool IsActiveHoldLine(string text)
    {
        if (_active.Count == 0 || string.IsNullOrEmpty(text)) return false;
        string norm = NormalizeFumbleLine(text);
        if (norm.Length == 0) return false;
        foreach ((string pattern, MessageRecord r) in _appliedIndex)
        {
            if (!r.Flags.HasFlag(MessageFlags.MovementPrevented) || !_active.Contains(r.Id)) continue;
            if (string.Equals(norm, NormalizeFumbleLine(pattern), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The Confused record whose ConfuseFumbleLine contains normalizedText, or null.
    // Shared by IsConfuseFumbleLine and OnLine's ActionFailed detection so both
    // read the same index the same way.
    private MessageRecord? MatchConfuseFumble(string normalizedText)
    {
        foreach ((string wording, MessageRecord record) in _confuseFumbleIndex)
            if (string.Equals(normalizedText, wording, StringComparison.OrdinalIgnoreCase))
                return record;
        return null;
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
        _expiresAt.Clear();
        if (_active.Count == 0) return;
        _active.Clear();
        RecomputeFlags();
        _log?.Info(LogCategory, $"all conditions cleared ({reason})");
    }

    // Drop every active record carrying any of the given flags. For outside proof
    // that an ailment is gone when its wear-off line never arrived (a move going
    // through while MovementPrevented is latched). Like ClearAll it is a safe
    // over-clear: a condition that is really still on re-latches on its next line.
    // No ConditionEnded fires — the buff timers that event feeds anchor on a
    // record's own wear-off line, same as the collateral clears in OnLine.
    // onlyWholly leaves a record that also carries a flag the proof says nothing about
    // (a breath that blinds and confuses, when only sight is proven): dropping it would
    // end the other condition too, and its onset line won't come again.
    public void ClearFlag(MessageFlags flags, string reason, bool onlyWholly = false)
    {
        if ((ActiveFlags & flags) == MessageFlags.None) return;
        List<string> names = new();
        foreach (MessageRecord r in _messages.Messages)
            if ((r.Flags & flags) != MessageFlags.None
                && (!onlyWholly || (r.Flags & ~flags) == MessageFlags.None)
                && _active.Remove(r.Id))
                names.Add(r.Name);
        RecomputeFlags();
        // Each latch carrying the flag also carried another one (onlyWholly): nothing
        // was cleared, so nothing is logged.
        if (names.Count == 0) return;
        _log?.Info(LogCategory,
            $"{flags} cleared ({reason}) — dropped {names.Count} record(s): {string.Join(", ", names)}");
    }

    // The store fires one Reset for a bulk (re)load, so this rebuilds once per set
    // switch rather than once per record — see BulkObservableCollection.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildIndex();

    private void RebuildIndex()
    {
        List<(string, MessageRecord)> applied = new();
        List<(string, MessageRecord)> ends = new();
        Dictionary<string, List<MessageRecord>> aliases = new(StringComparer.Ordinal);
        List<(string Norm, MessageRecord Record)> fumbles = new();
        Dictionary<string, List<MessageRecord>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRecord r in _messages.Messages)
        {
            // 'Disabled (don't use)' — the record never indexes here, so it can't
            // latch a condition, set a flag, contribute a fumble wording, or fire
            // ActionFailed. Any active copy is dropped in the prune below. Its cast
            // lines still own what they hold (BuildCastLinesHolding).
            if (r.Flags.HasFlag(MessageFlags.Disabled)) continue;

            string name = r.Name.Trim();
            if (name.Length > 0)
            {
                if (!byName.TryGetValue(name, out List<MessageRecord>? named))
                    byName[name] = named = new List<MessageRecord>();
                named.Add(r);
            }

            // A slot holding a {null}/{void}/{empty} sentinel means "this spell has no such
            // line" — treat it as absent so it never compiles into a matcher (IsBlankOrAbsent),
            // exactly as an empty slot would. Only real wording indexes.
            // A present-but-tiny pattern ("n"/"E" from a corrupt import) would Contains-match
            // almost every line — never index it, and warn so the bad record is findable.
            if (!MessageRecord.IsBlankOrAbsent(r.AppliedMessage))
            {
                if (MessageRecord.IsTooShortToMatch(r.AppliedMessage))
                    _log?.Warn(LogCategory,
                        $"record '{r.Name}' (id {r.Id}) has a too-short applied pattern '{r.AppliedMessage.Trim()}' — not indexing it; a Contains-match on so few characters fires on unrelated lines (corrupt import data). Fix or clear this record's Applied slot.");
                else
                {
                    applied.Add((r.AppliedMessage, r));
                    if (!aliases.TryGetValue(r.AppliedMessage, out List<MessageRecord>? group))
                        aliases[r.AppliedMessage] = group = new List<MessageRecord>();
                    group.Add(r);
                }
            }
            if (!MessageRecord.IsBlankOrAbsent(r.AppliedEndsWith))
            {
                if (MessageRecord.IsTooShortToMatch(r.AppliedEndsWith))
                    _log?.Warn(LogCategory,
                        $"record '{r.Name}' (id {r.Id}) has a too-short wear-off pattern '{r.AppliedEndsWith.Trim()}' — not indexing it (corrupt import data). Fix or clear this record's Wears-off slot.");
                else
                    ends.Add((r.AppliedEndsWith, r));
            }
            if (r.Flags.HasFlag(MessageFlags.Confused) && !MessageRecord.IsBlankOrAbsent(r.ConfuseFumbleLine))
                foreach (string wording in r.ConfuseFumbleLine.Split('\n'))
                {
                    if (MessageRecord.IsAbsentSentinel(wording)) continue;
                    string norm = NormalizeFumbleLine(wording);
                    if (norm.Length > 0) fumbles.Add((norm, r));
                }
        }
        _appliedIndex = applied;
        _endsIndex = ends;
        _appliedAliases = aliases;
        BuildCastLineIndex(applied);
        _longerApplied = BuildLongerApplied(aliases.Keys);
        _castLinesHolding = BuildCastLinesHolding(applied, _messages.Messages);
        _confuseFumbleIndex = fumbles;
        _recordsByName = byName;
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

    // The same message with or without its closing punctuation ("You are enveloped
    // in darkness" / "…darkness!").
    private static string ApplyTextKey(string applied) => applied.Trim().TrimEnd('!', '.', ' ');

    // Index the cast lines of the records whose applied text is shared by records
    // that don't all carry the same flags.
    private void BuildCastLineIndex(List<(string Pattern, MessageRecord Record)> applied)
    {
        var byText = new Dictionary<string, List<MessageRecord>>(StringComparer.Ordinal);
        foreach ((string pattern, MessageRecord r) in applied)
        {
            string key = ApplyTextKey(pattern);
            if (!byText.TryGetValue(key, out List<MessageRecord>? group)) byText[key] = group = new List<MessageRecord>();
            group.Add(r);
        }

        List<(CasterMessageMatcher?, string, string)> lines = new();
        HashSet<string> ambiguous = new(StringComparer.Ordinal);
        foreach (List<MessageRecord> group in byText.Values)
        {
            bool mixed = false;
            foreach (MessageRecord r in group)
                if (r.Flags != group[0].Flags) { mixed = true; break; }
            if (!mixed) continue;
            foreach (MessageRecord r in group)
            {
                ambiguous.Add(r.Id);
                foreach (string template in new[] { r.TargetMessage, r.WitnessMessage, r.CasterMessage })
                {
                    if (MessageRecord.IsBlankOrAbsent(template) || MessageRecord.IsTooShortToMatch(template)) continue;
                    lines.Add((CasterMessageMatcher.TryCreate(template, compiled: false), template.Trim(), r.Id));
                }
            }
        }
        _castLineIndex = lines;
        _ambiguousApplied = ambiguous;
        _castSeenAt.Clear();
        _namedCaster = null;
    }

    // The conditions KeepWhatCouldBeCastHere never leaves out on the roster's word.
    // A caster has to be in the room for its spell to land (user, 2026-10-11:
    // "monsters or players can only affect you with spells/lines, IF they are in
    // your room"), but a few monsters follow in with no line at all (the same day:
    // "only very very few monsters in the game can follow you silently", the dark
    // goblin archer for one), and nothing in the game data is known to say which.
    // So the roster can never be proven whole, and what stops the character moving,
    // fighting or seeing is not ruled out by it: missed, such a condition costs far
    // more than one latched in error, which its wear-off or Reset States clears.
    private const MessageFlags NeverNarrowed =
        MessageFlags.MovementPrevented | MessageFlags.AttackPrevented | MessageFlags.Blinded;

    // The record's own cast line came just ahead of the line being read. Only records
    // whose applied text is shared across differing flags have their cast lines
    // watched (BuildCastLineIndex).
    private bool CastLineJustSeen(MessageRecord record, DateTimeOffset when) =>
        _ambiguousApplied.Contains(record.Id)
        && _castSeenAt.TryGetValue(record.Id, out DateTimeOffset seen) && when - seen <= CastLineWindow;

    // Of the records one applied line matched, the ones to leave unlatched: a record
    // sharing its applied text with another whose cast line was just seen, when its
    // own was not. Null when no cast line was seen, and every match latches.
    private HashSet<string>? OutrankedByAnnouncedCast(List<MessageRecord> matched, DateTimeOffset when)
    {
        List<MessageRecord>? announced = null;
        foreach (MessageRecord r in matched)
            if (CastLineJustSeen(r, when))
                (announced ??= new List<MessageRecord>()).Add(r);
        if (announced is null) return null;

        HashSet<string>? outranked = null;
        foreach (MessageRecord r in matched)
        {
            if (!_ambiguousApplied.Contains(r.Id) || announced.Contains(r)) continue;
            string key = ApplyTextKey(r.AppliedMessage);
            foreach (MessageRecord a in announced)
                if (ApplyTextKey(a.AppliedMessage) == key)
                {
                    (outranked ??= new HashSet<string>(StringComparer.Ordinal)).Add(r.Id);
                    break;
                }
        }
        if (outranked is not null)
        {
            _log?.Info(LogCategory,
                $"applied line shared by several spells — its cast line says {string.Join(" / ", announced.ConvertAll(a => $"'{a.Name}'"))}; "
                + $"not applying {string.Join(", ", matched.FindAll(m => outranked.Contains(m.Id)).ConvertAll(m => $"'{m.Name}' ({m.Flags})"))}");
        }
        return outranked;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        SweepExpired(line.Timestamp);
        if (line.IsPromptLine) return;
        string text = line.Text;
        if (string.IsNullOrEmpty(text)) return;

        // A cast the target threw off names its spell and brings none of its effect:
        // nothing on such a line starts or ends a condition.
        if (NegatedCastLine.TryRead(text, out NegatedCastLine.Cast negated))
        {
            LogNegatedCast(negated);
            return;
        }

        HashSet<string>? casters = null;
        foreach ((CasterMessageMatcher? template, string literal, string recordId) in _castLineIndex)
        {
            if (template is null)
            {
                if (!text.Contains(literal, StringComparison.Ordinal)) continue;
            }
            else if (!template.TryMatchCaptures(text, out MessageCaptures captures)) continue;
            else if (captures.Source is { Length: > 0 } source
                     && !source.Equals("You", StringComparison.OrdinalIgnoreCase))
                (casters ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(source);
            _castSeenAt[recordId] = line.Timestamp;
        }
        if (casters is not null)
        {
            _namedCaster = casters.Count == 1 ? (casters.First(), line.Timestamp) : null;
            foreach (string caster in casters) CasterNamed?.Invoke(caster, text);
        }

        // EndsWith first — if a condition both starts and ends with
        // overlapping text on the same line (rare but possible), the
        // end side wins so we don't latch a phantom-applied state.
        // Flags are recomputed BEFORE the events fire so subscribers
        // (CastingDirector's cure path) read the post-transition
        // state when they re-evaluate.
        List<MessageRecord> endedThisLine = new();
        List<MessageRecord> appliedThisLine = new();
        // A clear can release records without raising a ConditionEnded; the aggregate
        // flags still have to follow the active set.
        bool activeTouched = false;

        // Every record whose own wear-off this is, and that is latched, ends first,
        // before any collateral clear below: a record earlier in the list that shares
        // the wear-off text but isn't latched would otherwise clear its applied-line
        // aliases, a latched one among them, before that one's own turn, and its
        // ConditionEnded would never fire (greater zeal shares its applied and wear-off
        // lines with zeal; released from zeal's latch, its wear-off after unholy
        // fanaticism knocked it off left its timer running, user 2026-11-18).
        List<MessageRecord> wearOffRecords = new();
        foreach ((string pattern, MessageRecord r) in _endsIndex)
        {
            if (!text.Contains(pattern, StringComparison.Ordinal)) continue;
            wearOffRecords.Add(r);
            // ConditionEnded stays gated on the record having been latched: the
            // self-buff recast timers consume it, and a record never latched would
            // tear down the timer of a buff that is still up.
            if (_active.Remove(r.Id))
            {
                activeTouched = true;
                endedThisLine.Add(r);   // PRIMARY: its OWN wear-off line fired
            }
        }

        foreach (MessageRecord r in wearOffRecords)
        {
            // The wear-off line arriving is the event, whether or not this record is
            // the one latched: an applied line and the wear-off the game prints can
            // sit on different records that share a flag, and a flag nothing clears
            // stands until ClearAll or, for an ailment, its spell's time running out
            // (AilmentSeconds). The flag-wide clear below runs either way.

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
                    if (alias.Id != r.Id && _active.Remove(alias.Id))
                        activeTouched = true;
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
                    if ((other.Flags & r.Flags) != MessageFlags.None
                        && _active.Remove(other.Id))
                        activeTouched = true;
            }
        }

        // A `stat` status readout of an ALREADY-up effect is NOT a fresh cast: Paradigm's
        // "You feel X! (Ns)" carries a remaining-time tail, Stock's is the bare applied
        // line inside the stat screen. Its effect text is shared across many records
        // (one line names 11), so it can neither identify which buff is up nor
        // legitimately "apply" one — matching it falsely latched buffs on login, armed
        // their timers at full duration, and suppressed the real cast's confirm. Buff
        // timers anchor on the typed cast code instead.
        MessageRecord? actionFailed = null;
        if (!StatusEffectLine.HasCountdown(text) && _inStatScreen?.Invoke() != true)
        {
            List<MessageRecord> matched = new();
            List<(MessageRecord Record, HoldingLine Line)>? shadowed = null;
            foreach ((string pattern, MessageRecord r) in _appliedIndex)
            {
                if (!text.Contains(pattern, StringComparison.Ordinal)) continue;
                if (IsPartOfALongerMessage(text, pattern)) continue;
                if (CastLineOfAnotherSpell(text, pattern, r) is { } castLine)
                {
                    (shadowed ??= new()).Add((r, castLine));
                    continue;
                }
                matched.Add(r);
            }
            if (shadowed is not null) LogShadowed(shadowed);
            // The spell's own cast line is the better witness, so it speaks first: who
            // could have cast what is asked only of the records it leaves standing.
            HashSet<string>? outranked = matched.Count > 1 && _ambiguousApplied.Count > 0
                ? OutrankedByAnnouncedCast(matched, line.Timestamp) : null;
            if (outranked is not null) matched = matched.FindAll(r => !outranked.Contains(r.Id));
            if (matched.Count > 1 && _castReach is { } reach)
                matched = KeepWhatCouldBeCastHere(matched, reach, text, line.Timestamp);
            foreach (MessageRecord r in matched)
            {
                // Capture a LastActionFailed match BEFORE the active-set dedup below —
                // a failure line can recur while its record stays "applied" only once,
                // so ActionFailed must ride the raw line, not the deduped apply. First
                // match only; alias records sharing the line must not double the retry.
                if (actionFailed is null && r.Flags.HasFlag(MessageFlags.LastActionFailed))
                    actionFailed = r;
                if (!_active.Add(r.Id))
                {
                    ArmExpiry(r, line.Timestamp);
                    continue;
                }
                _latchedBy[r.Id] = (line.Timestamp, text);
                ArmExpiry(r, line.Timestamp);
                appliedThisLine.Add(r);
            }
        }

        // A confusion fumble (ConfuseFumbleLine) is itself an eaten command — the
        // same outcome LastActionFailed exists to signal — so it fires ActionFailed
        // the same way, without needing a second record to duplicate the wording
        // under that flag. Only checked when nothing above already claimed the
        // line, matching the existing first-match-only rule.
        if (actionFailed is null)
        {
            string norm = NormalizeFumbleLine(text);
            if (norm.Length > 0) actionFailed = MatchConfuseFumble(norm);
        }
        // A line that has just started an effect isn't read as a failure on top.
        if (actionFailed is null && appliedThisLine.Count == 0)
            actionFailed = _realmFailure?.Invoke(text);

        if (endedThisLine.Count > 0 || appliedThisLine.Count > 0 || activeTouched)
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

    // Says in the log which spell a negated cast was, by the message record of the
    // spell's name and the spells it is for; a name no record has is said so. A name
    // several records carry (hundreds do, on either realm) is only counted: listing
    // them all says no more of which one was cast.
    private void LogNegatedCast(NegatedCastLine.Cast cast)
    {
        if (_log is not { IsDebugEnabled: true }) return;
        string records = !_recordsByName.TryGetValue(cast.Spell.Trim(), out List<MessageRecord>? named)
            ? "no spell record of that name"
            : named.Count == 1
                ? "spell record " + DescribeRecordSpells(named[0])
                : $"{named.Count} spell records carry that name";
        _log.Debug(LogCategory,
            $"negated cast — {cast.Caster} cast '{cast.Spell}' at {cast.Target} and it did nothing "
            + $"({records}); no condition applied");
    }

    private static string DescribeRecordSpells(MessageRecord record)
    {
        IEnumerable<string> spells = (record.Links ?? Array.Empty<GameDataLink>())
            .Where(static link => link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase))
            .Select(static link => $"#{link.Number}");
        string numbers = string.Join(" ", spells);
        return numbers.Length > 0 ? $"'{record.Name}' ({numbers})" : $"'{record.Name}'";
    }

    // The applied text matched, but only as part of a longer applied text the line
    // also holds: the line is that longer message, and this record's condition was
    // never applied. "You are stunned by electrical shock!" (a hold) holds "You are
    // stunned" (the applied text of the confusion stuns), which latched Confused with
    // no wear-off of its own ever to come; "You are engulfed in flames!" (damage only)
    // holds "You are engulfed" (a hold) the same way. A longer text that only adds
    // punctuation ("You are blind" / "You are blind!") is the same message and was
    // never indexed as longer; a shorter text that also stands on its own elsewhere
    // in the line still counts.
    //
    // This half compares applied texts with each other. The cast lines that hold an
    // applied text are CastLineOfAnotherSpell's, which has to tell a spell's own cast
    // line from another's.
    private bool IsPartOfALongerMessage(string text, string pattern)
    {
        if (!_longerApplied.TryGetValue(pattern, out string[]? longer)) return false;
        foreach (string message in longer)
        {
            if (!text.Contains(message, StringComparison.Ordinal)) continue;
            if (StandsOutside(text, pattern, message)) continue;
            _log?.Debug(LogCategory, $"applied text '{pattern}' matched only inside '{message}' — not latched");
            return true;
        }
        return false;
    }

    // For each applied text, the other applied texts that hold it and add words to it.
    private static Dictionary<string, string[]> BuildLongerApplied(IReadOnlyCollection<string> appliedTexts)
    {
        Dictionary<string, string[]> result = new(StringComparer.Ordinal);
        foreach (string applied in appliedTexts)
        {
            List<string>? longer = null;
            foreach (string other in appliedTexts)
            {
                if (other.Length <= applied.Length) continue;
                int at = other.IndexOf(applied, StringComparison.Ordinal);
                if (at < 0 || !AddsWords(other, at, applied.Length)) continue;
                (longer ??= new List<string>()).Add(other);
            }
            if (longer is not null) result[applied] = longer.ToArray();
        }
        return result;
    }

    // The longer text has a letter or digit outside the span the shorter one covers.
    private static bool AddsWords(string longer, int at, int length)
    {
        for (int i = 0; i < longer.Length; i++)
            if ((i < at || i >= at + length) && char.IsLetterOrDigit(longer[i])) return true;
        return false;
    }

    // The shorter text occurs in the line somewhere not covered by the longer one.
    private static bool StandsOutside(string text, string shorter, string longer)
    {
        for (int i = text.IndexOf(shorter, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(shorter, i + 1, StringComparison.Ordinal))
        {
            bool covered = false;
            for (int j = text.IndexOf(longer, StringComparison.Ordinal); j >= 0 && !covered;
                 j = text.IndexOf(longer, j + 1, StringComparison.Ordinal))
                covered = i >= j && i + shorter.Length <= j + longer.Length;
            if (!covered) return true;
        }
        return false;
    }

    // The shorter text occurs in the line somewhere outside the span given.
    private static bool StandsOutside(string text, string shorter, int start, int length)
    {
        for (int i = text.IndexOf(shorter, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(shorter, i + 1, StringComparison.Ordinal))
            if (i < start || i + shorter.Length > start + length) return true;
        return false;
    }

    // A cast, target or witness line that holds an applied text with more words
    // around it. Template is set when the line carries placeholders; Owners names the
    // records it belongs to, for the log.
    private sealed record HoldingLine(string Text, CasterMessageMatcher? Template, string Owners);

    // The applied text matched, but the line is the cast line of a different spell,
    // which holds that text among its own words: the room spell freezing water prints
    // "You are freezing cold!", and "You are freezing" is the applied text of glacial
    // mist (a confusion) and the ice storms. Every one of them latched in the
    // Underwater Passage rooms, and Confused stood with no wear-off ever to come
    // (report paradigm-20261011-165726). The line is that other spell's message, and
    // this record's condition was never applied. As with a longer applied text, the
    // shorter one standing by itself elsewhere in the line still counts.
    //
    // Null for a line of the record's own spell, which is where its condition is
    // rightly read ("You are mesmerized by the movements!"): BuildCastLinesHolding
    // leaves those out.
    private HoldingLine? CastLineOfAnotherSpell(string text, string pattern, MessageRecord record)
    {
        if (!_castLinesHolding.TryGetValue(record, out HoldingLine[]? lines)) return null;
        foreach (HoldingLine line in lines)
        {
            if (line.Template is { } template)
            {
                if (!template.TryMatchSpan(text, out int start, out int length)) continue;
                if (StandsOutside(text, pattern, start, length)) continue;
            }
            else
            {
                if (!text.Contains(line.Text, StringComparison.Ordinal)) continue;
                if (StandsOutside(text, pattern, line.Text)) continue;
            }
            return line;
        }
        return null;
    }

    private void LogShadowed(List<(MessageRecord Record, HoldingLine Line)> shadowed)
    {
        if (_log is not { IsDebugEnabled: true }) return;
        foreach (IGrouping<HoldingLine, (MessageRecord Record, HoldingLine Line)> byLine in shadowed.GroupBy(s => s.Line))
            _log.Debug(LogCategory,
                $"applied text '{byLine.First().Record.AppliedMessage}' matched only inside '{byLine.Key.Text}', "
                + $"the line of {byLine.Key.Owners} — not latched: {string.Join(", ", byLine.Select(s => $"'{s.Record.Name}'").Distinct())}");
    }

    // For each record with an applied text, the cast, target and witness lines of
    // OTHER spells that hold that text and add words to it. A line is left out when
    // any record carrying it is the record itself, is for one of the same spells,
    // has the same applied text or carries the same conditions (IsTheSameEffect):
    // then the line is this effect's own announcement (web entangle's "You are
    // entangled in sticky strands of webbing." entangles as surely as the other
    // entangles' "You are entangled").
    //
    // Each cast line is walked once and looked up by the opening characters of the
    // applied texts, so a rebuild stays a few milliseconds over the whole catalogue.
    private static Dictionary<MessageRecord, HoldingLine[]> BuildCastLinesHolding(
        List<(string Pattern, MessageRecord Record)> applied, IEnumerable<MessageRecord> records)
    {
        Dictionary<MessageRecord, HoldingLine[]> result = new(ReferenceEqualityComparer.Instance);
        if (applied.Count == 0) return result;

        // An indexed applied text is never shorter than this (IsTooShortToMatch).
        const int prefixLength = MessageRecord.MinRecognitionPatternLength;
        Dictionary<string, List<string>> appliedByPrefix = new(StringComparer.Ordinal);
        foreach ((string pattern, _) in applied)
        {
            string prefix = pattern[..prefixLength];
            if (!appliedByPrefix.TryGetValue(prefix, out List<string>? texts))
                appliedByPrefix[prefix] = texts = new List<string>();
            if (!texts.Contains(pattern)) texts.Add(pattern);
        }
        Dictionary<string, List<string>>.AlternateLookup<ReadOnlySpan<char>> byPrefix =
            appliedByPrefix.GetAlternateLookup<ReadOnlySpan<char>>();

        // A record switched off in Game Data still owns its lines: the line is that
        // spell's whether or not the record acts on it, and switching off freezing
        // water must not let the mists latch on its "You are freezing cold!" again.
        Dictionary<string, List<MessageRecord>> owners = new(StringComparer.Ordinal);
        foreach (MessageRecord r in records)
        {
            foreach (string? slot in new[] { r.CasterMessage, r.TargetMessage, r.WitnessMessage })
            {
                if (MessageRecord.IsBlankOrAbsent(slot)) continue;
                // A witness slot can hold several wordings, one to a line.
                foreach (string wording in slot!.Split('\n'))
                {
                    if (MessageRecord.IsBlankOrAbsent(wording) || MessageRecord.IsTooShortToMatch(wording)) continue;
                    string line = wording.Trim();
                    if (!owners.TryGetValue(line, out List<MessageRecord>? from))
                        owners[line] = from = new List<MessageRecord>();
                    if (!from.Contains(r)) from.Add(r);
                }
            }
        }

        Dictionary<string, List<string>> holding = new(StringComparer.Ordinal);
        foreach (string line in owners.Keys)
            for (int at = 0; at + prefixLength <= line.Length; at++)
            {
                if (!byPrefix.TryGetValue(line.AsSpan(at, prefixLength), out List<string>? texts)) continue;
                foreach (string text in texts)
                {
                    if (line.Length <= text.Length
                        || !line.AsSpan(at).StartsWith(text, StringComparison.Ordinal)
                        || !AddsWords(line, at, text.Length)) continue;
                    if (!holding.TryGetValue(text, out List<string>? lines))
                        holding[text] = lines = new List<string>();
                    if (!lines.Contains(line)) lines.Add(line);
                }
            }

        Dictionary<string, HoldingLine> built = new(StringComparer.Ordinal);
        foreach ((string pattern, MessageRecord record) in applied)
        {
            if (!holding.TryGetValue(pattern, out List<string>? lines)) continue;
            List<HoldingLine>? others = null;
            foreach (string line in lines)
            {
                List<MessageRecord> from = owners[line];
                if (from.Exists(owner => IsTheSameEffect(owner, record))) continue;
                if (!built.TryGetValue(line, out HoldingLine? holder))
                    built[line] = holder = new HoldingLine(
                        line, CasterMessageMatcher.TryCreate(line, compiled: false),
                        string.Join(" / ", from.Select(o => $"'{o.Name}'").Distinct()));
                (others ??= new List<HoldingLine>()).Add(holder);
            }
            if (others is not null) result[record] = others.ToArray();
        }
        return result;
    }

    // Whether a line carried by owner brings on what record stands for: the record
    // itself, a record for one of the same spells, one with the same applied text,
    // or one that carries every condition record does.
    //
    // The last is for a line whose own spell really applies that condition: hypnotic
    // hands' "You are mesmerized by the hypnotic movements!" holds the applied text
    // of mesmerize and strut, and all three are confusions, so the line confuses
    // whichever record it is read through. Its own applied line ("You are
    // confused!") does not always follow at once (user, 2026-10-11), and waiting for
    // it left the character confused and not known to be. A record with no condition
    // has nothing a line could bring on in its place, so it is not let through this
    // way: freezing water's line still latches no ice storm.
    private static bool IsTheSameEffect(MessageRecord owner, MessageRecord record)
    {
        if (ReferenceEquals(owner, record) || owner.Id == record.Id) return true;
        if (!MessageRecord.IsBlankOrAbsent(owner.AppliedMessage)
            && ApplyTextKey(owner.AppliedMessage) == ApplyTextKey(record.AppliedMessage)) return true;
        MessageFlags conditions = record.Flags & ConditionFlags;
        if (conditions != MessageFlags.None && (owner.Flags & conditions) == conditions) return true;
        if (owner.Links is null || record.Links is null) return false;
        foreach (GameDataLink link in owner.Links)
            if (record.Links.Contains(link)) return true;
        return false;
    }

    // The flags that say what state a line puts the character in. The others say
    // what to do about a line (resend the action) or switch a record off.
    private const MessageFlags ConditionFlags =
        MessageFlags.Blinded | MessageFlags.Confused | MessageFlags.Poisoned | MessageFlags.Fear
        | MessageFlags.MovementPrevented | MessageFlags.AttackPrevented | MessageFlags.Diseased;

    // Of the records one applied line matched, the ones a caster here could have
    // applied. Several spells print the same applied line, and every record carrying
    // it used to latch whichever was cast, their flags added together: an ice storm
    // set Confused through the mists' records. Who can cast a spell is in the game
    // data, and the room and the monsters in it are known, so a record is left out
    // when its spell is cast only by monsters and rooms, none of them here, AND some
    // other matched record is one this room or a monster in it does cast. With no
    // caster in sight for any of them nothing is left out (a dark room, a hidden
    // monster), and a spell an item or a textblock can also cast is never left out.
    // A player's spell is in reach only of a player outside the party on a PvP realm
    // when it is hostile, and is never left out otherwise (CastReach). (User,
    // 2026-10-11: "it should also be able to discriminate spells being cast based on
    // monsters in the room, spells they have available and what spells the room
    // casts as well".)
    //
    // Two kinds of record are never left out, whoever is here. One that holds,
    // stops attacks or blinds (NeverNarrowed). And one whose own cast line was just
    // seen while another's was not: the line says which was cast, and that outweighs
    // who is listed. A cast line every one of them shares says nothing of which, so
    // it protects none of them (the three plagues print one line).
    //
    // A cast line that names its caster narrows further. When the monster it names is
    // on the roster and casts one of the matched spells, the records it doesn't cast
    // are left out, holds included: the caster is known, so no silent follower can be
    // the one who cast it (user, 2026-11-24; report stock-20261124-101930, where "fierce
    // dark priest casts a spell on you!" held the character through wrathful curse,
    // which no dark priest casts).
    //
    // A narrowed line is logged at Info, once for the line: what it leaves out is a
    // condition the client then does not hold, and a report has to show why. The
    // same narrowing again within a minute is logged at Debug.
    private List<MessageRecord> KeepWhatCouldBeCastHere(
        List<MessageRecord> matched, CastReach reach, string text, DateTimeOffset when)
    {
        if (reach.Now() is not { } here) return matched;

        bool castLineTellsApart = matched.Exists(r => CastLineJustSeen(r, when))
            && matched.Exists(r => !CastLineJustSeen(r, when));
        string? named = _namedCaster is { } caster && when - caster.At <= CastLineWindow
            ? CastReach.MonsterNamed(here, caster.Name) : null;
        if (named is not null && !matched.Exists(r => reach.OfCaster(r, named) == CastReach.Verdict.InReach))
            named = null;
        List<string>? castHere = null;
        HashSet<MessageRecord>? outOfReach = null;
        foreach (MessageRecord r in matched)
        {
            CastReach.Verdict byNamed = named is null ? CastReach.Verdict.Unknown : reach.OfCaster(r, named);
            if (byNamed == CastReach.Verdict.InReach)
            {
                (castHere ??= new List<string>()).Add($"'{r.Name}' ({named}, named in the cast line)");
                continue;
            }
            if (byNamed == CastReach.Verdict.OutOfReach)
            {
                if (castLineTellsApart && CastLineJustSeen(r, when)) continue;
                (outOfReach ??= new HashSet<MessageRecord>(ReferenceEqualityComparer.Instance)).Add(r);
                continue;
            }
            switch (reach.Of(r, here, out string? by))
            {
                case CastReach.Verdict.InReach:
                    (castHere ??= new List<string>()).Add($"'{r.Name}' ({by})");
                    break;
                case CastReach.Verdict.OutOfReach:
                    if ((r.Flags & NeverNarrowed) != MessageFlags.None) break;
                    if (castLineTellsApart && CastLineJustSeen(r, when)) break;
                    (outOfReach ??= new HashSet<MessageRecord>(ReferenceEqualityComparer.Instance)).Add(r);
                    break;
            }
        }
        if (castHere is null || outOfReach is null) return matched;

        Narrowing narrowed = new(when, text,
            string.Join(", ", castHere.Distinct()),
            string.Join(", ", outOfReach.Select(r => $"'{r.Name}' ({r.Flags})").Distinct()));
        // A room spell narrows the same line every tick; once at Info says it, and
        // the repeats in the window only add noise to a log read with Debug off.
        bool repeat = LastNarrowed is { } last
            && last.Line == narrowed.Line && last.Kept == narrowed.Kept && last.LeftOut == narrowed.LeftOut
            && when - last.At < NarrowingRepeatWindow;
        LastNarrowed = narrowed;
        string said = $"applied line '{text}' shared by several spells — cast here: {narrowed.Kept}; "
            + (named is null
                ? $"not applying, no monster, room or player outside the party that casts them being here: {narrowed.LeftOut}"
                : $"not applying what {named}, named in the cast line, doesn't cast: {narrowed.LeftOut}");
        if (repeat) _log?.Debug(LogCategory, said);
        else _log?.Info(LogCategory, said);
        return matched.FindAll(r => !outOfReach.Contains(r));
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

    // Drop the applied-latch for every currently-active record the predicate picks.
    // Called when a buff is clobber-cleared elsewhere (CastingDirector, when a landing
    // buff strips one it removes per RemovesSpell): the game removed the effect, so its
    // condition is no longer up. Without this the record stays in _active, and a later
    // RE-CAST of that same buff is deduped as "already applied" (see OnLine) — its
    // ConditionApplied never re-fires, so the re-cast can't drive its own clobber-clear.
    // That stranded a re-cast greater bless from ever dropping an active chant, because
    // gbls's applied-latch survived chant's earlier clobber of gbls
    // (report paradigm-20260910-012303). Matches against the applied index (the only
    // records that can be latched); RecomputeFlags after so a flag the released record
    // carried clears with it.
    public void ReleaseApplied(Func<MessageRecord, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_active.Count == 0) return;
        List<string>? drop = null;
        foreach ((_, MessageRecord r) in _appliedIndex)
            if (_active.Contains(r.Id) && match(r))
                (drop ??= new()).Add(r.Id);
        if (drop is null) return;
        foreach (string id in drop) _active.Remove(id);
        RecomputeFlags();
        _log?.Info(LogCategory, $"applied-latch released for {drop.Count} record(s) (clobber-cleared)");
    }

    // Latch as applied every record the predicate picks, with no applied event. For
    // a buff known to be on from before this log could have seen its applied line:
    // the link dropped and the log was cleared with it (ClearAll, which is right for
    // ailments and wrong for a buff, since buffs stay on the character while it is
    // out of the game), or the client was started since. The game prints no line for
    // a buff that is merely still on, so nothing would ever latch it again, and a
    // wear-off line ends only a latched record (several buffs share one wear-off
    // text): unlatched, the buff's own wear-off would end nothing and its timer would
    // run to the end over a buff that is gone.
    public void LatchApplied(Func<MessageRecord, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        List<string>? latched = null;
        foreach ((_, MessageRecord r) in _appliedIndex)
            if (match(r) && _active.Add(r.Id))
            {
                _latchedBy[r.Id] = (DateTimeOffset.Now, "(carried over from time out of the game)");
                (latched ??= new()).Add(r.Name);
            }
        if (latched is null) return;
        RecomputeFlags();
        _log?.Info(LogCategory,
            $"latched as applied, carried over time out of the game: {string.Join(", ", latched.Distinct())}");
    }

    private void RecomputeFlags()
    {
        // A record cleared by its wear-off, a cure or a reset keeps no expiry.
        if (_expiresAt.Count > 0)
            foreach (string id in _expiresAt.Keys.Where(id => !_active.Contains(id)).ToList())
                _expiresAt.Remove(id);
        MessageFlags flags = MessageFlags.None;
        foreach (MessageRecord r in _messages.Messages)
        {
            if (!_active.Contains(r.Id)) continue;
            flags |= r.Flags;
        }
        if (_gmcpFlags is { } known) flags = (flags & ~GmcpMask) | (known & GmcpMask);
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
