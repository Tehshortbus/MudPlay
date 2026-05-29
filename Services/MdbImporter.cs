using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text.Json;

// OLE DB is annotated [SupportedOSPlatform("windows")] in the BCL, but
// our design hosts ACE under Wine on Linux too — see
// MDB-Import-Implementation-Guide.md §1 and the IsAceInstalled() guard
// below, which returns a helpful message instead of throwing when no
// ACE provider is registered. CA1416 fires on every OleDb* type
// touched here; suppress in this file only.
#pragma warning disable CA1416

namespace FujinTerm.Services;

/// <summary>
/// Imports every user table from a Microsoft Access <c>.mdb</c> /
/// <c>.accdb</c> database into a folder of JSON files (one per table)
/// under <see cref="AppPaths.GameDataRoot"/>. Each subfolder of
/// <c>Data/game data/</c> becomes a switchable "game-data set" the
/// later <c>GameDataCache</c> can activate (Phase 5 PR 5.2).
/// </summary>
/// <remarks>
/// <para>
/// Requires the Microsoft Access Database Engine (ACE) redistributable
/// at matching process bitness. On Linux the redist must be installed
/// inside the Wine prefix used to host this process — native Linux
/// OLE DB does not exist. The <see cref="IsAceInstalled"/> /
/// <see cref="GetAceNotInstalledMessage"/> pair surfaces a helpful
/// message when the redist is missing instead of failing with a raw
/// COM error.
/// </para>
/// <para>
/// Wine compatibility note: this class probes ACE via
/// <see cref="System.Type.GetTypeFromProgID(string)"/> rather than
/// <c>OleDbEnumerator.GetRootEnumerator</c> because Wine's
/// <c>oledb32.dll</c> enumerator returns nothing even when ACE is
/// correctly registered. See <c>MDB-Import-Implementation-Guide.md</c>.
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

    // ----- ACE detection (Wine-compatible) ----------------------------------

    /// <summary>
    /// Probe the registry for the highest-versioned ACE OLE DB ProgID
    /// installed. Versions 16.0 / 15.0 / 14.0 / 12.0 cover the 2007 →
    /// 2016 redistributables.
    /// </summary>
    /// <returns>The ProgID string (e.g. <c>"Microsoft.ACE.OLEDB.16.0"</c>), or <c>null</c> if none is registered.</returns>
    private static string? GetAceProgId()
    {
        foreach (string version in new[] { "16.0", "15.0", "14.0", "12.0" })
        {
            string progId = $"Microsoft.ACE.OLEDB.{version}";
            if (Type.GetTypeFromProgID(progId) is not null) return progId;
        }
        return null;
    }

    /// <summary>True when ACE OLE DB is registered at this process's bitness.</summary>
    public static bool IsAceInstalled() => GetAceProgId() is not null;

    /// <summary>
    /// User-facing message explaining how to install ACE — used by both
    /// the early-exit guard in <see cref="ImportAsync"/> and by the
    /// import-dialog UX (Phase 5 PR 5.3+) to display when the user
    /// picks Import .mdb… on a host that doesn't have ACE.
    /// </summary>
    public static string GetAceNotInstalledMessage() =>
        "Microsoft Access Database Engine (ACE) is not installed.\n\n" +
        "ACE is a free Microsoft component required to read .mdb / .accdb files.\n\n" +
        "To install:\n" +
        "1. Download from: https://www.microsoft.com/en-us/download/details.aspx?id=54920\n" +
        "2. Choose the bitness that matches THIS application (32-bit or 64-bit).\n" +
        "   Mismatched bitness is the most common cause of \"not installed\" even after install.\n" +
        "3. Run the installer.\n" +
        "4. Restart the application.\n\n" +
        "On Linux, install ACE inside the Wine prefix used to host FujinTerm.";

    // ----- Import entry point ----------------------------------------------

    /// <summary>
    /// Read every user table from <paramref name="mdbFilePath"/> and
    /// write each as <c>{table}.json</c> under <c>GameDataRoot / folderName</c>.
    /// </summary>
    /// <param name="mdbFilePath">Absolute path to the <c>.mdb</c> / <c>.accdb</c> file.</param>
    /// <param name="targetSubfolder">
    /// Subfolder name under <see cref="GameDataRoot"/>. Defaults to the
    /// file's basename. The Phase 5 PR 5.4+ import dialog feeds the
    /// user's chosen set name here.
    /// </param>
    /// <param name="cancellationToken">Cancellation propagated to OLE DB reads + file writes.</param>
    /// <returns>
    /// <c>success</c> = true when ACE was found, the connection opened,
    /// and every reachable table was written (per-table read errors are
    /// reported via <see cref="OnError"/> but do not flip success false).
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

        string? aceProgId = GetAceProgId();
        if (aceProgId is null)
            return (false, GetAceNotInstalledMessage(), string.Empty);

        string folderName = targetSubfolder ?? Path.GetFileNameWithoutExtension(mdbFilePath);
        string outputPath = Path.Combine(GameDataRoot, folderName);
        Directory.CreateDirectory(outputPath);

        // Pick whichever ACE version actually answered the probe — hard-
        // coding 12.0 would break on systems that only have the 2016
        // redist installed.
        string connectionString = $"Provider={aceProgId};Data Source={mdbFilePath};Mode=Read;";

        try
        {
            using OleDbConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            OnStatusChanged?.Invoke("Connected to database…");

            IReadOnlyList<string> tables = GetUserTables(connection);
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
                    int rowCount = await ExportTableAsync(connection, tableName, outputPath, cancellationToken);
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
        catch (OleDbException ex)
        {
            return (false, TranslateOleDbException(ex, mdbFilePath), string.Empty);
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
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}", string.Empty);
        }
    }

    // ----- Table enumeration -----------------------------------------------

    private static IReadOnlyList<string> GetUserTables(OleDbConnection connection)
    {
        object?[] restrictions = new object?[] { null, null, null, "TABLE" };
        DataTable? schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, restrictions!);
        List<string> tables = new();
        if (schema is null) return tables;

        foreach (DataRow row in schema.Rows)
        {
            string? name = row["TABLE_NAME"]?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            // MSys* are Access metadata — ACE throws "no read permission".
            if (name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) continue;
            // ~TMP* are orphaned temp tables from crashed Access sessions.
            if (name.StartsWith('~')) continue;
            tables.Add(name);
        }
        tables.Sort(StringComparer.OrdinalIgnoreCase);
        return tables;
    }

    // ----- Per-table export -------------------------------------------------

    private async Task<int> ExportTableAsync(
        OleDbConnection connection,
        string tableName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // Bracket-quote the table name so spaces and reserved words work.
        using OleDbCommand countCommand = new($"SELECT COUNT(*) FROM [{tableName}]", connection);
        int totalRows = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        OnStatusChanged?.Invoke($"  {totalRows} rows");

        using OleDbCommand command = new($"SELECT * FROM [{tableName}]", connection);
        using OleDbDataReader reader = (OleDbDataReader)await command.ExecuteReaderAsync(cancellationToken);

        string[] columns = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
            columns[i] = reader.GetName(i);

        List<Dictionary<string, object?>> rows = new(capacity: Math.Max(16, totalRows));
        int rowsDone = 0;
        int lastReportedPercent = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            Dictionary<string, object?> row = new(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);
                row[columns[i]] = value == DBNull.Value ? null : value;
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

    // ----- Error translation -----------------------------------------------

    private static string TranslateOleDbException(OleDbException ex, string mdbFilePath)
    {
        string lower = ex.Message.ToLowerInvariant();

        if (lower.Contains("not a valid path") || lower.Contains("could not find file"))
            return $"Could not open database: {mdbFilePath}\n" +
                   "Verify the file exists and is a valid Access database.";

        if (lower.Contains("not registered"))
            return GetAceNotInstalledMessage();

        if (IsFileLockMessage(lower))
            return $"Database file is locked or permission-restricted: {mdbFilePath}\n\n" +
                   "Close any program that has it open, or copy it to a folder where " +
                   "you have full read/write permissions.";

        return $"Database error: {ex.Message}";
    }

    private static bool IsFileLockMessage(string lower)
        => lower.Contains("could not lock file") || lower.Contains("cannot open") ||
           lower.Contains("exclusive") || lower.Contains("locked") ||
           lower.Contains("permission") || lower.Contains("denied") ||
           lower.Contains("used by another process") || lower.Contains("locking violation");

    private static bool IsFileLockIOException(IOException ex)
    {
        string lower = ex.Message.ToLowerInvariant();
        return lower.Contains("locked") ||
               lower.Contains("used by another process") ||
               lower.Contains("sharing violation");
    }
}
