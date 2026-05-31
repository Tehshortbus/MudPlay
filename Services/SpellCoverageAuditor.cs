using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// Background audit that flags player-facing spells in the active
/// game-data set's <c>Spells.json</c> that have no corresponding
/// <see cref="MessageRecord"/> anchor — i.e. spells the game can
/// produce a line for, that we have no parser entry to recognise.
/// </summary>
/// <remarks>
/// <para>
/// Fires on every <see cref="GameDataCache.ActiveSetChanged"/> + every
/// <see cref="MessageStore.Messages"/> CollectionChanged so the
/// summary stays live as the user edits the catalogue. The
/// <see cref="ResultAvailable"/> event drives the report window;
/// the same compute also writes a summary <see cref="LogEntry"/> to
/// <see cref="LogService"/> tagged with <see cref="LogSource"/> so
/// the LogPane's double-click handler can route into the report
/// window via <see cref="LogService.TryInvokeDetailHandler"/>.
/// </para>
/// <para>
/// Player-facing filter (kept conservative to avoid spamming on
/// junk rows): row's Name is ≥3 chars and not all-digits AND at
/// least one of <c>Learnable==1</c>, <c>Casted By</c> non-empty,
/// <c>Learned From</c> non-empty. That matches the three ways a
/// player will ever see the spell fire — they cast it, an NPC casts
/// it at them, or an item procs it. Inverse direction (Spells/Items/
/// Monsters that DON'T need messages) is implicitly ignored.
/// </para>
/// </remarks>
public sealed class SpellCoverageAuditor
{
    /// <summary>Tag every entry the auditor emits uses on <see cref="LogService"/>.</summary>
    public const string LogSource = "GameData/Coverage";

    private readonly GameDataCache _cache;
    private readonly MessageStore _messages;
    private readonly LogService _log;
    /// <summary>
    /// Coalesce-flag for <see cref="QueueRun"/>: a set switch fires
    /// <see cref="GameDataCache.ActiveSetChanged"/> AND triggers
    /// <see cref="MessageStore.Load"/> which raises one
    /// Clear-Reset + N Add CollectionChanged events; without
    /// debouncing the audit would run N+2 times per swap and spam
    /// the LogPane. Posting once to the dispatcher and clearing the
    /// flag in the dispatched handler collapses the burst to a
    /// single run on the current UI tick.
    /// </summary>
    private bool _runQueued;

    /// <summary>The most recent result. <c>null</c> until the first audit runs (no set active).</summary>
    public CoverageResult? Latest { get; private set; }

    /// <summary>Fires after every audit run — populated <see cref="Latest"/> at fire time.</summary>
    public event Action<CoverageResult>? ResultAvailable;

    public SpellCoverageAuditor(GameDataCache cache, MessageStore messages, LogService log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(log);
        _cache    = cache;
        _messages = messages;
        _log      = log;

        cache.ActiveSetChanged              += _ => QueueRun();
        messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRun();

    /// <summary>
    /// Defer <see cref="Run"/> to the next dispatcher tick + collapse
    /// any further queued requests onto the same tick. Set switches
    /// always end up here; an MDB import lands here via the
    /// ActiveSetChanged that <c>SwitchActiveGameDataSet</c> raises
    /// after a successful import.
    /// </summary>
    private void QueueRun()
    {
        if (_runQueued) return;
        _runQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { _runQueued = false; Run(); },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Explicit re-audit hook for the report window's "Refresh" button + future MDB-import callers.</summary>
    public CoverageResult? Run()
    {
        string? set = _cache.ActiveSet;
        if (set is null)
        {
            Latest = null;
            return null;
        }

        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null)
        {
            Latest = new CoverageResult(set, 0, 0, Array.Empty<UnanchoredSpell>());
            ResultAvailable?.Invoke(Latest);
            return Latest;
        }

        // Build per-table anchor sets: every (Spells/Monsters/Items, N)
        // appearing in any Message's Links. A spell is "anchored" when
        // a Message points at the spell itself OR at any caster
        // (monster / item) of the spell. The user-facing drilldown
        // links to casters by preference (the player encounters the
        // monster, not the abstract spell), so a Monsters-only anchor
        // is enough to mark coverage as resolved.
        HashSet<int> anchoredSpells   = new();
        HashSet<int> anchoredMonsters = new();
        HashSet<int> anchoredItems    = new();
        foreach (MessageRecord m in _messages.Messages)
        {
            if (m.Links is null) continue;
            foreach (GameDataLink link in m.Links)
            {
                if (string.Equals(link.Table, "Spells",   StringComparison.OrdinalIgnoreCase)) anchoredSpells.Add(link.Number);
                if (string.Equals(link.Table, "Monsters", StringComparison.OrdinalIgnoreCase)) anchoredMonsters.Add(link.Number);
                if (string.Equals(link.Table, "Items",    StringComparison.OrdinalIgnoreCase)) anchoredItems.Add(link.Number);
            }
        }

        int considered = 0;
        List<UnanchoredSpell> gaps = new();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!IsPlayerFacing(row, out string name, out int number)) continue;
            considered++;

            // Direct anchor on the spell row itself.
            if (anchoredSpells.Contains(number)) continue;

            // Indirect anchor — any caster (monster / item) of this
            // spell is anchored, so the message text is covered.
            string? castedByRaw    = ReadString(row, "Casted By");
            string? learnedFromRaw = ReadString(row, "Learned From");
            if (AnyCasterAnchored(castedByRaw, anchoredMonsters, anchoredItems)) continue;
            if (AnyCasterAnchored(learnedFromRaw, anchoredMonsters, anchoredItems)) continue;

            gaps.Add(new UnanchoredSpell(
                Number:      number,
                Name:        name,
                CastedBy:    ResolveRefs(castedByRaw),
                LearnedFrom: ResolveRefs(learnedFromRaw),
                Classes:     ReadString(row, "Classes")));
        }

        gaps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Latest = new CoverageResult(set, considered, gaps.Count, gaps);

        _log.Log(LogSeverity.Info, LogSource,
            $"Active set '{set}': {gaps.Count} of {considered} player-facing spells have no Message anchor.  (double-click for details)");

        ResultAvailable?.Invoke(Latest);
        return Latest;
    }

    /// <summary>
    /// Filter: row is a player-facing spell candidate iff its Name is
    /// non-junk (≥3 chars, not all-digits) AND at least one of
    /// (Learnable==1, Casted By non-empty, Learned From non-empty)
    /// holds. The three OR-paths cover player-cast, NPC-cast-at-player,
    /// and item-proc respectively — everything else (test rows, internal
    /// NPC-only effects with no monster reference, deprecated entries)
    /// drops out and doesn't pollute the unanchored count.
    /// </summary>
    private static bool IsPlayerFacing(JsonElement row, out string name, out int number)
    {
        name = string.Empty;
        number = 0;
        if (!row.TryGetProperty("Number", out JsonElement numEl)) return false;
        if (numEl.ValueKind != JsonValueKind.Number || !numEl.TryGetInt32(out number)) return false;

        string? rawName = ReadString(row, "Name");
        if (string.IsNullOrWhiteSpace(rawName)) return false;
        if (rawName.Length < 3) return false;
        if (rawName.All(char.IsDigit)) return false;
        name = rawName;

        bool learnable = row.TryGetProperty("Learnable", out JsonElement lEl)
                      && lEl.ValueKind == JsonValueKind.Number
                      && lEl.TryGetInt32(out int li) && li == 1;
        bool castedBy   = HasContent(ReadString(row, "Casted By"));
        bool learnedFrom = HasContent(ReadString(row, "Learned From"));
        return learnable || castedBy || learnedFrom;
    }

    /// <summary>
    /// Plain-text content check that treats NULL-byte-only strings
    /// (<c>"\0"</c>) as empty. The MdbImporter writes a literal
    /// NUL char for empty Jet text columns instead of an empty string,
    /// so <see cref="string.IsNullOrWhiteSpace"/> isn't enough on its
    /// own — NUL isn't whitespace.
    /// </summary>
    private static bool HasContent(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (char c in s)
            if (c != '\0' && !char.IsWhiteSpace(c)) return true;
        return false;
    }

    private static string? ReadString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        string? raw = el.GetString();
        // Strip the all-NUL sentinel here too so display columns never
        // render "\0" / show as empty unexpectedly to the user.
        return HasContent(raw) ? raw : null;
    }

    /// <summary>
    /// Translate every <c>"Monster #N"</c> / <c>"Item #N"</c> /
    /// <c>"Spell #N"</c> reference in <paramref name="raw"/> to
    /// <c>"Resolved Name {N}"</c> using the active set's tables.
    /// References whose target row doesn't exist in the set are left
    /// verbatim so the gap is still visible to the user (rather than
    /// silently disappearing). <c>TextBlock</c> references are
    /// passed through untouched per user direction — block IDs are
    /// only meaningful when looked up directly.
    /// </summary>
    private string ResolveRefs(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        return RefPattern.Replace(raw, m =>
        {
            string typeName = m.Groups[1].Value;
            if (!int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int num))
            {
                return m.Value;
            }
            string table = typeName switch
            {
                "Monster" => "Monsters",
                "Item"    => "Items",
                "Spell"   => "Spells",
                _         => string.Empty,  // future / unknown — pass through verbatim
            };
            if (table.Length == 0) return m.Value;
            string? resolved = _cache.FindNameByNumber(table, num);
            return resolved is null ? m.Value : $"{resolved} {{{num}}}";
        });
    }

    /// <summary>
    /// Matches <c>"Monster #1234"</c> / <c>"Item #56"</c> /
    /// <c>"Spell #9"</c> anywhere in a string. Trailing-digit-greedy
    /// so multi-digit numbers don't get truncated, and the lookahead
    /// guards against gluing into a longer identifier.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RefPattern = new(
        @"\b(Monster|Item|Spell)\s*#\s*(\d+)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True when any Monster/Item ref in <paramref name="raw"/>
    /// already has a Message anchor in
    /// <paramref name="anchoredMonsters"/> /
    /// <paramref name="anchoredItems"/>. Lets the audit treat a spell
    /// as covered when the user has authored a message for one of
    /// its casters even without a direct (Spells, N) link.
    /// </summary>
    private static bool AnyCasterAnchored(
        string? raw,
        HashSet<int> anchoredMonsters,
        HashSet<int> anchoredItems)
    {
        foreach ((string table, int number) in ParseRefs(raw))
        {
            if (table == "Monsters" && anchoredMonsters.Contains(number)) return true;
            if (table == "Items"    && anchoredItems.Contains(number))    return true;
        }
        return false;
    }

    /// <summary>
    /// Parse every <c>"Monster #N"</c> / <c>"Item #N"</c> /
    /// <c>"Spell #N"</c> reference in <paramref name="raw"/> into
    /// structured <c>(Table, Number)</c> tuples. Used by the
    /// coverage report's drilldown to seed Links on a fresh Message
    /// record. Unknown / future reference types are skipped.
    /// </summary>
    public static IEnumerable<(string Table, int Number)> ParseRefs(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) yield break;
        foreach (System.Text.RegularExpressions.Match m in RefPattern.Matches(raw))
        {
            if (!int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int num))
                continue;
            string table = m.Groups[1].Value switch
            {
                "Monster" => "Monsters",
                "Item"    => "Items",
                "Spell"   => "Spells",
                _         => string.Empty,
            };
            if (table.Length == 0) continue;
            yield return (table, num);
        }
    }
}

/// <summary>Snapshot returned from one <see cref="SpellCoverageAuditor"/> run.</summary>
/// <param name="SetName">The active game-data set at audit time.</param>
/// <param name="ConsideredCount">How many spell rows passed the player-facing filter.</param>
/// <param name="UnanchoredCount">How many of those had no Message link pointing at them.</param>
/// <param name="Unanchored">The full list (sorted by Name) of unanchored spells.</param>
public sealed record CoverageResult(
    string                       SetName,
    int                          ConsideredCount,
    int                          UnanchoredCount,
    IReadOnlyList<UnanchoredSpell> Unanchored);

/// <summary>One row in <see cref="CoverageResult.Unanchored"/>.</summary>
public sealed record UnanchoredSpell(
    int     Number,
    string  Name,
    string? CastedBy,
    string? LearnedFrom,
    string? Classes);
