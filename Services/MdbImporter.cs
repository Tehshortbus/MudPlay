using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using JetDatabaseReader;

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
/// Backed by <see cref="AccessReader"/> from JetDatabaseReader — a
/// pure-managed Jet3 / Jet4 / ACCDB reader. No native dependencies, no
/// OLE DB / ACE / ODBC / Wine: the same binary reads Jet on Windows,
/// Linux, and macOS.
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
    /// An <see cref="MdbImportResult"/> carrying success, the on-disk
    /// folder name, a human-readable message safe to show in a dialog,
    /// and counts (tables found / imported / skipped, plus total rows
    /// imported). Counts are zero on failure paths; the message field
    /// always carries enough detail to surface in the Program Log.
    /// </returns>
    public async Task<MdbImportResult> ImportAsync(
        string mdbFilePath,
        string? targetSubfolder = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mdbFilePath))
            return MdbImportResult.Failure($"Database file not found: {mdbFilePath}");

        string folderName = targetSubfolder ?? Path.GetFileNameWithoutExtension(mdbFilePath);
        string outputPath = Path.Combine(GameDataRoot, folderName);
        Directory.CreateDirectory(outputPath);

        // Reader runs synchronously off the calling thread; hop to a
        // worker so the UI stays responsive during large imports.
        return await Task.Run(() => ImportCore(mdbFilePath, folderName, outputPath, cancellationToken),
                              cancellationToken);
    }

    private async Task<MdbImportResult> ImportCore(
        string mdbFilePath,
        string folderName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using AccessReader reader = AccessReader.Open(mdbFilePath);
            OnStatusChanged?.Invoke("Opened database…");

            IReadOnlyList<string> tables = FilterUserTables(reader.ListTables());
            OnStatusChanged?.Invoke($"Found {tables.Count} user tables");

            int tablesDone = 0;
            int totalRows = 0;
            List<string> imported = new();
            List<string> skipped = new();
            OnProgressChanged?.Invoke(0, tables.Count);

            foreach (string tableName in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnStatusChanged?.Invoke($"Importing {tableName}…");

                try
                {
                    int rowCount = await ExportTableAsync(reader, tableName, outputPath, cancellationToken);
                    totalRows += rowCount;
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
            message += $"\n\nTotal rows imported: {totalRows:N0}\nOutput: {outputPath}";

            return new MdbImportResult(
                Success: true,
                Message: message,
                FolderName: folderName,
                TablesFound: tables.Count,
                TablesImported: imported.Count,
                TablesSkipped: skipped.Count,
                RowsImported: totalRows);
        }
        catch (OperationCanceledException)
        {
            return MdbImportResult.Failure("Import was cancelled.");
        }
        catch (UnauthorizedAccessException)
        {
            return MdbImportResult.Failure(
                $"Permission denied opening: {mdbFilePath}\n" +
                "Move the file to a folder where you have full read/write access.");
        }
        catch (IOException ex) when (IsFileLockIOException(ex))
        {
            return MdbImportResult.Failure(
                $"File is locked by another process: {mdbFilePath}\n" +
                "Close any program that may have it open and try again.");
        }
        catch (FileNotFoundException)
        {
            return MdbImportResult.Failure($"Database file not found: {mdbFilePath}");
        }
        catch (Exception ex)
        {
            return MdbImportResult.Failure($"Could not read database: {mdbFilePath}\n\n{ex.Message}");
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
        AccessReader reader,
        string tableName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // ReadTable returns a typed DataTable — column.DataType is the
        // CLR mapping (int / decimal / DateTime / string / byte[] / etc.).
        DataTable dt = reader.ReadTable(tableName);
        int totalRows = dt.Rows.Count;
        OnStatusChanged?.Invoke($"  {totalRows} rows");

        string[] columns = new string[dt.Columns.Count];
        for (int i = 0; i < dt.Columns.Count; i++)
            columns[i] = dt.Columns[i].ColumnName;

        List<Dictionary<string, object?>> rows = new(capacity: totalRows);
        int rowsDone = 0;
        int lastReportedPercent = 0;

        foreach (DataRow dr in dt.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, object?> row = new(columns.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Length; i++)
            {
                object cell = dr[i];
                row[columns[i]] = cell is DBNull ? null : NormalizeValue(cell);
            }
            rows.Add(row);
            rowsDone++;

            if (totalRows > 0)
            {
                int percent = rowsDone * 100 / totalRows;
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    OnRowProgress?.Invoke(tableName, rowsDone, totalRows);
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

    // Marshal Jet column types to JSON-friendly primitives. Most values
    // already come through as .NET primitives; byte[] (OLE / binary
    // columns) gets base64'd so it round-trips through JSON cleanly,
    // and DateTime serializes as ISO 8601 round-trip "O" format.
    private static object? NormalizeValue(object value) => value switch
    {
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

/// <summary>
/// Outcome of one <see cref="MdbImporter.ImportAsync"/> invocation. The
/// caller uses the counts to compose status text and to recognise the
/// MajorMUD MDB shape (9 user tables = old realm format, 10 = new
/// format; anything less is a malformed / truncated MDB).
/// </summary>
/// <param name="Success">True when the database opened and every reachable table was at least attempted.</param>
/// <param name="Message">Multi-line summary safe for the Program Log.</param>
/// <param name="FolderName">On-disk subfolder under <see cref="AppPaths.GameDataRoot"/> the JSON landed in; empty on failure.</param>
/// <param name="TablesFound">User tables enumerated from the MDB (MSys* + ~tmp filtered out).</param>
/// <param name="TablesImported">Tables successfully written to JSON.</param>
/// <param name="TablesSkipped">Tables that the importer attempted but a per-table error aborted (see <see cref="MdbImporter.OnError"/> for the reasons).</param>
/// <param name="RowsImported">Sum of rows across every imported table.</param>
public readonly record struct MdbImportResult(
    bool   Success,
    string Message,
    string FolderName,
    int    TablesFound,
    int    TablesImported,
    int    TablesSkipped,
    int    RowsImported)
{
    /// <summary>Build a failure result. All counts zero, folder name empty.</summary>
    public static MdbImportResult Failure(string message)
        => new(false, message, string.Empty, 0, 0, 0, 0);
}
