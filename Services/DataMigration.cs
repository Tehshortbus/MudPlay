using System.IO;
using System.Linq;

namespace FujinTerm.Services;

// One-shot migration that moves the legacy flat per-BBS settings file
// (Data/BBS/{name}.json) into the per-folder layout (Data/BBS/{name}/bbs.json).
// Runs at startup before any service touches the filesystem. Idempotent —
// re-running on an already-migrated tree is a no-op. Defensive — never deletes
// the source file unless the destination write succeeded.
//
// Character profiles are NOT migrated automatically: they moved from a flat
// Data/profiles/{char}/ layout to BBS-scoped Data/BBS/{bbs}/profiles/{char}/,
// and the correct destination BBS can't be inferred safely. Users relocate any
// pre-existing profiles by hand.
//
// The new layout exists so each tier can grow helper files alongside its primary
// settings JSON without forcing monolithic blobs (per-set override side-files,
// per-BBS favorites, per-character macros / triggers / events / death history /
// etc.). The migration just flattens-to-folders; no schema rewrites.
public static class DataMigration
{
    // Walk the legacy paths and relocate any flat files into the new per-name
    // folders. Safe to call every startup — no-op when the new layout already
    // exists. Writes a single info line to log per file moved.
    public static void RunIfNeeded(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);

        int moved = MigrateLegacyFlatJsons(
            sourceDir: AppPaths.BbsDir,
            targetFolderResolver: AppPaths.BbsFolder,
            targetFileResolver:   AppPaths.BbsProfileFile,
            tierLabel: "BBS",
            log: log);

        if (moved > 0)
            log.Info("DataMigration", $"Migrated {moved} legacy flat-file(s) to per-tier folder layout.");
    }

    private static int MigrateLegacyFlatJsons(
        string sourceDir,
        Func<string, string> targetFolderResolver,
        Func<string, string> targetFileResolver,
        string tierLabel,
        LogService log)
    {
        if (!Directory.Exists(sourceDir)) return 0;

        // Legacy layout: flat .json files directly in sourceDir.
        // New layout: subfolder per name, primary file inside.
        // We move each legacy flat file into its target folder; only
        // delete the source when the target write succeeded.
        int moved = 0;
        foreach (string legacyFile in Directory.EnumerateFiles(sourceDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(legacyFile);
            if (string.IsNullOrWhiteSpace(name)) continue;

            string targetFolder = targetFolderResolver(name);
            string targetFile   = targetFileResolver(name);

            // Already migrated (folder + file both present)? Skip and
            // log so the user can manually reconcile if the legacy
            // file still hangs around.
            if (File.Exists(targetFile))
            {
                log.Warn("DataMigration",
                    $"{tierLabel} '{name}': legacy file at '{legacyFile}' AND new file at '{targetFile}' — " +
                    "skipping migration; resolve manually (the new file wins).");
                continue;
            }

            try
            {
                Directory.CreateDirectory(targetFolder);
                File.Copy(legacyFile, targetFile, overwrite: false);
                // Only after the copy succeeds do we remove the legacy file.
                File.Delete(legacyFile);
                log.Info("DataMigration",
                    $"{tierLabel} '{name}': moved '{legacyFile}' → '{targetFile}'.");
                moved++;
            }
            catch (Exception ex)
            {
                log.Error("DataMigration",
                    $"{tierLabel} '{name}': migration failed for '{legacyFile}' — left in place. {ex.Message}");
            }
        }
        return moved;
    }
}
