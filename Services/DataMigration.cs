using System.IO;
using System.Linq;

namespace MudPlay.Services;

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

    // One-time forced retirement of the pre-split message data. Before the Messages
    // seed was realm-flavored, the catalogue lived in a single Global/Messages.seed.json
    // and (once edited) per-set "game data/{set}/messages.json" — both of which OVERRIDE
    // the new flavored bundled seeds, so an updated install would otherwise keep loading
    // the stale, unsplit catalogue. This runs ONCE (guarded by a marker file), backing
    // each stale file up to a sibling .bak before removing it, so the next set load falls
    // through to the realm-flavored seed. A user who misses a hand-added record can
    // recover it from the .bak.
    //
    // REMOVE-AFTER-ROLLOUT: this method and its startup call are defunct on any install
    // that already carries the marker; delete them in a later release (tracked as a
    // GitHub issue). Runs after EnsureGlobalSeedsBootstrapped has placed the flavored
    // seeds, so the per-set delete leaves a valid seed to fall through to.
    public static void RetireLegacyMessagesOnce(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);

        string marker = Path.Combine(AppPaths.DataRoot, "Global", ".messages-flavored-migrated");
        if (File.Exists(marker)) return;

        int retired = BackupAndDelete(AppPaths.LegacyMessagesSeedFile, log);
        if (Directory.Exists(AppPaths.GameDataRoot))
            foreach (string setDir in Directory.EnumerateDirectories(AppPaths.GameDataRoot))
                retired += BackupAndDelete(Path.Combine(setDir, "messages.json"), log);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker,
                "The pre-split Messages catalogue was retired to .bak files; the app now loads the " +
                "realm-flavored Messages.{stock,paradigm}.seed.json. Delete this marker to re-run.");
        }
        catch { /* no marker ⇒ re-runs next launch, harmlessly: the stale files are already gone */ }

        if (retired > 0)
            log.Info("DataMigration",
                $"Retired {retired} pre-split message file(s) to .bak; catalogue now loads from the realm-flavored seeds.");
    }

    // One-time forced reseed of the Messages catalogue. It has recurated the shipped seeds
    // twice now: (1) the item-on-use → cast-spell release (an item's on-use / proc message
    // moved onto the cast SPELL record, weapon combat-proc pure-damage records dropped, many
    // realm lines corrected), and (2) the ailment-flag re-derivation — every message's
    // Blinded/Confused/Poisoned/MovementPrevented Effects flag re-derived from its linked
    // spell's game-data ability codes (so e.g. a confuse spell whose messages.md decode
    // missed the flag now carries it). A user's older Global seed OR per-set edits would keep
    // loading the stale catalogue (both resolve above the freshly-shipped bundled seed), so
    // this runs ONCE per marker, backs up each stale file to a sibling .bak, then copies the
    // new bundled seed into Global. The marker is bumped whenever the bundled seed is
    // recurated, so every install re-seeds once more. Users re-apply any hand edits afterward
    // (the Incomplete Messages tab's "Upload edits" button is the intended workflow); a lost
    // record is recoverable from the .bak.
    //
    // REMOVE-AFTER-ROLLOUT: defunct on any install that already carries the current marker;
    // delete this and its startup call a few releases out. Runs AFTER
    // EnsureGlobalSeedsBootstrapped (so a fresh install already has a Global seed to back up)
    // and after RetireLegacyMessagesOnce (so the pre-split retirement happens first).
    public static void ForceMessageReseedOnce(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);

        // Marker bumped for the ailment-flag re-derivation — an install that already carries
        // the prior .messages-reseed-castssp marker re-seeds once more to pick up the
        // corrected Effects flags.
        string marker = Path.Combine(AppPaths.DataRoot, "Global", ".messages-reseed-ailment-flags");
        if (File.Exists(marker)) return;

        int reseeded = 0;

        // Per-set user message edits (game data/{set}/messages.json) shadow the Global seed
        // — retire them so the set falls through to the reseeded Global.
        if (Directory.Exists(AppPaths.GameDataRoot))
            foreach (string setDir in Directory.EnumerateDirectories(AppPaths.GameDataRoot))
                reseeded += BackupAndDelete(Path.Combine(setDir, "messages.json"), log);

        // Global realm seeds: back up + remove the stale copy, then lay the freshly-shipped
        // bundled seed down in its place (copy-if-missing bootstrap already skipped it,
        // because the stale Global copy existed, so re-copy explicitly here).
        foreach (string realm in new[] { "stock", "paradigm" })
        {
            string global = AppPaths.MessagesSeedFile(realm);
            reseeded += BackupAndDelete(global, log);
            string bundled = AppPaths.BundledMessagesSeedFile(realm);
            if (!File.Exists(bundled)) continue;   // dev build without the bundled copy — skip
            try { File.Copy(bundled, global, overwrite: false); }
            catch (Exception ex)
            {
                log.Warn("DataMigration", $"reseed: couldn't place bundled '{realm}' seed into Global: {ex.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker,
                "Messages were recurated (ailment Effects flags re-derived from linked spells' game-data " +
                "ability codes, plus the earlier item-on-use → cast-spell recuration); the prior Global seed " +
                "and per-set edits were backed up to .bak and re-seeded from the shipped copy. " +
                "Delete this marker to re-run.");
        }
        catch { /* no marker ⇒ re-runs next launch, harmlessly: the stale files are already gone */ }

        if (reseeded > 0)
            log.Info("DataMigration",
                $"Force-reseeded {reseeded} message file(s) from the shipped seed for the item-cast " +
                "consolidation; prior copies saved to .bak.");
    }

    // Copy path → path.bak (overwriting a prior .bak) then delete the original. Returns 1
    // on success, 0 when the file is absent or the move fails (left in place, logged).
    private static int BackupAndDelete(string path, LogService log)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
            File.Delete(path);
            log.Info("DataMigration", $"backed up + removed stale '{path}' → '{path}.bak'");
            return 1;
        }
        catch (Exception ex)
        {
            log.Warn("DataMigration", $"could not retire '{path}': {ex.Message}");
            return 0;
        }
    }
}
