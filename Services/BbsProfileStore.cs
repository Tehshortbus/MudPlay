using System.IO;
using System.Linq;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Owns <c>Data/BBS/{name}/</c> — one folder per BBS, containing the
/// primary <c>bbs.json</c> (connection info + BBS-tier settings deltas)
/// plus any per-set override side-files (<c>monster_overrides.{set}.json</c>,
/// <c>message_overrides.{set}.json</c>, …) and future per-BBS helper
/// files (favorites, character roster, …).
/// </summary>
public sealed class BbsProfileStore
{
    /// <summary>
    /// Load a single BBS profile by name. Returns <c>null</c> if no
    /// <c>bbs.json</c> exists for that name. The folder may exist with
    /// only side-files (e.g. mid-migration) — that still counts as
    /// "no BBS profile".
    /// </summary>
    public BbsProfile? Get(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return null;
        return JsonStore.Load<BbsProfile>(AppPaths.BbsProfileFile(bbsName));
    }

    /// <summary>
    /// Persist a BBS profile to <c>Data/BBS/{Name}/bbs.json</c>,
    /// creating the folder on first save.
    /// </summary>
    public void Save(BbsProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("BbsProfile.Name is required for save.", nameof(profile));

        Directory.CreateDirectory(AppPaths.BbsFolder(profile.Name));
        JsonStore.Save(AppPaths.BbsProfileFile(profile.Name), profile);
    }

    /// <summary>
    /// Delete a BBS — removes the entire <c>Data/BBS/{name}/</c>
    /// folder (primary file + all side-files). No-op if the folder
    /// doesn't exist.
    /// </summary>
    public void Delete(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return;
        string folder = AppPaths.BbsFolder(bbsName);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// Enumerate every BBS that has a primary <c>bbs.json</c> on disk.
    /// The folder name (= BBS name) is yielded, alphabetical order
    /// optional at the caller. Folders missing a <c>bbs.json</c>
    /// are skipped — they aren't fully initialised yet.
    /// </summary>
    public IEnumerable<string> ListNames()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string folder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(name)) continue;
            if (!File.Exists(AppPaths.BbsProfileFile(name))) continue;
            yield return name;
        }
    }
}
