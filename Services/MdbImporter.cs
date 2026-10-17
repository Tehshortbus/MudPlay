using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using JetDatabaseReader;
using MudPlay.Game.GameData;

namespace MudPlay.Services;

// Imports every user table from a Microsoft Access .mdb / .accdb database into
// a folder of JSON files (one per table) under AppPaths.GameDataRoot. Each
// subfolder of game data/ becomes a switchable "game-data set" the
// GameDataCache can activate.
//
// Backed by AccessReader from JetDatabaseReader — a pure-managed Jet3 / Jet4 /
// ACCDB reader. No native dependencies, no OLE DB / ACE / ODBC / Wine: the same
// binary reads Jet on Windows, Linux, and macOS.
//
// "Every user table" means every table the database exposes — the importer has
// no allow-list. Access metadata tables (MSys*) and orphaned temp tables
// (names beginning with ~) are filtered out. This lets us pick up new
// MajorMUD-flavoured tables that future realm releases add without code changes.
public sealed class MdbImporter
{
    // Root directory imported sets land under.
    public string GameDataRoot { get; } = AppPaths.GameDataRoot;

    // Single-line status text — connection / table being read / completion.
    public event Action<string>? OnStatusChanged;

    // Non-fatal per-table errors. The importer continues with the next table.
    public event Action<string>? OnError;

    public MdbImporter()
    {
        Directory.CreateDirectory(GameDataRoot);
    }

    // ----- Import entry point ----------------------------------------------

    // Read every user table from the .mdb / .accdb at mdbFilePath and write each
    // as {table}.json under GameDataRoot/folderName. targetSubfolder names the
    // subfolder under GameDataRoot, defaulting to the file's basename (the
    // import-conflict dialog feeds the user's chosen set name here).
    //
    // Returns an MdbImportResult carrying success, the on-disk folder name, a
    // human-readable message safe to show in a dialog, and counts (tables found
    // / imported / skipped, plus total rows imported). Counts are zero on
    // failure paths; the message field always carries enough detail to surface
    // in the Program Log.
    public async Task<MdbImportResult> ImportAsync(
        string mdbFilePath,
        string? targetSubfolder = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mdbFilePath))
            return MdbImportResult.Failure($"Database file not found: {mdbFilePath}");

        string folderName = targetSubfolder ?? Path.GetFileNameWithoutExtension(mdbFilePath);
        // The set is named after the file, and a file can be called anything: the
        // name has to be one a set can have (a plain one, or a set already there).
        if (!PathGuard.Usable(GameDataRoot, folderName))
        {
            PathGuard.Refuse("Importing a database", "game-data set name", folderName);
            return MdbImportResult.Failure(
                $"{PathGuard.Show(folderName)} can't be used as a game-data set's name: " +
                $"{PathGuard.WhyNotPlain(folderName, "set name")} Rename the file and import it again.");
        }
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

            if (tables.Count == 0)
            {
                // The database opened but its catalog lists no importable game tables
                // (only Access system / application objects). Typically an .mdb that was
                // opened and saved in the Microsoft Access GUI, leaving its object catalog
                // damaged or uncompacted so the game tables are physically present but
                // detached from MSysObjects. Fail hard — never leave an empty set for the
                // caller to switch to (an empty set blanks the map / monsters / items) —
                // and surface both the fix and the reader's catalog scan for diagnosis.
                RemoveDirectoryIfEmpty(outputPath);
                return MdbImportResult.Failure(
                    $"No game tables found in: {mdbFilePath}\n\n" +
                    "The database opened, but its table catalog lists no game tables — only " +
                    "Microsoft Access internal objects. This usually means the .mdb was opened " +
                    "and edited in Microsoft Access and its catalog is damaged, or it was never " +
                    "compacted (the tables can be physically present yet unreferenced).\n\n" +
                    "Fix: open the file in Access and run Database Tools → \"Compact and Repair " +
                    "Database\", or re-export a fresh MDB from Nightmare Redux, then import again.\n\n" +
                    "Reader catalog scan:\n" + reader.LastDiagnostics);
            }

            int tablesDone = 0;
            int totalRows = 0;
            _roomCommandItems = 0;
            List<string> imported = new();
            List<string> skipped = new();

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
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Retry this ONE table once. A malformed / truncated write or a
                    // transient read hiccup usually clears on a second pass; a
                    // genuinely corrupt source keeps failing and is recorded as
                    // skipped, so the completion line (red, "N skipped — see Program
                    // Log") tells the user the table is unavailable. Other tables in
                    // the set are unaffected.
                    OnStatusChanged?.Invoke($"Retrying {tableName} after error: {ex.Message}");
                    try
                    {
                        int rowCount = await ExportTableAsync(reader, tableName, outputPath, cancellationToken);
                        totalRows += rowCount;
                        imported.Add($"{tableName} ({rowCount} rows)");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex2)
                    {
                        OnError?.Invoke($"Error importing {tableName} (failed after one retry): {ex2.Message}");
                        skipped.Add($"{tableName} ({ex2.Message})");
                    }
                }

                tablesDone++;
            }

            if (imported.Count == 0)
            {
                // Tables were listed but every one failed to read (see the per-table
                // errors above). Same rule as the no-tables case: don't switch to a set
                // that got nothing written into it.
                RemoveDirectoryIfEmpty(outputPath);
                return MdbImportResult.Failure(
                    $"No tables could be read from: {mdbFilePath}\n\n" +
                    $"All {tables.Count} table(s) failed to import:\n" +
                    string.Join("\n", skipped.Select(t => "  ⚠ " + t)));
            }

            string message =
                $"Import complete.\n\nImported {imported.Count} tables:\n" +
                string.Join("\n", imported.Select(t => $"  ✓ {t}"));
            if (skipped.Count > 0)
                message += $"\n\nSkipped {skipped.Count}:\n" +
                           string.Join("\n", skipped.Select(t => $"  ⚠ {t}"));
            bool hasLairs = imported.Any(t => t.StartsWith("Lairs (", StringComparison.OrdinalIgnoreCase));
            message += $"\n\nTotal rows imported: {totalRows:N0}"
                     + $"\nLairs table: {(hasLairs ? "yes" : "no")}"
                     + $"\nItems with a room-command source: {_roomCommandItems:N0}"
                     + $"\nOutput: {outputPath}";

            return new MdbImportResult(
                Success: true,
                Message: message,
                FolderName: folderName,
                TablesFound: tables.Count,
                TablesImported: imported.Count,
                TablesSkipped: skipped.Count,
                RowsImported: totalRows,
                HasLairsTable: hasLairs,
                RoomCommandItems: _roomCommandItems);
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

    // Items whose `Obtained From` carries a room-command entry, counted as the Items
    // table is written. Nothing in an export says which tool wrote it, so the entries
    // are found by being there (RoomCommandSourceParser).
    private int _roomCommandItems;

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
                }
            }
        }

        if (tableName.Equals("Items", StringComparison.OrdinalIgnoreCase))
            _roomCommandItems = rows.Count(r =>
                r.TryGetValue("Obtained From", out object? cell)
                && RoomCommandSourceParser.ParseCell(cell as string).Count > 0);

        string json = JsonSerializer.Serialize(rows, JsonOpts);
        string fileName = MakeFilesystemSafe(tableName) + ".json";
        string filePath = Path.Combine(outputPath, fileName);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        // Read the file back and confirm it parses. JsonSerializer always emits
        // valid JSON, so this catches a bad WRITE — a truncated / partial file from
        // a disk-full or interrupted flush — before it ships as a table that would
        // crash (now: be dropped by) every consumer that loads it. The caller
        // retries the whole table once on this throw; a persistent failure is then
        // reported as skipped so the user is told the table is unavailable.
        if (!TableJsonParses(filePath))
            throw new InvalidDataException(
                "written JSON did not parse back — the file may be truncated (disk full / interrupted write)");

        OnStatusChanged?.Invoke($"  Wrote {rows.Count} rows -> {fileName}");
        return rows.Count;
    }

    // Round-trip check for a just-written table file: true when it re-parses as
    // JSON.
    internal static bool TableJsonParses(string filePath)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(filePath));
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException)   { return false; }
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

    // Strip filesystem-invalid characters from a table name so it can safely
    // become a JSON filename. Internal — exposed via the test surface only.
    internal static string MakeFilesystemSafe(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim();
        return string.IsNullOrEmpty(safe) ? "_unnamed" : safe;
    }

    // Delete a freshly-created output folder when the import wrote nothing into it,
    // so a failed import never leaves an empty game-data set behind (switching to an
    // empty set poisons every engine — blank room graph, no items / monsters / spells).
    // Only removes an EMPTY directory: a failed re-import over an existing populated set
    // leaves that set's files untouched.
    internal static void RemoveDirectoryIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* best-effort cleanup; a leftover empty dir is harmless */ }
    }

    private static bool IsFileLockIOException(IOException ex)
    {
        string lower = ex.Message.ToLowerInvariant();
        return lower.Contains("locked") ||
               lower.Contains("used by another process") ||
               lower.Contains("sharing violation");
    }
}

// Outcome of one MdbImporter.ImportAsync invocation. The caller uses the counts
// to compose status text and to recognise the MajorMUD MDB shape: 9 user tables,
// or 10 with a Lairs table; extra tables beyond that are imported but unused, not
// an error. An export may also carry room-command item sources, with or without
// the Lairs table, so the two are reported separately.
//   Success        — true when the database opened AND at least one table was
//                    imported. An import that yields zero tables is a FAILURE:
//                    the caller must not switch to an empty set, so ImportAsync
//                    returns Failure (with the reader's catalog scan) rather than
//                    a "successful" empty import.
//   Message        — multi-line summary safe for the Program Log.
//   FolderName     — on-disk subfolder under AppPaths.GameDataRoot the JSON
//                    landed in; empty on failure.
//   TablesFound    — user tables enumerated from the MDB (MSys* + ~tmp filtered out).
//   TablesImported — tables successfully written to JSON.
//   TablesSkipped  — tables the importer attempted but a per-table error
//                    aborted (see OnError for the reasons).
//   RowsImported   — sum of rows across every imported table.
//   HasLairsTable  — the export carries the Lairs table.
//   RoomCommandItems — items whose sources include a room command.
public readonly record struct MdbImportResult(
    bool   Success,
    string Message,
    string FolderName,
    int    TablesFound,
    int    TablesImported,
    int    TablesSkipped,
    int    RowsImported,
    bool   HasLairsTable,
    int    RoomCommandItems)
{
    // Build a failure result. All counts zero, folder name empty.
    public static MdbImportResult Failure(string message)
        => new(false, message, string.Empty, 0, 0, 0, 0, false, 0);
}
