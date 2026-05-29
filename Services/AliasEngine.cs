using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the active character's
/// <see cref="Models.GameData.Alias"/> entries. Loaded from
/// <see cref="CharacterProfile.Aliases"/> on profile load. PR 5.11
/// ships the data spine + the listing surface; the runtime match
/// dispatch (intercepting input keystrokes on TerminalControl /
/// ConversationWindow's input field) lands alongside the editor in a
/// follow-up after every Phase 5 table's listing is in place.
/// </summary>
/// <remarks>
/// Aliases read from the same session-scoped named-variable store
/// <see cref="TriggerEngine"/> exposes via
/// <see cref="TriggerEngine.Variables"/>, so a trigger can capture a
/// value (e.g. <c>$playerName</c>) and an alias's expansion can
/// reference it on the next send.
/// </remarks>
public sealed class AliasEngine
{
    /// <summary>The loaded character's aliases — empty when no profile is active.</summary>
    public ObservableCollection<Alias> Aliases { get; } = new();

    public AliasEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        if (profile.Current is { } current) LoadFrom(current);
    }

    private void LoadFrom(CharacterProfile profile)
    {
        Aliases.Clear();
        if (profile.Aliases is null) return;
        foreach (Alias a in profile.Aliases) Aliases.Add(a);
    }

    private void Clear() => Aliases.Clear();
}
