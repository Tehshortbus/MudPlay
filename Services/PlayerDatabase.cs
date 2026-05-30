using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// In-memory store of observed-or-edited <see cref="PlayerRecord"/>
/// entries. The actual <c>who</c>-output parser that drives
/// observation writes lives with Phase 6 PartyManager; PR 5.20 ships
/// the storage spine + the merge / cleanup rules.
/// </summary>
/// <remarks>
/// <para>
/// Observation writes (<see cref="RecordObservation"/>) refresh the
/// engine-known fields — Class / Race / Alignment / Title / LastSeen
/// — and leave user-authored fields (Notes, Permissions) untouched.
/// New observations get FirstSeenUtc = LastSeenUtc = now.
/// </para>
/// <para>
/// <see cref="PurgeStale"/> drops every record last seen more than
/// <c>days</c> ago (Settings → Other → "Inactive player cleanup
/// window" governs the default at the call site). Per-character
/// promotion / global promotion of a record (via the standard 4-tier
/// resolver) is a Phase 5 follow-up — PR 5.20 stores everything at the
/// BBS tier.
/// </para>
/// </remarks>
public sealed class PlayerDatabase
{
    /// <summary>Backing store — observable so views can react to updates.</summary>
    public ObservableCollection<PlayerRecord> Players { get; } = new();

    /// <summary>Replace the store wholesale (used by load-from-disk paths).</summary>
    public void Replace(IEnumerable<PlayerRecord> rows)
    {
        Players.Clear();
        foreach (PlayerRecord r in rows) Players.Add(r);
    }

    /// <summary>
    /// Apply one observed row (typically from a <c>who</c> line).
    /// Merges with any existing record matching by <see cref="PlayerRecord.Name"/>
    /// (case-insensitive); fresh observations create a new record.
    /// </summary>
    public void RecordObservation(
        string name,
        string? @class,
        string? race,
        string? alignment,
        string? title,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(name);

        int index = FindIndexByName(name);
        if (index < 0)
        {
            Players.Add(new PlayerRecord(
                Name: name,
                Class: @class,
                Race: race,
                Alignment: alignment,
                Title: title,
                FirstSeenUtc: nowUtc,
                LastSeenUtc: nowUtc));
            return;
        }

        PlayerRecord existing = Players[index];
        Players[index] = existing with
        {
            Class = @class ?? existing.Class,
            Race = race ?? existing.Race,
            Alignment = alignment ?? existing.Alignment,
            Title = title ?? existing.Title,
            LastSeenUtc = nowUtc,
            // Notes + Permissions intentionally preserved — observation
            // never overwrites user-authored fields.
        };
    }

    /// <summary>
    /// Replace a record's user-authored fields (Notes / Permissions).
    /// Returns <c>false</c> when no record matches <paramref name="name"/>.
    /// </summary>
    public bool EditNotes(string name, string? notes)
    {
        int index = FindIndexByName(name);
        if (index < 0) return false;
        Players[index] = Players[index] with { Notes = notes };
        return true;
    }

    /// <inheritdoc cref="EditNotes(string, string?)"/>
    public bool EditPermissions(string name, PlayerPermissions permissions)
    {
        int index = FindIndexByName(name);
        if (index < 0) return false;
        Players[index] = Players[index] with { Permissions = permissions };
        return true;
    }

    /// <summary>
    /// Drop every record last seen more than <paramref name="days"/>
    /// days ago. Returns the number removed.
    /// </summary>
    public int PurgeStale(int days, DateTime nowUtc)
    {
        if (days <= 0) return 0;
        DateTime cutoff = nowUtc.AddDays(-days);
        int removed = 0;
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (Players[i].LastSeenUtc < cutoff)
            {
                Players.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private int FindIndexByName(string name)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            if (string.Equals(Players[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
