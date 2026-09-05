using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudPlay.Services;

// Seeds a game-data set with the base navigation loops + GOTO favourites shipped
// (embedded) under Defaults/nav-seed/{realm}/. Runs on MDB import AND on every
// set-activate, so starter content added in a LATER app version reaches an
// already-imported set on the next launch.
//
// Rules:
//  - Additive: never overwrites an existing loop file or drops an existing favourite.
//  - Once-per-item (respects deletions): a per-set ledger records every item already
//    OFFERED (loop relative-path + favourite key + folder name). An item in the ledger
//    is never re-added — so a loop/GOTO the user deleted stays deleted — while a NEW
//    bundled item, absent from the ledger, is added.
//  - Migration: a set seeded under the old binary .nav-seeded marker (pre-ledger)
//    seeds its ledger with EVERYTHING currently bundled and adds nothing, so no
//    already-shipped item — including ones the user deleted — is resurrected. Only
//    genuinely-new future items land after that.
//  - Realm-matched (stock vs paradigm via Info.json Legit); best-effort (a failure
//    logs and leaves the set as-is rather than blocking).
public static class NavSeedBootstrapper
{
    public static void SeedIfNeeded(string setName, LogService? log = null)
    {
        if (string.IsNullOrWhiteSpace(setName)) return;
        string realm = GameDataRealm.Resolve(setName);
        Apply(realm,
              AppPaths.BundledNavSeedDir(realm),
              AppPaths.GameDataSetLoopsFolder(setName),
              AppPaths.GameDataSetFavoritesFile(setName),
              AppPaths.NavSeedLedgerFile(setName),
              AppPaths.NavSeedMarkerFile(setName),
              setName, log);
    }

    // The path-resolved core (public for tests, which pass scratch dirs so nothing
    // touches the real data root). bundle = the realm's unzipped nav-seed dir.
    public static void Apply(string realm, string bundle, string loopsDest, string favDest,
                             string ledgerPath, string legacyMarker, string setName, LogService? log = null)
    {
        if (!Directory.Exists(bundle))
        {
            // Dev build or no bundle for this realm: skip WITHOUT recording, so a
            // later build that ships the bundle can still seed this set.
            log?.Log(LogSeverity.Info, "NavSeed",
                $"No seed bundle for realm '{realm}' at '{bundle}'; set '{setName}' left unseeded.");
            return;
        }

        NavSeedLedger? ledger = LoadLedger(ledgerPath);

        // Pre-ledger set (old binary marker): treat everything currently bundled as
        // already-offered so nothing shipped before this feature — including the
        // user's deletions — is re-added. Only future-new items land afterwards.
        if (ledger is null && File.Exists(legacyMarker))
        {
            ledger = BuildLedgerFromBundle(bundle);
            SaveLedger(ledgerPath, ledger);
            try { File.Delete(legacyMarker); } catch { /* best-effort */ }
            log?.Log(LogSeverity.Info, "NavSeed",
                $"Migrated set '{setName}' ({realm}) to nav-seed ledger — " +
                $"{ledger.Loops.Count} loop(s) + {ledger.Favorites.Count} favourite(s) " +
                "marked already-applied (no re-adds).");
            return;
        }

        ledger ??= new NavSeedLedger();

        try
        {
            // Idempotent — runs on every activate, so only touch disk when the ledger
            // actually grew (a new bundled item was offered this pass).
            int before = ledger.Loops.Count + ledger.Favorites.Count + ledger.Folders.Count;
            int loops = ApplyLoops(Path.Combine(bundle, "Loops"), loopsDest, ledger);
            int favs = ApplyFavourites(Path.Combine(bundle, "Favorites.json"), favDest, ledger);
            bool grew = ledger.Loops.Count + ledger.Favorites.Count + ledger.Folders.Count != before;
            if (grew) SaveLedger(ledgerPath, ledger);
            if (loops > 0 || favs > 0)
                log?.Log(LogSeverity.Info, "NavSeed",
                    $"Seeded set '{setName}' ({realm}): +{loops} loop(s), +{favs} favourite(s).");
        }
        catch (Exception ex)
        {
            log?.Log(LogSeverity.Warn, "NavSeed",
                $"Failed to seed set '{setName}' from realm '{realm}': {ex.Message}");
        }
    }

    // Copy each bundled *.loop whose relative path isn't already in the ledger into
    // dst (preserving the sub-folder tree, NEVER overwriting an existing file), and
    // record it as offered. A loop already in the ledger is skipped — deletions stay
    // deleted. Returns the count of files actually copied.
    private static int ApplyLoops(string srcLoops, string dstLoops, NavSeedLedger ledger)
    {
        if (!Directory.Exists(srcLoops)) return 0;
        var offered = new HashSet<string>(ledger.Loops, StringComparer.Ordinal);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(srcLoops, "*.loop", SearchOption.AllDirectories))
        {
            string rel = LoopIdentity(srcLoops, file);
            if (!offered.Add(rel)) continue;    // already offered — a deleted loop stays gone
            ledger.Loops.Add(rel);
            string target = Path.Combine(dstLoops, Path.GetRelativePath(srcLoops, file));
            if (File.Exists(target)) continue;  // preserve a loop the user already has
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            copied++;
        }
        return copied;
    }

    // Union the bundle's Favorites/FavoriteFolders into the set's — but only entries
    // whose identity isn't already in the ledger (so a deleted favourite/folder is
    // never re-added). Records every offered identity. Returns favourites added.
    private static int ApplyFavourites(string src, string dst, NavSeedLedger ledger)
    {
        if (!File.Exists(src)) return 0;
        JsonObject seed = JsonNode.Parse(File.ReadAllText(src))?.AsObject() ?? new JsonObject();

        JsonObject cur;
        if (File.Exists(dst))
            cur = JsonNode.Parse(File.ReadAllText(dst))?.AsObject() ?? new JsonObject();
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            cur = new JsonObject();
        }

        JsonArray curFavs = cur["Favorites"] as JsonArray ?? new JsonArray();
        var present = new HashSet<string>(curFavs.Select(FavKey), StringComparer.Ordinal);
        var offered = new HashSet<string>(ledger.Favorites, StringComparer.Ordinal);
        int added = 0;
        foreach (JsonNode? f in (seed["Favorites"] as JsonArray) ?? new JsonArray())
        {
            if (f is null) continue;
            string key = FavKey(f);
            if (!offered.Add(key)) continue;        // already offered — deletion stays
            ledger.Favorites.Add(key);
            if (!present.Add(key)) continue;        // user already has an equivalent
            curFavs.Add(f.DeepClone());
            added++;
        }
        cur["Favorites"] = curFavs;

        JsonArray curFolders = cur["FavoriteFolders"] as JsonArray ?? new JsonArray();
        var haveFolders = new HashSet<string>(curFolders.Select(x => x?.ToString() ?? ""), StringComparer.Ordinal);
        var offeredFolders = new HashSet<string>(ledger.Folders, StringComparer.Ordinal);
        bool foldersChanged = false;
        foreach (JsonNode? fld in (seed["FavoriteFolders"] as JsonArray) ?? new JsonArray())
        {
            string name = fld?.ToString() ?? "";
            if (name.Length == 0) continue;
            if (!offeredFolders.Add(name)) continue;
            ledger.Folders.Add(name);
            if (haveFolders.Add(name)) { curFolders.Add(name); foldersChanged = true; }
        }
        cur["FavoriteFolders"] = curFolders;

        // Only rewrite the set's file when we actually added something — this runs on
        // every activate, and an unchanged rewrite would churn the file needlessly.
        if (added > 0 || foldersChanged)
            File.WriteAllText(dst, cur.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return added;
    }

    // Record every currently-bundled item as offered without copying anything — the
    // migration path for a set seeded under the old binary marker.
    private static NavSeedLedger BuildLedgerFromBundle(string bundle)
    {
        var ledger = new NavSeedLedger();
        string loops = Path.Combine(bundle, "Loops");
        if (Directory.Exists(loops))
            foreach (string file in Directory.EnumerateFiles(loops, "*.loop", SearchOption.AllDirectories))
                ledger.Loops.Add(LoopIdentity(loops, file));

        string favFile = Path.Combine(bundle, "Favorites.json");
        if (File.Exists(favFile))
        {
            JsonObject o = JsonNode.Parse(File.ReadAllText(favFile))?.AsObject() ?? new JsonObject();
            foreach (JsonNode? f in (o["Favorites"] as JsonArray) ?? new JsonArray())
                if (f is not null) ledger.Favorites.Add(FavKey(f));
            foreach (JsonNode? fld in (o["FavoriteFolders"] as JsonArray) ?? new JsonArray())
            {
                string name = fld?.ToString() ?? "";
                if (name.Length > 0) ledger.Folders.Add(name);
            }
        }
        return ledger;
    }

    // Loop identity = its path relative to the Loops/ root, separators normalised to
    // '/' so the ledger is identical across Linux/Windows/macOS and matches the zip.
    private static string LoopIdentity(string loopsRoot, string file) =>
        Path.GetRelativePath(loopsRoot, file).Replace('\\', '/');

    private static string FavKey(JsonNode? f) =>
        $"{f?["Map"]}/{f?["Room"]}|{f?["Label"]}|{f?["Folder"]}";

    private static NavSeedLedger? LoadLedger(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<NavSeedLedger>(File.ReadAllText(path)); }
        catch { return null; }   // corrupt ledger → treat as absent (re-derives safely)
    }

    private static void SaveLedger(string path, NavSeedLedger ledger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(ledger,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // Per-set record of nav-seed items already offered. Anything listed here is never
    // re-added, so user deletions stick; a bundled item absent from all three lists is
    // new and gets added on the next apply.
    private sealed class NavSeedLedger
    {
        public List<string> Loops { get; set; } = new();
        public List<string> Favorites { get; set; } = new();
        public List<string> Folders { get; set; } = new();
    }
}
