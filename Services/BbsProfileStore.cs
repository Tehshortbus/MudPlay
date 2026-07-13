using System.IO;
using System.Linq;
using FujinTerm.Models.Settings;

namespace FujinTerm.Services;

// Owns Data/BBS/{name}/ — one folder per BBS, containing the primary bbs.json
// (connection info + BBS-tier settings deltas) plus any per-set override
// side-files (monster_overrides.{set}.json, message_overrides.{set}.json, …)
// and future per-BBS helper files (favorites, character roster, …).
public sealed class BbsProfileStore
{
    // Load a single BBS profile by name. Returns null if no bbs.json exists for
    // that name. The folder may exist with only side-files (e.g. mid-migration)
    // — that still counts as "no BBS profile".
    public BbsProfile? Get(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return null;
        return JsonStore.Load<BbsProfile>(AppPaths.BbsProfileFile(bbsName));
    }

    // Persist a BBS profile to Data/BBS/{Name}/bbs.json, creating the folder on
    // first save.
    public void Save(BbsProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("BbsProfile.Name is required for save.", nameof(profile));

        Directory.CreateDirectory(AppPaths.BbsFolder(profile.Name));
        JsonStore.Save(AppPaths.BbsProfileFile(profile.Name), profile);
    }

    // Delete a BBS — removes the entire Data/BBS/{name}/ folder (primary file +
    // all side-files). No-op if the folder doesn't exist.
    public void Delete(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return;
        string folder = AppPaths.BbsFolder(bbsName);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // Rename a BBS in place: move the whole Data/BBS/{old}/ folder — bbs.json,
    // every override side-file, AND every nested character profile — to the new
    // name, then rewrite the primary file's Name field. A folder move keeps the
    // nested profiles intact; a Delete(old)+Save(new) would recursively destroy
    // them. No-op if the source folder is missing; throws if the destination
    // already exists (the caller resolves clashes first). Case-only renames
    // never reach here — the Settings Apply path gates on an OrdinalIgnoreCase
    // inequality, and Directory.Move can't do a case-only move on a
    // case-insensitive filesystem.
    public void Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
        string oldFolder = AppPaths.BbsFolder(oldName);
        string newFolder = AppPaths.BbsFolder(newName);
        if (!Directory.Exists(oldFolder)) return;
        if (Directory.Exists(newFolder))
            throw new IOException($"A BBS folder already exists at '{newFolder}'.");

        Directory.Move(oldFolder, newFolder);

        // The moved bbs.json still carries the old Name — bring it in line.
        if (Get(newName) is { } profile)
        {
            profile.Name = newName;
            JsonStore.Save(AppPaths.BbsProfileFile(newName), profile);
        }
    }

    // Enumerate every BBS that has a primary bbs.json on disk. The folder name
    // (= BBS name) is yielded, alphabetical order optional at the caller.
    // Folders missing a bbs.json are skipped — they aren't fully initialised yet.
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
