using System.Diagnostics;
using System.IO;

namespace FujinTerm.Services;

// Moves the entire AppPaths.DataRoot tree to a user-chosen destination, then
// restarts the app at the new location. Used by the Settings → General "Change
// data directory" flow.
//
// Sequence is deliberately copy-then-verify-then-delete:
//   1. Validate the destination (writable, not overlapping the source).
//   2. Copy every file under the source to the destination, preserving relative
//      paths.
//   3. Verify the copy by file count + total byte size.
//   4. Write the new path to AppPaths.PointerFile.
//   5. Delete the source tree.
//   6. Spawn a fresh app process and exit the current one.
// A failure at any step before the pointer-file write leaves both copies intact
// and the source untouched — the user can retry without data loss. A failure
// between pointer-write and source-delete leaves the source orphaned but the new
// location is canonical; the user can clean up the stale source folder by hand.
public static class DataRootRelocator
{
    // Reasons Validate can reject a destination, or Relocate can fail
    // mid-flight. The Settings UI maps each value to a user-facing message.
    public enum FailureReason
    {
        None,
        DestinationIsSource,
        DestinationInsideSource,
        DestinationNotEmpty,
        DestinationNotWritable,
        CopyFailed,
        VerifyFailed,
        PointerWriteFailed,
        SourceDeletePartial,
    }

    // Outcome of Plan — describes the move before the user commits. FileCount is
    // the total files under AppPaths.DataRoot; TotalBytes is their total size.
    public readonly record struct MovePlan(int FileCount, long TotalBytes);

    // Summary of AppPaths.DataRoot as it stands right now.
    public static MovePlan Plan()
    {
        if (!Directory.Exists(AppPaths.DataRoot)) return new MovePlan(0, 0);

        int count = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(AppPaths.DataRoot, "*", SearchOption.AllDirectories))
        {
            count++;
            try { bytes += new FileInfo(file).Length; }
            catch { /* file vanished mid-enumeration — best-effort total */ }
        }
        return new MovePlan(count, bytes);
    }

    // Validate the destination without moving anything. Returns
    // FailureReason.None on success. Same checks as Relocate runs internally, but
    // cheap and side-effect-free so the Settings UI can light up the OK button
    // live as the user picks.
    public static FailureReason Validate(string destination)
    {
        string src = Path.GetFullPath(AppPaths.DataRoot);
        string dst = Path.GetFullPath(destination);

        if (PathsEqual(src, dst))            return FailureReason.DestinationIsSource;
        if (IsDescendant(src, dst))          return FailureReason.DestinationInsideSource;
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
            return FailureReason.DestinationNotEmpty;

        // Write-probe: create the dest folder if absent, then drop & delete
        // a marker file. Catches read-only mounts, missing permissions, etc.
        try
        {
            Directory.CreateDirectory(dst);
            string probe = Path.Combine(dst, ".fujinterm-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch
        {
            return FailureReason.DestinationNotWritable;
        }

        return FailureReason.None;
    }

    // Execute the relocation. Returns FailureReason.None on success (caller
    // should then call RestartApp). On any other return value, the source folder
    // is still authoritative — the pointer file has NOT been written.
    // destination is the absolute path of the new data root; log receives Info /
    // Warn / Error breadcrumbs.
    public static FailureReason Relocate(string destination, LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);

        FailureReason gate = Validate(destination);
        if (gate != FailureReason.None) return gate;

        string src = Path.GetFullPath(AppPaths.DataRoot);
        string dst = Path.GetFullPath(destination);

        // 1) Copy
        int copied;
        long copiedBytes;
        try
        {
            (copied, copiedBytes) = CopyTree(src, dst);
        }
        catch (Exception ex)
        {
            log.Error("DataRootRelocator", $"Copy failed mid-flight: {ex.Message}");
            return FailureReason.CopyFailed;
        }

        // 2) Verify (file count + byte total must match the source)
        MovePlan expected = Plan();
        if (copied != expected.FileCount || copiedBytes != expected.TotalBytes)
        {
            log.Error("DataRootRelocator",
                $"Verify failed: copied {copied} files / {copiedBytes} bytes, " +
                $"expected {expected.FileCount} / {expected.TotalBytes}. Source left intact.");
            return FailureReason.VerifyFailed;
        }

        // 3) Pointer file — past this point, the new location is canonical.
        try
        {
            string pointerDir = Path.GetDirectoryName(AppPaths.PointerFile)!;
            Directory.CreateDirectory(pointerDir);
            File.WriteAllText(AppPaths.PointerFile, dst);
        }
        catch (Exception ex)
        {
            log.Error("DataRootRelocator",
                $"Pointer-file write failed: {ex.Message}. Source still authoritative.");
            return FailureReason.PointerWriteFailed;
        }

        // 4) Delete source (best-effort — the pointer-file write is the
        // commit point; a partial delete here leaves orphan files but the
        // new location is already canonical).
        try
        {
            Directory.Delete(src, recursive: true);
            log.Info("DataRootRelocator", $"Moved data root '{src}' → '{dst}' ({copied} files).");
            return FailureReason.None;
        }
        catch (Exception ex)
        {
            log.Warn("DataRootRelocator",
                $"Source delete after move failed: {ex.Message}. Old folder '{src}' " +
                "can be removed manually.");
            return FailureReason.SourceDeletePartial;
        }
    }

    // Spawn a fresh app process at Environment.ProcessPath and terminate the
    // current one. The new process re-enters AppPaths static-init, reads the
    // freshly-written pointer file, and resolves AppPaths.DataRoot to the new
    // location.
    public static void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
            }
            catch
            {
                // If spawn fails, fall through to Exit anyway — the user
                // can relaunch manually and the pointer file is already
                // written.
            }
        }
        Environment.Exit(0);
    }

    private static (int FileCount, long TotalBytes) CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        int count = 0;
        long bytes = 0;

        foreach (string srcDir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, srcDir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }

        foreach (string srcFile in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, srcFile);
            string dstFile = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, overwrite: false);
            count++;
            bytes += new FileInfo(dstFile).Length;
        }

        return (count, bytes);
    }

    private static bool PathsEqual(string a, string b)
    {
        StringComparison cmp = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Path.TrimEndingDirectorySeparator(a),
                             Path.TrimEndingDirectorySeparator(b), cmp);
    }

    private static bool IsDescendant(string ancestor, string candidate)
    {
        string a = Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar;
        string c = Path.TrimEndingDirectorySeparator(candidate);
        StringComparison cmp = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return c.StartsWith(a, cmp);
    }
}
