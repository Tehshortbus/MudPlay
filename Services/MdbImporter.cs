using System.IO;
using System.Linq;
using System.Text.Json;
using JackcessDotNet;

namespace FujinTerm.Services;

/// <summary>
/// Imports every user table from a Microsoft Access <c>.mdb</c> /
/// <c>.accdb</c> database into a folder of JSON files (one per table)
/// under <see cref="AppPaths.GameDataRoot"/>. Each subfolder of
/// <c>Data/game data/</c> becomes a switchable "game-data set" the
/// <see cref="GameDataCache"/> can activate.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="Database"/> from JackcessDotNet — a pure-managed
/// port of Apache Jackcess. No native dependencies, no OLE DB / ACE /
/// ODBC / Wine: the same binary reads Jet on Windows, Linux, and macOS.
/// </para>
/// <para>
/// "Every user table" means every table the database exposes — the
/// importer has no allow-list. Access metadata tables (<c>MSys*</c>)
/// and orphaned temp tables (names beginning with <c>~</c>) are
/// filtered out. This lets us pick up new MajorMUD-flavoured tables
/// that future realm releases add without code changes.
/// </para>
/// </remarks>
public sealed class MdbImporter
{
    /// <summary>Root directory imported sets land under (<see cref="AppPaths.GameDataRoot"/>).</summary>
    public string GameDataRoot { get; } = AppPaths.GameDataRoot;

    /// <summary>Single-line status text — connection / table being read / completion.</summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>Overall progress — fires once per table with <c>(tablesDone, tablesTotal)</c>.</summary>
    public event Action<int, int>? OnProgressChanged;

    /// <summary>Per-table row progress — fires at ~5% increments with <c>(tableName, rowsDone, rowsTotal)</c>.</summary>
    public event Action<string, int, int>? OnRowProgress;

    /// <summary>Non-fatal per-table errors. The importer continues with the next table.</summary>
    public event Action<string>? OnError;

    public MdbImporter()
    {
        Directory.CreateDirectory(GameDataRoot);
    }

    /// <summary>List of imported subfolders under <see cref="GameDataRoot"/>, alphabetical.</summary>
    public IReadOnlyList<string> GetGameDataFolders()
    {
        if (!Directory.Exists(GameDataRoot)) return Array.Empty<string>();
        return Directory.GetDirectories(GameDataRoot)
            .Select(Path.GetFileName)
            .Where(static n => !string.IsNullOrEmpty(n))
            .Select(static n => n!)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Absolute path of an imported subfolder by name.</summary>
    public string GetSubfolderPath(string folderName)
        => Path.Combine(GameDataRoot, folderName);

    // ----- Import entry point ----------------------------------------------

    /// <summary>
    /// Read every user table from <paramref name="mdbFilePath"/> and
    /// write each as <c>{table}.json</c> under <c>GameDataRoot / folderName</c>.
    /// </summary>
    /// <param name="mdbFilePath">Absolute path to the <c>.mdb</c> / <c>.accdb</c> file.</param>
    /// <param name="targetSubfolder">
    /// Subfolder name under <see cref="GameDataRoot"/>. Defaults to the
    /// file's basename. The Phase 5 import-conflict dialog feeds the
    /// user's chosen set name here.
    /// </param>
    /// <param name="cancellationToken">Cancellation propagated to row reads + file writes.</param>
    /// <returns>
    /// <c>success</c> = true when the database opened and every reachable
    /// table was written (per-table read errors are reported via
    /// <see cref="OnError"/> but do not flip success false).
    /// <c>message</c> is a human-readable summary safe to show in a
    /// dialog. <c>folderName</c> is the on-disk subfolder name written
    /// to — empty string on failure.
    /// </returns>
    public async Task<(bool success, string message, string folderName)> ImportAsync(
        string mdbFilePath,
        string? targetSubfolder = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mdbFilePath))
            return (false, $"Database file not found: {mdbFilePath}", string.Empty);

        string folderName = targetSubfolder ?? Path.GetFileNameWithoutExtension(mdbFilePath);
        string outputPath = Path.Combine(GameDataRoot, folderName);
        Directory.CreateDirectory(outputPath);

        // Jackcess reads off the calling thread; run the whole pipeline on
        // a worker so the UI stays responsive during large imports.
        return await Task.Run(() => ImportCore(mdbFilePath, folderName, outputPath, cancellationToken),
                              cancellationToken);
    }

    private async Task<(bool success, string message, string folderName)> ImportCore(
        string mdbFilePath,
        string folderName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using Database db = Database.Open(mdbFilePath);
            OnStatusChanged?.Invoke("Opened database…");

            IReadOnlyList<string> tables = FilterUserTables(db.ListTables(includeSystem: false));
            OnStatusChanged?.Invoke($"Found {tables.Count} user tables");

            int tablesDone = 0;
            List<string> imported = new();
            List<string> skipped = new();
            OnProgressChanged?.Invoke(0, tables.Count);

            foreach (string tableName in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnStatusChanged?.Invoke($"Importing {tableName}…");

                try
                {
                    int rowCount = await ExportTableAsync(db, tableName, outputPath, cancellationToken);
                    imported.Add($"{tableName} ({rowCount} rows)");
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Error importing {tableName}: {ex.Message}");
                    skipped.Add($"{tableName} ({ex.Message})");
                }

                tablesDone++;
                OnProgressChanged?.Invoke(tablesDone, tables.Count);
            }

            string message =
                $"Import complete.\n\nImported {imported.Count} tables:\n" +
                string.Join("\n", imported.Select(t => $"  ✓ {t}"));
            if (skipped.Count > 0)
                message += $"\n\nSkipped {skipped.Count}:\n" +
                           string.Join("\n", skipped.Select(t => $"  ⚠ {t}"));
            message += $"\n\nOutput: {outputPath}";

            return (true, message, folderName);
        }
        catch (OperationCanceledException)
        {
            return (false, "Import was cancelled.", string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return (false,
                $"Permission denied opening: {mdbFilePath}\n" +
                "Move the file to a folder where you have full read/write access.",
                string.Empty);
        }
        catch (IOException ex) when (IsFileLockIOException(ex))
        {
            return (false,
                $"File is locked by another process: {mdbFilePath}\n" +
                "Close any program that may have it open and try again.",
                string.Empty);
        }
        catch (FileNotFoundException)
        {
            return (false, $"Database file not found: {mdbFilePath}", string.Empty);
        }
        catch (Exception ex)
        {
            return (false,
                $"Could not read database: {mdbFilePath}\n\n{ex.Message}",
                string.Empty);
        }
    }

    // ----- Table enumeration -----------------------------------------------

    private static IReadOnlyList<string> FilterUserTables(IReadOnlyList<string> names)
    {
        List<string> tables = new(names.Count);
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            // MSys* are Access metadata; ~TMP* are orphaned temp tables.
            if (name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith('~')) continue;
            tables.Add(name);
        }
        tables.Sort(StringComparer.OrdinalIgnoreCase);
        return tables;
    }

    // ----- Per-table export -------------------------------------------------

    private async Task<int> ExportTableAsync(
        Database db,
        string tableName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Table table = db.GetTable(tableName);
        IReadOnlyList<Row> rawRows = table.ReadAllRows();
        OnStatusChanged?.Invoke($"  {rawRows.Count} rows");

        string[] columns = table.Columns.Select(c => c.Name).ToArray();

        List<Dictionary<string, object?>> rows = new(capacity: rawRows.Count);
        int rowsDone = 0;
        int lastReportedPercent = 0;

        foreach (Row raw in rawRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, object?> row = new(columns.Length, StringComparer.OrdinalIgnoreCase);
            foreach (string col in columns)
            {
                raw.TryGetValue(col, out object? value);
                row[col] = NormalizeValue(value);
            }
            rows.Add(row);
            rowsDone++;

            if (rawRows.Count > 0)
            {
                int percent = rowsDone * 100 / rawRows.Count;
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    OnRowProgress?.Invoke(tableName, rowsDone, rawRows.Count);
                }
            }
        }

        string json = JsonSerializer.Serialize(rows, JsonOpts);
        string fileName = MakeFilesystemSafe(tableName) + ".json";
        string filePath = Path.Combine(outputPath, fileName);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        OnStatusChanged?.Invoke($"  Wrote {rows.Count} rows -> {fileName}");
        return rows.Count;
    }

    // Marshal Jackcess scalar types to JSON-friendly primitives. Most
    // values come through as .NET primitives already; byte[] (OLE / binary
    // columns) gets base64'd so it round-trips through JSON cleanly.
    private static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Strip filesystem-invalid characters from a table name so it can
    /// safely become a JSON filename. Internal — exposed via the test
    /// surface only.
    /// </summary>
    internal static string MakeFilesystemSafe(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim();
        return string.IsNullOrEmpty(safe) ? "_unnamed" : safe;
    }

    private static bool IsFileLockIOException(IOException ex)
    {
        string lower = ex.Message.ToLowerInvariant();
        return lower.Contains("locked") ||
               lower.Contains("used by another process") ||
               lower.Contains("sharing violation");
    }
}
