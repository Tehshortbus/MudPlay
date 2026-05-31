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

        cache.ActiveSetChanged           += _ => Run();
        messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Run();

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

        // Build the set of (Spells, #) numbers any Message links at.
        HashSet<int> anchored = new();
        foreach (MessageRecord m in _messages.Messages)
        {
            if (m.Links is null) continue;
            foreach (GameDataLink link in m.Links)
            {
                if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase))
                    anchored.Add(link.Number);
            }
        }

        int considered = 0;
        List<UnanchoredSpell> gaps = new();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!IsPlayerFacing(row, out string name, out int number)) continue;
            considered++;
            if (anchored.Contains(number)) continue;
            gaps.Add(new UnanchoredSpell(
                Number:     number,
                Name:       name,
                CastedBy:   ReadString(row, "Casted By"),
                LearnedFrom: ReadString(row, "Learned From"),
                Classes:    ReadString(row, "Classes")));
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
        bool castedBy   = !string.IsNullOrWhiteSpace(ReadString(row, "Casted By"));
        bool learnedFrom = !string.IsNullOrWhiteSpace(ReadString(row, "Learned From"));
        return learnable || castedBy || learnedFrom;
    }

    private static string? ReadString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out JsonElement el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
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
