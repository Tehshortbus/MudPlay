using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Two-layer store of player records — the BBS-tier observation list
/// (one row per player ever seen on the active BBS) merged with the
/// loaded character's customization dictionary (per-player remote-
/// command permissions, auto-party toggles, etc.). Both layers persist
/// to disk: observations to <c>Data/BBS/{name}/players.json</c>;
/// customizations to the loaded profile's
/// <see cref="CharacterProfile.PlayerCustomizations"/> dictionary,
/// pruned at save time so only non-default entries hit disk.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Players"/> is the merged view bound by the Game Data
/// Browser → Players tab. Observation writes
/// (<see cref="RecordObservation"/> from <see cref="Game.WhoListParser"/>
/// + the future look-on-player parser) update the BBS layer and
/// schedule a disk save; customization writes (<see cref="EditCustomization"/>
/// from the player edit dialog) update the Character layer and
/// schedule a profile save. Either path rebuilds <see cref="Players"/>.
/// </para>
/// <para>
/// On BBS swap the observation layer reloads from disk; on profile
/// swap the customization layer reloads. <see cref="PurgeStale"/>
/// drops observations only — customizations stay attached to the
/// profile, harmless when no record exists for them at the moment
/// (a later observation re-binds them automatically).
/// </para>
/// </remarks>
public sealed class PlayerDatabase
{
    private readonly ProfileService? _profile;
    private readonly Func<BbsProfile?>? _activeBbsProvider;

    // ----- Backing layers ------------------------------------------------

    /// <summary>BBS-tier observations, keyed by display name (case-insensitive).</summary>
    private readonly Dictionary<string, PlayerObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Char-tier customizations, keyed by display name (case-insensitive). Empty when no profile loaded.</summary>
    private readonly Dictionary<string, PlayerCustomization> _customizations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Active BBS the observation layer currently mirrors. <c>null</c> when no BBS is pinned.</summary>
    private string? _activeBbsName;

    /// <summary>Backing store for the merged view — observable so views can react to updates.</summary>
    public ObservableCollection<PlayerRecord> Players { get; } = new();

    // ----- Construction --------------------------------------------------

    /// <summary>Parameterless ctor for tests and in-memory-only scenarios — no disk persistence.</summary>
    public PlayerDatabase() { }

    /// <summary>
    /// Production ctor. Subscribes to <see cref="ProfileService.ProfileLoaded"/>,
    /// <see cref="ProfileService.ProfileClosed"/>,
    /// <see cref="ProfileService.ProfileSaving"/>, and
    /// <see cref="ProfileService.BbsPinApplied"/> so both layers stay
    /// in sync with whichever character + BBS the user is on.
    /// </summary>
    public PlayerDatabase(ProfileService profile, Func<BbsProfile?> activeBbsProvider)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activeBbsProvider);
        _profile = profile;
        _activeBbsProvider = activeBbsProvider;

        profile.ProfileLoaded  += _ => OnSwap();
        profile.ProfileClosed  +=      OnSwap;
        profile.BbsPinApplied  += _ => OnSwap();
        profile.ProfileSaving  += SnapshotCustomizationsForSave;
    }

    // ----- Observation writes (BBS tier) --------------------------------

    /// <summary>
    /// Apply one observed row (typically from <c>who</c> output). Wire
    /// names are split via <see cref="PlayerObservation.SplitName"/> on
    /// the first whitespace. Existing records merge — nulls don't
    /// overwrite — so a sparse observation only updates the fields it
    /// has. Saves the BBS observation file after the merge.
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
        (string given, string family) = PlayerObservation.SplitName(name);
        string display = string.IsNullOrEmpty(family) ? given : $"{given} {family}";
        if (string.IsNullOrEmpty(display)) return;

        if (_observations.TryGetValue(display, out PlayerObservation? existing))
        {
            _observations[display] = existing with
            {
                GivenName   = given,
                FamilyName  = family,
                Class       = @class    ?? existing.Class,
                Race        = race      ?? existing.Race,
                Alignment   = alignment ?? existing.Alignment,
                Title       = title     ?? existing.Title,
                Gang        = gang      ?? existing.Gang,
                Role        = role      ?? existing.Role,
                LastSeenUtc = nowUtc,
            };
        }
        else
        {
            _observations[display] = new PlayerObservation(
                GivenName:    given,
                FamilyName:   family,
                Class:        @class,
                Race:         race,
                Alignment:    alignment,
                Title:        title,
                Gang:         gang,
                Role:         role,
                FirstSeenUtc: nowUtc,
                LastSeenUtc:  nowUtc);
        }

        Rebuild();
        SaveObservations();
    }

    /// <summary>
    /// Replace the customization slice for one player. Triggered by the
    /// player edit dialog Save path; persists via
    /// <see cref="ProfileService.Save"/>. Defaults aren't stored: a
    /// pristine "everything off / no notes" customization removes any
    /// existing entry so the profile JSON doesn't bloat with one row
    /// per observed stranger.
    /// </summary>
    public bool EditCustomization(string displayName, PlayerCustomization customization)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        if (customization.IsDefault)
            _customizations.Remove(displayName);
        else
            _customizations[displayName] = customization;

        Rebuild();
        _profile?.Save();
        return true;
    }

    /// <summary>
    /// Drop every observation last seen more than <paramref name="days"/>
    /// days ago, EXCEPT those whose customization is flagged
    /// <see cref="PlayerCustomization.DontAutoDelete"/>. Returns the
    /// number removed. Customizations for purged players stay attached
    /// to the profile — a later observation re-binds them automatically.
    /// </summary>
    public int PurgeStale(int days, DateTime nowUtc)
    {
        if (days <= 0) return 0;
        DateTime cutoff = nowUtc.AddDays(-days);
        int removed = 0;
        foreach (string key in _observations.Keys.ToArray())
        {
            PlayerObservation o = _observations[key];
            if (o.LastSeenUtc >= cutoff) continue;
            if (_customizations.TryGetValue(key, out PlayerCustomization c) && c.DontAutoDelete) continue;
            _observations.Remove(key);
            removed++;
        }
        if (removed > 0)
        {
            Rebuild();
            SaveObservations();
        }
        return removed;
    }

    /// <summary>
    /// Replace the BBS-tier observation layer wholesale. Used by load-
    /// from-disk paths and by tests that want a deterministic baseline.
    /// </summary>
    public void ReplaceObservations(IEnumerable<PlayerObservation> rows)
    {
        _observations.Clear();
        foreach (PlayerObservation o in rows)
        {
            string key = o.DisplayName;
            if (!string.IsNullOrEmpty(key)) _observations[key] = o;
        }
        Rebuild();
    }

    // ----- Swap + persistence -------------------------------------------

    private void OnSwap()
    {
        BbsProfile? bbs = _activeBbsProvider?.Invoke();
        _activeBbsName = bbs?.Name;

        // BBS layer: load from disk if a BBS resolves, else clear.
        _observations.Clear();
        if (!string.IsNullOrEmpty(_activeBbsName))
        {
            string path = AppPaths.BbsPlayersFile(_activeBbsName);
            List<PlayerObservation>? loaded = JsonStore.Load<List<PlayerObservation>>(path);
            if (loaded is not null)
            {
                foreach (PlayerObservation o in loaded)
                {
                    string key = o.DisplayName;
                    if (!string.IsNullOrEmpty(key)) _observations[key] = o;
                }
            }
        }

        // Char layer: pull off the loaded profile (null when no profile).
        _customizations.Clear();
        CharacterProfile? profile = _profile?.Current;
        if (profile?.PlayerCustomizations is { } pcs)
        {
            foreach ((string key, PlayerCustomization c) in pcs)
            {
                if (!string.IsNullOrEmpty(key)) _customizations[key] = c;
            }
        }

        Rebuild();
    }

    /// <summary>
    /// Pushed onto <see cref="ProfileService.ProfileSaving"/> so the
    /// in-memory customization dict gets serialised onto the profile
    /// JUST before the file is written. Pristine entries are pruned —
    /// only customizations that hold a non-default value land on disk.
    /// </summary>
    private void SnapshotCustomizationsForSave(CharacterProfile profile)
    {
        if (_customizations.Count == 0)
        {
            profile.PlayerCustomizations = null;
            return;
        }
        Dictionary<string, PlayerCustomization> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, PlayerCustomization c) in _customizations)
        {
            if (c.IsDefault) continue;
            snapshot[key] = c;
        }
        profile.PlayerCustomizations = snapshot.Count == 0 ? null : snapshot;
    }

    private void SaveObservations()
    {
        if (string.IsNullOrEmpty(_activeBbsName)) return; // in-memory mode
        string path = AppPaths.BbsPlayersFile(_activeBbsName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, _observations.Values
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    // ----- Merged view rebuild ------------------------------------------

    private void Rebuild()
    {
        Players.Clear();
        foreach (PlayerObservation o in _observations.Values
                     .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _customizations.TryGetValue(o.DisplayName, out PlayerCustomization c);
            Players.Add(PlayerRecord.Merge(o, c));
        }
    }
}
