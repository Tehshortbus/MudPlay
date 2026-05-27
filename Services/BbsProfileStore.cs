using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

/// <summary>
/// Owns <c>Data/BBS/*.json</c> — the BBS tier of the settings hierarchy. Each
/// file is one BBS profile (host, port, plus deltas pinned to that BBS).
/// </summary>
public sealed class BbsProfileStore
{
    /// <summary>
    /// Load a single BBS profile by filename (without <c>.json</c> extension).
    /// Returns <c>null</c> if the file does not exist.
    /// </summary>
    public BbsProfile? Get(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return null;
        return JsonStore.Load<BbsProfile>(AppPaths.BbsProfileFile(bbsName));
    }

    /// <summary>
    /// Persist a BBS profile. The on-disk filename uses <see cref="BbsProfile.Name"/>.
    /// </summary>
    public void Save(BbsProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("BbsProfile.Name is required for save.", nameof(profile));

        JsonStore.Save(AppPaths.BbsProfileFile(profile.Name), profile);
    }

    /// <summary>
    /// Delete a BBS profile file from disk. No-op if the file does not exist.
    /// </summary>
    public void Delete(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return;
        string path = AppPaths.BbsProfileFile(bbsName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Enumerate every BBS profile filename currently saved (without the
    /// <c>.json</c> extension). Order is not guaranteed.
    /// </summary>
    public IEnumerable<string> ListNames()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string file in Directory.EnumerateFiles(AppPaths.BbsDir, "*.json"))
        {
            yield return Path.GetFileNameWithoutExtension(file);
        }
    }
}
