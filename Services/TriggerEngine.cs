using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the active character's triggers + the app-session
/// named variable store. PR 5.10 ships the data spine + CRUD wiring;
/// <see cref="Terminal.LineExtractor"/> / <see cref="Game.ChatRouter"/> /
/// <see cref="LogService"/> subscription + runtime dispatch lands in
/// the next commit.
/// </summary>
/// <remarks>
/// Variables are app-session-scoped (in-memory, wiped on app close —
/// same lifetime as <see cref="Game.ChatHistoryStore"/>) rather than
/// per-profile so a trigger that captures a value on one character can
/// be read by a trigger / alias on another within the same session.
/// </remarks>
public sealed class TriggerEngine
{
    private readonly ProfileService? _profile;

    /// <summary>The loaded character's triggers — empty when no profile is active.</summary>
    public ObservableCollection<Trigger> Triggers { get; } = new();

    /// <summary>App-session-scoped named-variable store shared with the Phase 5 PR 5.11 alias engine.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TriggerEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        if (profile.Current is { } current) LoadFrom(current);
    }

    /// <summary>Parameterless ctor for tests / in-memory scenarios — no profile persistence.</summary>
    public TriggerEngine() { }

    /// <summary>Insert a new trigger and persist.</summary>
    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Triggers.Add(trigger);
        _profile?.Save();
    }

    /// <summary>
    /// Replace an existing trigger identified by reference. Persists.
    /// Returns <c>false</c> when the original isn't in the list.
    /// </summary>
    public bool Replace(Trigger original, Trigger updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Triggers.IndexOf(original);
        if (index < 0) return false;
        Triggers[index] = updated;
        _profile?.Save();
        return true;
    }

    /// <summary>Remove a trigger by reference. Persists. <c>false</c> if not found.</summary>
    public bool Remove(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        bool removed = Triggers.Remove(trigger);
        if (removed) _profile?.Save();
        return removed;
    }

    // ----- Profile sync ---------------------------------------------------

    private void LoadFrom(CharacterProfile profile)
    {
        Triggers.Clear();
        if (profile.Triggers is null) return;
        foreach (Trigger t in profile.Triggers) Triggers.Add(t);
    }

    private void Clear() => Triggers.Clear();

    /// <summary>Snapshot the live list onto the profile DTO right before save.</summary>
    private void SnapshotForSave(CharacterProfile profile)
    {
        profile.Triggers = Triggers.Count == 0 ? null : Triggers.ToList();
    }
}
