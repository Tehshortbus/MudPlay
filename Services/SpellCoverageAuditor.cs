using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

// Background audit that flags player-facing spells in the active game-data
// set's Spells.json that have no corresponding MessageRecord anchor — i.e.
// spells the game can produce a line for, that we have no parser entry to
// recognise.
//
// Fires on every GameDataCache.ActiveSetChanged + every MessageStore.Messages
// CollectionChanged so the summary stays live as the user edits the catalogue.
// The ResultAvailable event drives the report window; the same compute also
// writes a summary LogEntry to LogService tagged with LogSource so the LogPane's
// double-click handler can route into the report window via
// LogService.TryInvokeDetailHandler.
//
// Player-facing filter (kept conservative to avoid spamming on junk rows): row's
// Name is >=3 chars and not all-digits AND at least one of Learnable==1, Casted
// By non-empty, Learned From non-empty. That matches the three ways a player
// will ever see the spell fire — they cast it, an NPC casts it at them, or an
// item procs it. Inverse direction (Spells/Items/Monsters that DON'T need
// messages) is implicitly ignored.
public sealed class SpellCoverageAuditor
{
    // Tag every entry the auditor emits uses on LogService.
    public const string LogSource = "GameData/Coverage";

    private readonly GameDataCache _cache;
    private readonly MessageStore _messages;
    private readonly LogService _log;
    // Coalesce-flag for QueueRun: a set switch fires
    // GameDataCache.ActiveSetChanged AND triggers MessageStore.Load which raises
    // one Clear-Reset + N Add CollectionChanged events; without debouncing the
    // audit would run N+2 times per swap and spam the LogPane. Posting once to
    // the dispatcher and clearing the flag in the dispatched handler collapses
    // the burst to a single run on the current UI tick.
    private bool _runQueued;

    // The most recent result. null until the first audit runs (no set active).
    public CoverageResult? Latest { get; private set; }

    // Fires after every audit run — populated Latest at fire time.
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

    // Defer Run to the next dispatcher tick + collapse any further queued
    // requests onto the same tick. Set switches always end up here; an MDB
    // import lands here via the ActiveSetChanged that SwitchActiveGameDataSet
    // raises after a successful import.
    private void QueueRun()
    {
        if (_runQueued) return;
        _runQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { _runQueued = false; Run(); },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // Explicit re-audit hook for the report window's "Refresh" button + future
    // MDB-import callers.
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

            int spellMagery    = ReadInt(row, "Magery");
            int spellMageryLvl = ReadInt(row, "MageryLVL");
            bool hasLearnedFrom = !string.IsNullOrEmpty(learnedFromRaw);
            gaps.Add(new UnanchoredSpell(
                Number:      number,
                Name:        name,
                CastedBy:    ResolveRefs(castedByRaw),
                LearnedFrom: ResolveRefs(learnedFromRaw),
                Classes:     ResolveClasses(ReadString(row, "Classes"),
                                            spellMagery, spellMageryLvl, hasLearnedFrom)));
        }

        gaps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Latest = new CoverageResult(set, considered, gaps.Count, gaps);

        _log.Log(LogSeverity.Info, LogSource,
            $"Active set '{set}': {gaps.Count} of {considered} player-facing spells have no Message anchor.  (double-click for details)");

        ResultAvailable?.Invoke(Latest);
        return Latest;
    }

    // Filter: row is a player-facing spell candidate iff its Name is non-junk
    // (>=3 chars, not all-digits) AND at least one of (Learnable==1, Casted By
    // non-empty, Learned From non-empty) holds. The three OR-paths cover
    // player-cast, NPC-cast-at-player, and item-proc respectively — everything
    // else (test rows, internal NPC-only effects with no monster reference,
    // deprecated entries) drops out and doesn't pollute the unanchored count.
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

    // Plain-text content check that treats NULL-byte-only strings ("\0") as
    // empty. The MdbImporter writes a literal NUL char for empty Jet text
    // columns instead of an empty string, so string.IsNullOrWhiteSpace isn't
    // enough on its own — NUL isn't whitespace.
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

    // Translate every "Monster #N" / "Item #N" / "Spell #N" reference in raw to
    // "Resolved Name {N}" using the active set's tables. References whose target
    // row doesn't exist in the set are left verbatim so the gap is still visible
    // to the user (rather than silently disappearing). TextBlock references are
    // passed through untouched per user direction — block IDs are only
    // meaningful when looked up directly.
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
                "NPC"     => "Monsters",  // NPCs share the Monsters table — flagged rows, not their own.
                "Item"    => "Items",
                "Spell"   => "Spells",
                _         => string.Empty,  // future / unknown — pass through verbatim
            };
            if (table.Length == 0) return m.Value;
            string? resolved = _cache.FindNameByNumber(table, num);
            return resolved is null ? m.Value : $"{resolved} {{{num}}}";
        });
    }

    // Matches "Monster #1234" / "NPC #251" / "Item #56" / "Spell #9" anywhere in
    // a string. Trailing-digit-greedy so multi-digit numbers don't get
    // truncated, and the word-boundary tail guards against gluing into a longer
    // identifier. NPCs share the Monsters table — the MDB labels NPC-flagged
    // rows differently in spell references but the row data lives in
    // Monsters.json.
    private static readonly System.Text.RegularExpressions.Regex RefPattern = new(
        @"\b(Monster|NPC|Item|Spell)\s*#\s*(\d+)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches a single class-restriction token in a Spells.Classes field — e.g.
    // "(*)" (all), "(3)" (Class #3), or multi-token strings like "(11),(10)".
    // Captured group 1 is either * or the class number.
    private static readonly System.Text.RegularExpressions.Regex ClassPattern = new(
        @"\((\*|\d+)\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Resolve the spell's class-eligibility to a human-readable list per the
    // three-layer MajorMUD rules the user provided:
    //   1. Explicit class restriction overrides everything. If raw contains
    //      specific (N) tokens (not (*)), only those classes can cast — magery +
    //      learned-from are ignored.
    //   2. Player-path requires (*) OR a learned-from item. Without explicit
    //      class tokens, a spell qualifies for the magery filter only when its
    //      Classes field is (*) (explicitly open to all) or its Learned From
    //      field has an item (the item teaches the spell, confirming player
    //      reach). Spells with empty Classes + empty Learned From are NPC-only
    //      even when the magery type/level happens to match a player class.
    //   3. Magery filter: when the player path is confirmed (rule 2), a class
    //      can cast when class.MageryType == spell.Magery AND
    //      class.MageryLVL >= spell.MageryLVL.
    // Returns the resolved list (space-slash separated) or "(none)" when no
    // player class meets the rules.
    private string ResolveClasses(string? raw, int spellMagery, int spellMageryLvl, bool hasLearnedFrom)
    {
        List<string> explicitClassNames = new();
        bool hasStarMarker = false;
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (System.Text.RegularExpressions.Match m in ClassPattern.Matches(raw))
            {
                string token = m.Groups[1].Value;
                if (token == "*") { hasStarMarker = true; continue; }
                if (!int.TryParse(token, out int n)) continue;
                string? name = _cache.FindNameByNumber("Classes", n);
                explicitClassNames.Add(name ?? $"Class #{n}");
            }
        }

        // Rule 1: explicit class restriction wins; magery is ignored.
        if (explicitClassNames.Count > 0)
            return string.Join(" / ", explicitClassNames);

        // Rule 2: no explicit restriction → require either the (*)
        // marker OR a learned-from item to confirm a player path.
        // Without either, the spell is NPC-only.
        if (!hasStarMarker && !hasLearnedFrom) return "(none)";

        // Rule 3: magery filter.
        if (spellMagery == 0) return "(none)";

        List<string> mageryClassNames = new();
        System.Text.Json.JsonDocument? doc = _cache.GetRawTable("Classes");
        if (doc is not null)
        {
            foreach (System.Text.Json.JsonElement cls in doc.RootElement.EnumerateArray())
            {
                if (ReadInt(cls, "MageryType") != spellMagery) continue;
                if (ReadInt(cls, "MageryLVL")  <  spellMageryLvl) continue;
                string? name = ReadString(cls, "Name");
                if (!string.IsNullOrEmpty(name)) mageryClassNames.Add(name);
            }
        }
        return mageryClassNames.Count == 0 ? "(none)" : string.Join(" / ", mageryClassNames);
    }

    // Reads an int property or returns 0 when missing / wrong-typed.
    private static int ReadInt(System.Text.Json.JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out System.Text.Json.JsonElement el)) return 0;
        if (el.ValueKind != System.Text.Json.JsonValueKind.Number) return 0;
        return el.TryGetInt32(out int n) ? n : 0;
    }

    // True when any Monster/Item ref in raw already has a Message anchor in
    // anchoredMonsters / anchoredItems. Lets the audit treat a spell as covered
    // when the user has authored a message for one of its casters even without a
    // direct (Spells, N) link.
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

    // Parse every "Monster #N" / "Item #N" / "Spell #N" reference in raw into
    // structured (Table, Number) tuples. Used by the coverage report's drilldown
    // to seed Links on a fresh Message record. Unknown / future reference types
    // are skipped.
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
                "NPC"     => "Monsters",
                "Item"    => "Items",
                "Spell"   => "Spells",
                _         => string.Empty,
            };
            if (table.Length == 0) continue;
            yield return (table, num);
        }
    }
}

// Snapshot returned from one SpellCoverageAuditor run. SetName is the active
// game-data set at audit time, ConsideredCount how many spell rows passed the
// player-facing filter, UnanchoredCount how many of those had no Message link
// pointing at them, Unanchored the full list (sorted by Name) of unanchored
// spells.
public sealed record CoverageResult(
    string                       SetName,
    int                          ConsideredCount,
    int                          UnanchoredCount,
    IReadOnlyList<UnanchoredSpell> Unanchored);

// One row in CoverageResult.Unanchored.
public sealed record UnanchoredSpell(
    int     Number,
    string  Name,
    string? CastedBy,
    string? LearnedFrom,
    string? Classes);
