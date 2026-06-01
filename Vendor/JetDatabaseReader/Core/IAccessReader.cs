#nullable disable
#pragma warning disable
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace JetDatabaseReader
{
    /// <summary>
    /// Interface for reading Microsoft Access JET databases (.mdb / .accdb).
    /// Provides methods for listing tables, reading data, and streaming large datasets.
    /// </summary>
    public interface IAccessReader : IDisposable
    {
        /// <summary>When true, GetUserTables logs verbose hex dumps for debugging. Default: false.</summary>
        bool DiagnosticsEnabled { get; set; }

        /// <summary>Maximum number of pages to keep in cache. 0 = unlimited, -1 = disabled. Default: 256 (1 MB for 4K pages).</summary>
        int PageCacheSize { get; set; }

        /// <summary>When true, uses parallel processing for reading multiple pages. Can improve performance for large tables. Default: false.</summary>
        bool ParallelPageReadsEnabled { get; set; }

        /// <summary>Diagnostic output populated after each call to <see cref="ListTables"/>.</summary>
        string LastDiagnostics { get; }

        /// <summary>
        /// Returns the column headers and up to <paramref name="maxRows"/> rows
        /// from the first user table, plus the table name and total table count.
        /// </summary>
        FirstTableResult ReadFirstTable(int maxRows = 100);

        /// <summary>Returns the names of all user tables in the database.</summary>
        List<string> ListTables();

        /// <summary>
        /// Returns name, stored row-count, and column-count for every user table.
        /// Calling this instead of <see cref="ListTables"/> avoids a duplicate catalog scan.
        /// </summary>
        List<TableStat> GetTableStats();

        /// <summary>
        /// Returns table metadata as a DataTable with columns: TableName, RowCount, ColumnCount.
        /// Ideal for binding to data grids or exporting to CSV/Excel.
        /// </summary>
        DataTable GetTablesAsDataTable();

        /// <summary>
        /// Scans all data pages to count live (non-deleted, non-overflow) rows for the specified table.
        /// This is slower than reading the TDEF RowCount (which may be stale), but always accurate.
        /// Use this after many deletes/imports when Compact & Repair hasn't been run.
        /// </summary>
        long GetRealRowCount(string tableName);

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with native CLR types.
        /// Rows are in <see cref="TableResult.Rows"/>.
        /// Use <see cref="ReadTableAsStrings"/> when raw string values are needed.
        /// </summary>
        TableResult ReadTable(string tableName, int maxRows);

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with all values as strings.
        /// Rows are in <see cref="StringTableResult.Rows"/>.
        /// </summary>
        StringTableResult ReadTableAsStrings(string tableName, int maxRows);

        /// <summary>
        /// Async overload of <see cref="ReadTable(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows with native CLR types asynchronously.
        /// </summary>
        Task<TableResult> ReadTableAsync(string tableName, int maxRows);

        /// <summary>
        /// Async overload of <see cref="ReadTableAsStrings(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows as strings asynchronously.
        /// </summary>
        Task<StringTableResult> ReadTableAsStringsAsync(string tableName, int maxRows);

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as properly typed object arrays without collecting them all in memory.
        /// Each element in the array is the native CLR type (int, DateTime, decimal, etc.).
        /// Ideal for large tables — use foreach to process one row at a time.
        /// This is the recommended method for streaming data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        IEnumerable<object[]> StreamRows(string tableName, IProgress<int> progress = null);

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as string arrays without collecting them all in memory.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="StreamRows"/> which returns properly typed data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        IEnumerable<string[]> StreamRowsAsStrings(string tableName, IProgress<int> progress = null);

        /// <summary>
        /// Reads the entire table into a DataTable with all columns typed as strings.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="ReadTable"/> which returns properly typed columns.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        DataTable ReadTableAsStringDataTable(string tableName = null, IProgress<int> progress = null);

        /// <summary>
        /// Returns rich metadata for all columns in the specified table.
        /// </summary>
        List<ColumnMetadata> GetColumnMetadata(string tableName);

        /// <summary>
        /// Returns statistical information about the database.
        /// </summary>
        DatabaseStatistics GetStatistics();

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// This is the recommended method for bulk reading.
        /// </summary>
        Dictionary<string, DataTable> ReadAllTables(IProgress<string> progress = null);

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings.
        /// Use this for compatibility scenarios.
        /// </summary>
        Dictionary<string, DataTable> ReadAllTablesAsStrings(IProgress<string> progress = null);

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// This is the recommended method for reading table data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        DataTable ReadTable(string tableName = null, IProgress<int> progress = null);

        // ── Async Methods ──────────────────────────────────────────────────

        /// <summary>Returns the names of all user tables in the database asynchronously.</summary>
        Task<List<string>> ListTablesAsync();

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns asynchronously.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// </summary>
        Task<DataTable> ReadTableAsync(string tableName = null, IProgress<int> progress = null);

        /// <summary>
        /// Returns statistical information about the database asynchronously.
        /// </summary>
        Task<DatabaseStatistics> GetStatisticsAsync();

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns asynchronously.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// </summary>
        Task<Dictionary<string, DataTable>> ReadAllTablesAsync(IProgress<string> progress = null);

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings asynchronously.
        /// Use this for compatibility scenarios.
        /// </summary>
        Task<Dictionary<string, DataTable>> ReadAllTablesAsStringsAsync(IProgress<string> progress = null);

        /// <summary>
        /// Creates a fluent query interface for the specified table.
        /// Supports both typed and string row access:
        /// <list type="bullet">
        ///   <item>Typed chain:  <c>Where(obj => ...)</c>          → <c>Execute()</c>          / <c>FirstOrDefault()</c>          / <c>Count()</c></item>
        ///   <item>String chain: <c>WhereAsStrings(str => ...)</c> → <c>ExecuteAsStrings()</c> / <c>FirstOrDefaultAsStrings()</c> / <c>CountAsStrings()</c></item>
        /// </list>
        /// </summary>
        TableQuery Query(string tableName);
    }
}
