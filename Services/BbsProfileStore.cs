using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Owns BBS/{name}/ — one folder per BBS, holding the primary bbs.json
// (connection info + the realms it hosts), the character profiles under
// profiles/, and one folder per realm under Realms/ with everything collected
// while playing it (AppPaths.RealmFolder).
public sealed class BbsProfileStore
{
    // The Global fallback game-data set — lets the realm migration find the boss
    // timers of a BBS that never named its own set.
    private readonly Func<string?>? _defaultGameDataSet;
    private readonly LogService? _log;

    public BbsProfileStore(Func<string?>? defaultGameDataSet = null, LogService? log = null)
    {
        _defaultGameDataSet = defaultGameDataSet;
        _log = log;
    }

    // A BBS's name is its folder's, and Delete and Rename take that folder whole
    // with every character under it. A name that can't be used for one
    // (PathGuard.Usable: a plain name, or a folder already there under an older,
    // odder name) is refused before anything is touched, with the refusal logged.
    private static bool Usable(string operation, string? bbsName)
    {
        if (PathGuard.Usable(AppPaths.BbsDir, bbsName)) return true;
        PathGuard.Refuse(operation, "BBS name", bbsName);
        return false;
    }

    // Load a single BBS profile by name. Returns null if no bbs.json exists for
    // that name. The folder may exist with only side-files (e.g. mid-migration)
    // — that still counts as "no BBS profile".
    public BbsProfile? Get(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName) || !Usable("Reading a BBS", bbsName)) return null;
        BbsProfile? profile = JsonStore.Load<BbsProfile>(AppPaths.BbsProfileFile(bbsName));

        // The FOLDER is the source of truth for a BBS's identity (ProfileService:
        // "folder location is the source of truth"), and every caller passes the
        // folder name here. Reconcile the loaded Name to it: a bbs.json whose Name
        // drifted from its folder — a folder duplicated to make a same-host sibling
        // realm, its Name left pointing at the original — otherwise mis-keys
        // everything read off BbsProfile.Name. Both the BBS-scoped resource folders
        // (blacklist, leaderboard) AND the per-BBS logon-step lookup key on it, so a
        // "Paradigm PVE" profile whose json still said "Paradigm PVP" ran the PVP
        // realm-select step and logged into the wrong realm (report
        // paradigm-20260825-102259). Rename already keeps them in sync; a hand-copied
        // folder doesn't, so heal it here on load.
        if (profile is not null && !string.Equals(profile.Name, bbsName, StringComparison.Ordinal))
            profile.Name = bbsName;

        // A BBS always has at least one realm. One saved before realms existed gets
        // its first realm (and its collected data) here, once, and is re-saved.
        if (profile is not null && profile.Realms.Count == 0)
        {
            string path = AppPaths.BbsProfileFile(bbsName);
            string? raw = File.Exists(path) ? File.ReadAllText(path) : null;
            RealmProfile realm = RealmMigration.CreateFirstRealm(bbsName, raw, _defaultGameDataSet?.Invoke());
            profile.Realms.Add(realm);
            JsonStore.Save(path, profile);
            _log?.Info("BBS",
                $"'{bbsName}' now has realms: its realm settings and collected data moved into realm '{realm.Name}'.");
        }

        if (profile is not null) Remember(profile, JsonSerializer.SerializeToNode(profile, JsonStore.Options));
        return profile;
    }

    // Persist a BBS profile to BBS/{Name}/bbs.json, creating the folder on
    // first save.
    public void Save(BbsProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("BbsProfile.Name is required for save.", nameof(profile));
        if (!Usable("Saving a BBS", profile.Name))
            throw new ArgumentException($"{PathGuard.Show(profile.Name)} can't be used as a BBS name.", nameof(profile));
        if (profile.Realms.Count == 0) profile.Realms.Add(new RealmProfile { Name = profile.Name });

        Directory.CreateDirectory(AppPaths.BbsFolder(profile.Name));
        string path = AppPaths.BbsProfileFile(profile.Name);
        JsonNode? mine = JsonSerializer.SerializeToNode(profile, JsonStore.Options);
        _asRead.TryGetValue(profile, out JsonNode? baseline);
        if (baseline is not null && ReadNode(path) is { } theirs && !JsonNode.DeepEquals(theirs, baseline))
        {
            // Another client (or another holder in this one) saved the BBS since
            // this copy was read. With nothing changed here, theirs stands;
            // otherwise write theirs with this copy's changes on top.
            if (JsonNode.DeepEquals(mine, baseline)) return;
            JsonNode? merged = JsonThreeWayMerge.Merge(baseline, mine, theirs);
            if (merged?.Deserialize<BbsProfile>(JsonStore.Options) is { } combined)
            {
                JsonStore.Save(path, combined);
                Remember(profile, mine);
                return;
            }
        }
        JsonStore.Save(path, profile);
        Remember(profile, mine);
    }

    // What each profile handed out or saved held at that moment. A caller keeps a
    // BbsProfile for as long as its window is open (the Settings BBS tab) or the
    // character is loaded (the resolver), then saves the whole of it; this is what
    // tells its own changes from what it never touched.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BbsProfile, JsonNode> _asRead = new();

    private void Remember(BbsProfile profile, JsonNode? state)
    {
        _asRead.Remove(profile);
        if (state is not null) _asRead.Add(profile, state);
    }

    private static JsonNode? ReadNode(string path)
    {
        try
        {
            return JsonStore.Load<BbsProfile>(path) is { } onFile
                ? JsonSerializer.SerializeToNode(onFile, JsonStore.Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    // True when a BBS folder already occupies this name. Tests the folder, not
    // the bbs.json — Rename moves the whole folder and Directory.Move throws if
    // the destination exists at all, even a stray folder with no bbs.json (a
    // half-deleted BBS, or one holding only nested profiles). Callers gate their
    // rename clash-check on this so they refuse the same cases Move would reject.
    public bool Exists(string bbsName) =>
        !string.IsNullOrWhiteSpace(bbsName) && Usable("Looking for a BBS", bbsName)
        && Directory.Exists(AppPaths.BbsFolder(bbsName));

    // Delete a BBS — removes the entire BBS/{name}/ folder (primary file +
    // all side-files). No-op if the folder doesn't exist, or the name is not one a
    // BBS can have. The folder is checked once more as a path before it goes: only
    // ever a folder inside the BBS folder, never that folder or one above it.
    public void Delete(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return;
        const string operation = "Deleting a BBS";
        if (!Usable(operation, bbsName)) return;
        PathGuard.DeleteTree(AppPaths.BbsFolder(bbsName), AppPaths.BbsDir, operation);
    }

    // Rename a BBS in place: move the whole BBS/{old}/ folder — bbs.json,
    // every override side-file, AND every nested character profile — to the new
    // name, then rewrite the primary file's Name field. A folder move keeps the
    // nested profiles intact; a Delete(old)+Save(new) would recursively destroy
    // them. Returns whether it was renamed: false, with nothing changed, if the
    // source folder is missing or either name is not one a BBS can have. Throws if
    // the destination already exists (the caller resolves clashes first).
    // Case-only renames never reach here — the Settings Apply path gates on an
    // OrdinalIgnoreCase inequality, and Directory.Move can't do a case-only move
    // on a case-insensitive filesystem.
    public bool Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        const string operation = "Renaming a BBS";
        if (!Usable(operation, oldName) || !Usable(operation, newName)) return false;
        string oldFolder = AppPaths.BbsFolder(oldName);
        string newFolder = AppPaths.BbsFolder(newName);
        if (!Directory.Exists(oldFolder)) return false;
        if (Directory.Exists(newFolder))
            throw new IOException($"A BBS folder already exists at '{newFolder}'.");

        Directory.Move(oldFolder, newFolder);

        // The moved bbs.json still carries the old Name — bring it in line.
        if (Get(newName) is { } profile)
        {
            profile.Name = newName;
            JsonStore.Save(AppPaths.BbsProfileFile(newName), profile);
        }
        return true;
    }

    // Enumerate every BBS that has a primary bbs.json on disk. The folder name
    // (= BBS name) is yielded, alphabetical order optional at the caller.
    // Folders missing a bbs.json are skipped — they aren't fully initialised yet.
    public IEnumerable<string> ListNames()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string folder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            // A folder's own name is one path segment, plain or not; nothing that
            // would lead elsewhere is ever listed.
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(name) || name.Equals(AppPaths.RefusedName, StringComparison.OrdinalIgnoreCase)
                || !Usable("Listing BBSes", name)) continue;
            if (!File.Exists(AppPaths.BbsProfileFile(name))) continue;
            yield return name;
        }
    }
}
