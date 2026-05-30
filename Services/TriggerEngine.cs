using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the active character's triggers + the per-session
/// named variable store the triggers share with aliases (Phase 5 PR 5.11).
/// PR 5.10 ships the data spine — Triggers / Variables collections,
/// per-profile load + clear; <see cref="MessageRouter"/> wire-up and the
/// runtime action dispatch land alongside the editor in a follow-up
/// PR after the read-only listing tab for every game-data table is
/// in place.
/// </summary>
/// <remarks>
/// Variables are app-session-scoped (in-memory, wiped on app close —
/// same lifetime as <see cref="Game.ChatHistoryStore"/>) rather than
/// per-profile so a Trigger that captures a value from one character
/// can be read by an Alias on another character within the same
/// session.
/// </remarks>
public sealed class TriggerEngine
{
    /// <summary>The loaded character's triggers — empty when no profile is active.</summary>
    public ObservableCollection<Trigger> Triggers { get; } = new();

    /// <summary>App-session-scoped named-variable store shared with the Phase 5 PR 5.11 alias engine.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TriggerEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        if (profile.Current is { } current) LoadFrom(current);
    }

    private void LoadFrom(CharacterProfile profile)
    {
        Triggers.Clear();
        if (profile.Triggers is null) return;
        foreach (Trigger t in profile.Triggers) Triggers.Add(t);
    }

    private void Clear() => Triggers.Clear();
}
