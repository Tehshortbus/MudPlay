using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the loaded character's
/// <see cref="Models.GameData.Macro"/> entries. PR 5.22 ships the
/// listing surface; the Phase 10 MacroManager engine that intercepts
/// keystrokes and dispatches commands subscribes to the same store
/// at runtime.
/// </summary>
public sealed class MacroStore
{
    /// <summary>The loaded character's macros — empty when no profile is active.</summary>
    public ObservableCollection<Macro> Macros { get; } = new();

    public MacroStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        if (profile.Current is { } current) LoadFrom(current);
    }

    private void LoadFrom(CharacterProfile profile)
    {
        Macros.Clear();
        if (profile.Macros is null) return;
        foreach (Macro m in profile.Macros) Macros.Add(m);
    }

    private void Clear() => Macros.Clear();
}
