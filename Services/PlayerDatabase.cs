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
/// engine-known fields — given / family name (re-split from the wire
/// name), Class / Race / Alignment / Title / LastSeen — and leave
/// user-authored fields (Notes, RemoteControls, InviteToPartyIfSeen,
/// JoinPartyIfInvited, DontAutoDelete) untouched. New observations get
/// FirstSeenUtc = LastSeenUtc = now.
/// </para>
/// <para>
/// <see cref="PurgeStale"/> drops every record last seen more than
/// <c>days</c> ago, except those flagged
/// <see cref="PlayerRecord.DontAutoDelete"/>. The cleanup window comes
/// from <c>GlobalSettings.PlayerCleanupDays</c> (default 90).
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
    /// Apply one observed row (typically from a <c>who</c> line). Wire
    /// names are split via <see cref="PlayerRecord.SplitName"/> on the
    /// first whitespace so the table can display Given / Family columns
    /// separately. Merges with any existing record matching by
    /// <see cref="PlayerRecord.DisplayName"/> (case-insensitive); fresh
    /// observations create a new record.
    /// </summary>
    public void RecordObservation(
        string name,
        string? @class,
        string? race,
        string? alignment,
        string? title,
        string? gang,
        string? role,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        (string given, string family) = PlayerRecord.SplitName(name);

        int index = FindIndexByName(name);
        if (index < 0)
        {
            Players.Add(new PlayerRecord(
                GivenName:    given,
                FamilyName:   family,
                Class:        @class,
                Race:         race,
                Alignment:    alignment,
                Title:        title,
                Gang:         gang,
                Role:         role,
                FirstSeenUtc: nowUtc,
                LastSeenUtc:  nowUtc));
            return;
        }

        PlayerRecord existing = Players[index];
        Players[index] = existing with
        {
            // Re-split lets the player rename across sessions without
            // stranding the old given/family on the record.
            GivenName   = given,
            FamilyName  = family,
            Class       = @class ?? existing.Class,
            Race        = race ?? existing.Race,
            Alignment   = alignment ?? existing.Alignment,
            Title       = title ?? existing.Title,
            Gang        = gang ?? existing.Gang,
            Role        = role ?? existing.Role,
            LastSeenUtc = nowUtc,
            // Notes + RemoteControls + auto-party flags + DontAutoDelete
            // intentionally preserved — observation never overwrites
            // user-authored fields.
        };
    }

    /// <summary>
    /// Replace a record's freeform note. Returns <c>false</c> when no
    /// record matches <paramref name="displayName"/>.
    /// </summary>
    public bool EditNotes(string displayName, string? notes)
    {
        int index = FindIndexByName(displayName);
        if (index < 0) return false;
        Players[index] = Players[index] with { Notes = notes };
        return true;
    }

    /// <summary>
    /// Replace a record's user-authored fields in one shot — the player
    /// edit dialog routes through here on Save. Matches by the original
    /// display name so a rename in the dialog doesn't strand the
    /// existing record. Returns <c>false</c> when no record matches.
    /// </summary>
    public bool EditRecord(string originalDisplayName, PlayerRecord updated)
    {
        int index = FindIndexByName(originalDisplayName);
        if (index < 0) return false;
        Players[index] = updated;
        return true;
    }

    /// <summary>
    /// Drop every record last seen more than <paramref name="days"/>
    /// days ago, EXCEPT records flagged
    /// <see cref="PlayerRecord.DontAutoDelete"/>. Returns the number
    /// removed.
    /// </summary>
    public int PurgeStale(int days, DateTime nowUtc)
    {
        if (days <= 0) return 0;
        DateTime cutoff = nowUtc.AddDays(-days);
        int removed = 0;
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            PlayerRecord r = Players[i];
            if (r.DontAutoDelete) continue;
            if (r.LastSeenUtc < cutoff)
            {
                Players.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private int FindIndexByName(string displayName)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            if (string.Equals(Players[i].DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
