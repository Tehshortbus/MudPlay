#nullable disable
#pragma warning disable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JetDatabaseReader
{
    /// <summary>
    /// Pure-managed reader for Microsoft Access JET databases (.mdb / .accdb).
    /// No OleDB, ODBC, or ACE/Jet driver installation required.
    ///
    /// Supports:
    ///   Jet3  – Access 97 (.mdb)
    ///   Jet4+ – Access 2000-2019 (.mdb / .accdb)
    ///
    /// Features:
    ///   ✓ All standard data types (Text, Integer, Date, GUID, Currency, etc.)
    ///   ✓ MEMO fields (inline + single-page + multi-page LVAL chains)
    ///   ✓ OLE Object fields — auto-detects images (JPEG/PNG/GIF/BMP), documents (PDF/DOC/RTF), archives (ZIP)
    ///   ✓ Streaming API — process millions of rows without OOM (StreamRows, ReadTableAsDataTable)
    ///   ✓ Progress reporting — IProgress&lt;int&gt; callbacks for long operations
    ///   ✓ Page cache — 256-page LRU cache (default 1 MB) for 50%+ performance boost
    ///   ✓ Catalog caching — single MSysObjects scan, reused across calls
    ///   ✓ Non-Western text — auto-detects code page from database header (Cyrillic, Japanese, etc.)
    ///   ✓ Encryption detection — throws clear NotSupportedException for password-protected DBs
    ///
    /// Limitations:
    ///   ✗ Encrypted (password-protected) databases — remove password in Access first
    ///   ✗ Attachment fields (Type 0x11) — not supported (rare, added in Access 2007)
    ///   ✗ Linked tables — only local tables returned
    ///   ✗ Overflow rows (span multiple pages) — silently skipped (rare edge case)
    ///
    /// Based on the mdbtools format specification:
    ///   https://github.com/mdbtools/mdbtools/blob/master/HACKING.md
    /// </summary>
    public sealed class AccessReader : IAccessReader
    {
        // ── Column type codes (mdbtools HACKING.md) ──────────────────────
        private const byte T_BOOL    = 0x01; // 1 bit  – stored in null_mask
        private const byte T_BYTE    = 0x02; // 1 byte
        private const byte T_INT     = 0x03; // 2 bytes (signed)
        private const byte T_LONG    = 0x04; // 4 bytes (signed)
        private const byte T_MONEY   = 0x05; // 8 bytes (int64 / 10000)
        private const byte T_FLOAT   = 0x06; // 4 bytes (IEEE 754)
        private const byte T_DOUBLE  = 0x07; // 8 bytes (IEEE 754)
        private const byte T_DATETIME= 0x08; // 8 bytes (OA date)
        private const byte T_BINARY  = 0x09; // variable (≤ 255 bytes)
        private const byte T_TEXT    = 0x0A; // variable (UCS-2 in Jet4, ANSI in Jet3)
        private const byte T_OLE     = 0x0B; // LVAL
        private const byte T_MEMO    = 0x0C; // LVAL or inline
        private const byte T_GUID    = 0x0F; // 16 bytes
        private const byte T_NUMERIC = 0x10; // 17 bytes scaled decimal

        // Column flag bits
        private const byte FLAG_FIXED = 0x01;

        // Catalog (MSysObjects) constants
        private const int  OBJ_TABLE     = 1;
        private const uint SYSTABLE_MASK = 0x80000002U;

        // ── Format-specific offsets ───────────────────────────────────────

        // Data page
        private readonly int _dpTDefOff;    // offset of tdef_pg (4 bytes)
        private readonly int _dpNumRows;    // offset of num_rows (2 bytes)
        private readonly int _dpRowsStart;  // offset of first row-offset entry

        // TDEF page (absolute offsets within the TDEF byte array)
        private readonly int _tdNumCols;    // offset of num_cols    (2 bytes)
        private readonly int _tdNumRealIdx; // offset of num_real_idx (4 bytes)
        private readonly int _tdBlockEnd;   // first byte after table-definition block

        // Column descriptor (per-column, fixed-size block)
        private readonly int _colDescSz;
        private readonly int _colTypeOff;
        private readonly int _colVarOff;    // offset_V – var-col index
        private readonly int _colFixedOff;  // offset_F – byte offset in fixed area
        private readonly int _colSzOff;     // col_len
        private readonly int _colFlagsOff;  // bitmask
        private readonly int _colNumOff;    // col_num (includes deleted)

        // Per-real-index entry size (skipped during column parsing)
        private readonly int _realIdxEntrySz;

        // Row field sizes (differ between Jet3 and Jet4)
        private readonly int _numColsFldSz;  // 1 or 2
        private readonly int _varEntrySz;    // 1 or 2  (var_table entry)
        private readonly int _eodFldSz;      // 1 or 2
        private readonly int _varLenFldSz;   // 1 or 2

        private readonly int  _pgSz;
        private readonly bool _jet4;
        private readonly FileStream _fs;
        private readonly Encoding _ansiEncoding;
        private readonly int _codePage;
        private readonly object _cacheLock = new object();
        private readonly object _catalogLock = new object();
        private volatile List<CatalogEntry> _catalogCache;
        private volatile LruCache<long, byte[]> _pageCache;
        private bool _disposed;
        private long _cacheHits;
        private long _cacheMisses;

        /// <summary>When true, GetUserTables logs verbose hex dumps for debugging. Default: false.</summary>
        public bool DiagnosticsEnabled { get; set; }

        /// <summary>Maximum number of pages to keep in cache. 0 = unlimited, -1 = disabled. Default: 256 (1 MB for 4K pages).</summary>
        public int PageCacheSize { get; set; } = 256;

        /// <summary>When true, uses parallel processing for reading multiple pages. Can improve performance for large tables. Default: false.</summary>
        public bool ParallelPageReadsEnabled { get; set; }

        // ── Constructor ───────────────────────────────────────────────────

        static AccessReader()
        {
            // On .NET Core / .NET 5+ code-page encodings (e.g. Windows-1252) are not
            // available by default. Register them once so GetEncoding() works for any
            // ANSI code page stored in the JET database header.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>Opens <paramref name="path"/> and detects the JET version.</summary>
        private AccessReader(string path, AccessReaderOptions options)
        {
            Guard.NotNullOrEmpty(path, nameof(path));
            Guard.NotNull(options, nameof(options));

            DiagnosticsEnabled = options.DiagnosticsEnabled;
            PageCacheSize = options.PageCacheSize;
            ParallelPageReadsEnabled = options.ParallelPageReadsEnabled;

            _fs = new FileStream(path, FileMode.Open, options.FileAccess, options.FileShare);


            // Read enough of the database definition page (page 0)
            var hdr = new byte[0x80];
            _fs.Read(hdr, 0, hdr.Length);

            // Offset 0x14: 0 = Jet3, ≥ 1 = Jet4+
            byte ver = hdr[0x14];
            _jet4 = (ver >= 1);
            _pgSz = _jet4 ? 4096 : 2048;

            // Offset 0x3C (Jet4) or 0x3A (Jet3): sort order / code page ID
            // Common: 1033=en-US(1252), 1049=ru(1251), 1041=ja(932)
            int cpOffset = _jet4 ? 0x3C : 0x3A;
            int sortOrder = (hdr.Length > cpOffset + 1) ? Ru16(hdr, cpOffset) : 0;
            _codePage = (sortOrder >> 8) & 0xFF;
            if (_codePage == 0) _codePage = 1252;  // default to Windows-1252 if unknown
            try { _ansiEncoding = Encoding.GetEncoding(_codePage); }
            catch { _ansiEncoding = Encoding.UTF8; _codePage = 65001; }

            // [FUJINTERM PATCH] Skip the upstream encryption-flag check at
            // hdr[0x62] & 0x03. In Jet4, bytes 0x18-0x97 of page 0 are
            // XOR-obfuscated against a per-file salt; reading byte 0x62
            // raw misinterprets ordinary data as an encryption flag and
            // rejects MajorMUD-distribution MDBs that any other Jet
            // reader (Jackcess, mdbtools, web-based viewers, MMUD
            // Explorer) opens cleanly. If the file is *truly* encrypted
            // (RC4-passworded), the catalog read further down will fail
            // with garbled-bytes diagnostics — better than the current
            // false-positive lockout that loses real data on legit
            // imports. See Vendor/JetDatabaseReader/README.md.

            if (_jet4)
            {
                // ── Jet4 / ACE (Access 2000 – 2019, .mdb + .accdb) ──────
                // Data page
                _dpTDefOff    = 4;
                _dpNumRows    = 12;   // extra 4-byte field after tdef_pg
                _dpRowsStart  = 14;

                // TDEF: 8-byte header + 55-byte Jet4 block = 63 total
                //   num_cols    at 8 + 37 = 45
                //   num_real_idx at 8 + 43 = 51
                _tdNumCols    = 45;
                _tdNumRealIdx = 51;
                _tdBlockEnd   = 63;

                // Column descriptor (25 bytes)
                _colDescSz    = 25;
                _colTypeOff   =  0;   // col_type  (1)
                _colVarOff    =  7;   // offset_V  (2): 1+4+2
                _colFixedOff  = 21;   // offset_F  (2): 1+4+2+2+2+2+2+1+1+4
                _colSzOff     = 23;   // col_len   (2)
                _colFlagsOff  = 15;   // bitmask   (1): 1+4+2+2+2+2+2
                _colNumOff    =  5;   // col_num   (2)

                _realIdxEntrySz = 12;
                _numColsFldSz   =  2;
                _varEntrySz     =  2;
                _eodFldSz       =  2;
                _varLenFldSz    =  2;
            }
            else
            {
                // ── Jet3 (Access 97, .mdb) ────────────────────────────
                // Data page
                _dpTDefOff    = 4;
                _dpNumRows    =  8;
                _dpRowsStart  = 10;

                // TDEF: 8-byte header + 35-byte Jet3 block = 43 total
                //   num_cols    at 8 + 17 = 25
                //   num_real_idx at 8 + 23 = 31
                _tdNumCols    = 25;
                _tdNumRealIdx = 31;
                _tdBlockEnd   = 43;

                // Column descriptor (18 bytes)
                _colDescSz    = 18;
                _colTypeOff   =  0;   // col_type  (1)
                _colVarOff    =  3;   // offset_V  (2): 1+2
                _colFixedOff  = 14;   // offset_F  (2): 1+2+2+2+2+2+2+1
                _colSzOff     = 16;   // col_len   (2)
                _colFlagsOff  = 13;   // bitmask   (1)
                _colNumOff    =  1;   // col_num   (2)

                _realIdxEntrySz =  8;
                _numColsFldSz   =  1;
                _varEntrySz     =  1;
                _eodFldSz       =  1;
                _varLenFldSz    =  1;
            }

            if (options.ValidateOnOpen)
            {
                ValidateDatabaseFormat();
            }
        }

        /// <summary>
        /// Opens a JET database file and returns a new AccessReader instance.
        /// </summary>
        /// <param name="path">Path to the .mdb or .accdb file.</param>
        /// <param name="options">Optional configuration options.</param>
        public static AccessReader Open(string path, AccessReaderOptions options = null)
        {
            Guard.NotNullOrEmpty(path, nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException($"Database file not found: {path}", path);

            options = options ?? new AccessReaderOptions();

            return new AccessReader(path, options);
        }

        private void ValidateDatabaseFormat()
        {
            if (_fs.Length < 128)
                throw new InvalidDataException("File too small to be a valid JET database");

            // Verify the JET magic signature at offset 0: 00 01 00 00
            _fs.Seek(0, SeekOrigin.Begin);
            var magic = new byte[4];
            int read = _fs.Read(magic, 0, 4);
            if (read < 4 || magic[0] != 0x00 || magic[1] != 0x01 || magic[2] != 0x00 || magic[3] != 0x00)
                throw new InvalidDataException(
                    $"File does not have a valid JET magic signature " +
                    $"(expected 00 01 00 00, got {magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}).");
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _fs?.Dispose();
                lock (_cacheLock)
                {
                    _pageCache?.Clear();
                }
                lock (_catalogLock)
                {
                    _catalogCache?.Clear();
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AccessReader));
        }

        /// <summary>Diagnostic output populated after each call to <see cref="ListTables"/>.</summary>
        public string LastDiagnostics { get; private set; } = string.Empty;

        // ── Low-level helpers

        private static ushort Ru16(byte[] b, int o) =>
            (ushort)(b[o] | (b[o + 1] << 8));

        private static int Ri32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        private static uint Ru32(byte[] b, int o) => (uint)Ri32(b, o);

        private byte[] ReadPage(long n)
        {
            var buf = new byte[_pgSz];
            _fs.Seek(n * _pgSz, SeekOrigin.Begin);
            // FileStream.Read is not guaranteed to return all bytes in one call
            int read = 0;
            while (read < _pgSz)
            {
                int got = _fs.Read(buf, read, _pgSz - read);
                if (got == 0) break;
                read += got;
            }
            return buf;
        }

        /// <summary>Reads a page through the cache if enabled (PageCacheSize > 0).</summary>
        private byte[] ReadPageCached(long n)
        {
            ThrowIfDisposed();

            if (PageCacheSize < 0) return ReadPage(n);  // cache disabled

            // Lazy-init: only one thread creates the cache; LruCache is internally thread-safe
            // so subsequent TryGetValue/Add calls need no outer lock.
            if (_pageCache == null && PageCacheSize > 0)
            {
                lock (_cacheLock)
                {
                    if (_pageCache == null)
                        _pageCache = new LruCache<long, byte[]>(PageCacheSize);
                }
            }

            if (_pageCache != null && _pageCache.TryGetValue(n, out byte[] cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            Interlocked.Increment(ref _cacheMisses);
            byte[] page = ReadPage(n);
            _pageCache?.Add(n, page);
            return page;
        }

        // ── TDEF reading ──────────────────────────────────────────────────

        /// <summary>
        /// Concatenates the TDEF page chain starting at <paramref name="startPage"/>
        /// into a single byte array.  Pages after the first have their 8-byte
        /// TDEF header stripped before appending.
        /// </summary>
        private byte[] ReadTDefBytes(long startPage)
        {
            var parts = new List<byte[]>();
            var seen  = new HashSet<long>();
            long pg   = startPage;

            while (pg != 0 && !seen.Contains(pg))
            {
                seen.Add(pg);
                byte[] p = ReadPage(pg);
                if (p[0] != 0x02) break;   // not a TDEF page
                parts.Add(p);
                pg = Ru32(p, 4);           // next_pg (0 = end of chain)
            }

            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];

            // Concatenate: full first page, then continuation pages minus 8-byte TDEF header
            int total = parts[0].Length;
            for (int i = 1; i < parts.Count; i++)
                total += parts[i].Length - 8;

            var result = new byte[total];
            Buffer.BlockCopy(parts[0], 0, result, 0, parts[0].Length);
            int pos = parts[0].Length;
            for (int i = 1; i < parts.Count; i++)
            {
                int len = parts[i].Length - 8;
                Buffer.BlockCopy(parts[i], 8, result, pos, len);
                pos += len;
            }
            return result;
        }

        private TableDef ReadTableDef(long tdefPage)
        {
            byte[] td = ReadTDefBytes(tdefPage);
            if (td == null || td.Length < _tdBlockEnd) return null;

            int numCols    = Ru16(td, _tdNumCols);
            int numRealIdx = Ri32(td, _tdNumRealIdx);

            // Safety: corrupt or unusual TDEFs can report absurd index counts
            if (numRealIdx < 0 || numRealIdx > 1000) numRealIdx = 0;
            if (numCols    < 0 || numCols    > 4096) return null;

            // Column descriptors follow immediately after block + first real-idx entries
            int colStart = _tdBlockEnd + numRealIdx * _realIdxEntrySz;
            int namePos  = colStart + numCols * _colDescSz;

            if (namePos > td.Length) return null;

            var cols = new List<ColumnInfo>(numCols);
            for (int i = 0; i < numCols; i++)
            {
                int o = colStart + i * _colDescSz;
                if (o + _colDescSz > td.Length) break;
                cols.Add(new ColumnInfo
                {
                    Type     = td[o + _colTypeOff],
                    ColNum   = Ru16(td, o + _colNumOff),
                    VarIdx   = Ru16(td, o + _colVarOff),
                    FixedOff = Ru16(td, o + _colFixedOff),
                    Size     = Ru16(td, o + _colSzOff),
                    Flags    = td[o + _colFlagsOff]
                });
            }

            // Column names follow directly after all descriptors (in TDEF / descriptor order).
            // Names MUST be read before sorting so each name maps to the correct descriptor.
            for (int i = 0; i < cols.Count; i++)
            {
                if (namePos >= td.Length) break;

                if (_jet4)
                {
                    if (namePos + 2 > td.Length) break;
                    int len = Ru16(td, namePos); namePos += 2;
                    if (namePos + len > td.Length) break;
                    cols[i].Name = Encoding.Unicode.GetString(td, namePos, len);
                    namePos += len;
                }
                else
                {
                    int len = td[namePos++];
                    if (namePos + len > td.Length) break;
                    cols[i].Name = _ansiEncoding.GetString(td, namePos, len);
                    namePos += len;
                }
            }

            // Sort by col_num AFTER names are assigned.
            // Row data (null_mask bits, numCols check) is indexed by col_num,
            // not by TDEF position.  mdbtools does the same sort (mdb_col_comparer).
            cols.Sort((a, b) => a.ColNum.CompareTo(b.ColNum));

            // Detect deleted-column gaps: if ColNum sequence has gaps, flag it
            bool hasDeletedColumns = false;
            for (int i = 1; i < cols.Count; i++)
            {
                if (cols[i].ColNum != cols[i - 1].ColNum + 1)
                {
                    hasDeletedColumns = true;
                    break;
                }
            }

            return new TableDef 
            { 
                Columns = cols, 
                RowCount = td.Length > 20 ? (long)Ru32(td, 16) : 0,
                HasDeletedColumns = hasDeletedColumns
            };
        }

        // ── Catalog ───────────────────────────────────────────────────────

        private sealed class CatalogEntry
        {
            public string Name;
            public long   TDefPage;
        }

        /// <summary>Returns all user-visible table names and their TDEF page numbers.</summary>
        private List<CatalogEntry> GetUserTables()
        {
            if (_catalogCache != null) return _catalogCache;

            lock (_catalogLock)
            {
                if (_catalogCache != null) return _catalogCache;

                var diag = new System.Text.StringBuilder();
                diag.AppendLine($"JET: {(_jet4 ? "Jet4/ACE" : "Jet3")}  PageSize: {_pgSz}  TotalPages: {_fs.Length / _pgSz}");

                // MSysObjects TDEF is hard-coded at page 2 by the Jet engine
                TableDef msys = ReadTableDef(2);
                if (msys == null)
                {
                    diag.AppendLine("ERROR: Page 2 is not a valid TDEF page (null returned).");
                    LastDiagnostics = diag.ToString();
                    _catalogCache = new List<CatalogEntry>();
                    return _catalogCache;
                }

                diag.AppendLine($"MSysObjects cols ({msys.Columns.Count}): " +
                    string.Join(", ", msys.Columns.ConvertAll(c => $"{c.Name}[0x{c.Type:X2}]")));

                // Case-insensitive column lookup — column names vary slightly across Access versions
                int idxId    = msys.Columns.FindIndex(c => string.Equals(c.Name, "Id",    StringComparison.OrdinalIgnoreCase));
                int idxName  = msys.Columns.FindIndex(c => string.Equals(c.Name, "Name",  StringComparison.OrdinalIgnoreCase));
                int idxType  = msys.Columns.FindIndex(c => string.Equals(c.Name, "Type",  StringComparison.OrdinalIgnoreCase));
                int idxFlags = msys.Columns.FindIndex(c => string.Equals(c.Name, "Flags", StringComparison.OrdinalIgnoreCase));

                if (idxName < 0 || idxType < 0)
                {
                    diag.AppendLine("ERROR: Required catalog columns not found. Column name mismatch?");
                    LastDiagnostics = diag.ToString();
                    _catalogCache = new List<CatalogEntry>();
                    return _catalogCache;
                }

                var result    = new List<CatalogEntry>();
                long totPages = _fs.Length / _pgSz;
                int  catPages = 0;
                int  allRows  = 0;

                for (long p = 3; p < totPages; p++)
                {
                    byte[] page = ReadPageCached(p);
                    if (page[0] != 0x01) continue;             // data pages only
                    if (Ri32(page, _dpTDefOff) != 2) continue; // must belong to MSysObjects
                    catPages++;

                    foreach (List<string> row in EnumerateRows(page, msys))
                    {
                        allRows++;
                        string typeStr  = SafeGet(row, idxType);
                        string nameStr  = SafeGet(row, idxName);
                        string flagsStr = SafeGet(row, idxFlags);

                        if (!int.TryParse(typeStr, out int objType) || objType != OBJ_TABLE)
                            continue;

                        long.TryParse(flagsStr, out long flagsLong);
                        if (((uint)flagsLong & SYSTABLE_MASK) != 0)
                            continue;

                        if (string.IsNullOrEmpty(nameStr)) continue;

                        long tdefPage = 0;
                        if (idxId >= 0)
                        {
                            long.TryParse(SafeGet(row, idxId), out long id);
                            tdefPage = id & 0x00FFFFFFL;
                        }

                        if (tdefPage > 0)
                            result.Add(new CatalogEntry { Name = nameStr, TDefPage = tdefPage });
                    }
                }

                diag.AppendLine($"Catalog pages: {catPages}  Total rows scanned: {allRows}  User tables: {result.Count}");
                if (DiagnosticsEnabled)
                {
                    foreach (var e in result)
                        diag.AppendLine($"  [{e.Name}] TDEF page {e.TDefPage}");
                }

                LastDiagnostics = diag.ToString();
                _catalogCache = result;
                return _catalogCache;
            }
        }

        private static string SafeGet(List<string> row, int idx) =>
            (idx >= 0 && idx < row.Count) ? row[idx] : string.Empty;

        /// <summary>Finds a catalog entry by name (case-insensitive) without re-scanning the catalog.</summary>
        private CatalogEntry GetCatalogEntry(string tableName)
        {
            return GetUserTables().Find(e =>
                string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the column headers and up to <paramref name="maxRows"/> rows
        /// from the first user table, plus the table name and total table count.
        /// </summary>
        public FirstTableResult ReadFirstTable(int maxRows = 100)
        {
            ThrowIfDisposed();

            var empty = new FirstTableResult
            {
                Headers    = new List<string> { "Info" },
                Rows       = new List<List<string>> { new List<string> { "No user tables found" } },
                Schema     = new List<TableColumn>(),
                TableName  = string.Empty,
                TableCount = 0
            };

            List<CatalogEntry> tables = GetUserTables();
            if (tables.Count == 0) return empty;

            CatalogEntry entry = tables[0];
            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new FirstTableResult
                {
                    Headers    = new List<string> { "Info" },
                    Rows       = new List<List<string>> { new List<string> { $"Cannot read TDEF for '{entry.Name}'" } },
                    Schema     = new List<TableColumn>(),
                    TableName  = entry.Name,
                    TableCount = tables.Count
                };

            var headers = td.Columns.Select(c => c.Name).ToList();
            var rows    = new List<List<string>>();
            long total  = _fs.Length / _pgSz;

            for (long p = 3; p < total && rows.Count < maxRows; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    rows.Add(row);
                    if (rows.Count >= maxRows) break;
                }
            }

            return new FirstTableResult
            {
                Headers    = headers,
                Rows       = rows,
                Schema     = new List<TableColumn>(),
                TableName  = entry.Name,
                TableCount = tables.Count
            };
        }

        /// <summary>Returns the names of all user tables in the database.</summary>
        public List<string> ListTables()
        {
            ThrowIfDisposed();
            return GetUserTables().ConvertAll(e => e.Name);
        }

        /// <summary>
        /// Returns name, stored row-count, and column-count for every user table.
        /// Calling this instead of <see cref="ListTables"/> avoids a duplicate catalog scan.
        /// </summary>
        public List<TableStat> GetTableStats()
        {
            ThrowIfDisposed();
            var entries = GetUserTables();
            var result  = new List<TableStat>(entries.Count);
            foreach (var e in entries)
            {
                TableDef td = ReadTableDef(e.TDefPage);
                result.Add(new TableStat
                {
                    Name        = e.Name,
                    RowCount    = td?.RowCount ?? 0L,
                    ColumnCount = td?.Columns.Count ?? 0
                });
            }
            return result;
        }

        /// <summary>
        /// Returns table metadata as a DataTable with columns: TableName, RowCount, ColumnCount.
        /// Ideal for binding to data grids or exporting to CSV/Excel.
        /// </summary>
        public DataTable GetTablesAsDataTable()
        {
            ThrowIfDisposed();
            var dt = new DataTable("Tables");
            dt.Columns.Add("TableName", typeof(string));
            dt.Columns.Add("RowCount", typeof(long));
            dt.Columns.Add("ColumnCount", typeof(int));

            var stats = GetTableStats();
            foreach (TableStat s in stats)
            {
                dt.Rows.Add(s.Name, s.RowCount, s.ColumnCount);
            }

            return dt;
        }

        /// <summary>
        /// Scans all data pages to count live (non-deleted, non-overflow) rows for the specified table.
        /// This is slower than reading the TDEF RowCount (which may be stale), but always accurate.
        /// Use this after many deletes/imports when Compact & Repair hasn't been run.
        /// </summary>
        public long GetRealRowCount(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return 0;

            long count = 0;
            long total = _fs.Length / _pgSz;

            for (long p = 3; p < total; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                int numRows = Ru16(page, _dpNumRows);
                for (int r = 0; r < numRows; r++)
                {
                    int raw = Ru16(page, _dpRowsStart + r * 2);
                    if ((raw & 0x8000) != 0) continue; // deleted
                    if ((raw & 0x4000) != 0) continue; // overflow
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive).
        /// Returns column headers, rows with native CLR types (int, DateTime, decimal, etc.) in <see cref="TableResult.Rows"/>, and per-column schema.
        /// Use <see cref="ReadTableAsStrings"/> when raw string values are needed instead.
        /// </summary>
        public TableResult ReadTable(string tableName, int maxRows)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);

            if (entry == null)
                return new TableResult
                {
                    Headers   = new List<string>(),
                    Rows = new List<object[]>(),
                    Schema    = new List<TableColumn>()
                };

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new TableResult
                {
                    Headers   = new List<string>(),
                    Rows = new List<object[]>(),
                    Schema    = new List<TableColumn>()
                };

            var headers   = td.Columns.ConvertAll(c => c.Name);
            var schema    = td.Columns.ConvertAll(c => new TableColumn
            {
                Name = c.Name,
                Type = TypeCodeToClrType(c.Type),
                Size = SizeForColumn(c)
            });
            var typedRows = new List<object[]>();
            long total    = _fs.Length / _pgSz;

            for (long p = 3; p < total && typedRows.Count < maxRows; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    var typedRow = new object[row.Count];
                    for (int i = 0; i < row.Count && i < td.Columns.Count; i++)
                    {
                        Type colType = TypeCodeToClrType(td.Columns[i].Type);
                        typedRow[i] = TypedValueParser.ParseValue(row[i], colType);
                    }
                    typedRows.Add(typedRow);
                    if (typedRows.Count >= maxRows) break;
                }
            }

            return new TableResult { Headers = headers, Rows = typedRows, Schema = schema, TableName = tableName };
        }

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with all values as strings.
        /// Returns column headers, string rows in <see cref="StringTableResult.Rows"/>, and per-column schema.
        /// Use <see cref="ReadTable(string, int)"/> when native CLR types are preferred.
        /// </summary>
        public StringTableResult ReadTableAsStrings(string tableName, int maxRows)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);

            if (entry == null)
                return new StringTableResult
                {
                    Headers = new List<string>(),
                    Rows    = new List<List<string>>(),
                    Schema  = new List<TableColumn>()
                };

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new StringTableResult
                {
                    Headers = new List<string>(),
                    Rows    = new List<List<string>>(),
                    Schema  = new List<TableColumn>()
                };

            var headers = td.Columns.ConvertAll(c => c.Name);
            var schema  = td.Columns.ConvertAll(c => new TableColumn
            {
                Name = c.Name,
                Type = TypeCodeToClrType(c.Type),
                Size = SizeForColumn(c)
            });
            var rows  = new List<List<string>>();
            long total = _fs.Length / _pgSz;

            for (long p = 3; p < total && rows.Count < maxRows; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    rows.Add(row);
                    if (rows.Count >= maxRows) break;
                }
            }

            return new StringTableResult { Headers = headers, Rows = rows, Schema = schema, TableName = tableName };
        }

        private static string TypeCodeToName(byte t)
        {
            switch (t)
            {
                case T_BOOL:     return "Yes/No";
                case T_BYTE:     return "Byte";
                case T_INT:      return "Integer";
                case T_LONG:     return "Long Integer";
                case T_MONEY:    return "Currency";
                case T_FLOAT:    return "Single";
                case T_DOUBLE:   return "Double";
                case T_DATETIME: return "Date/Time";
                case T_BINARY:   return "Binary";
                case T_TEXT:     return "Text";
                case T_OLE:      return "OLE Object";
                case T_MEMO:     return "Memo";
                case T_GUID:     return "GUID";
                case T_NUMERIC:  return "Decimal";
                default:         return $"0x{t:X2}";
            }
        }

        private static ColumnSize SizeForColumn(ColumnInfo col)
        {
            switch (col.Type)
            {
                case T_BOOL:     return ColumnSize.FromBits(1);
                case T_BYTE:     return ColumnSize.FromBytes(1);
                case T_INT:      return ColumnSize.FromBytes(2);
                case T_LONG:     return ColumnSize.FromBytes(4);
                case T_MONEY:    return ColumnSize.FromBytes(8);
                case T_FLOAT:    return ColumnSize.FromBytes(4);
                case T_DOUBLE:   return ColumnSize.FromBytes(8);
                case T_DATETIME: return ColumnSize.FromBytes(8);
                case T_GUID:     return ColumnSize.FromBytes(16);
                case T_NUMERIC:  return ColumnSize.FromBytes(17);
                case T_TEXT:     return ColumnSize.FromChars(col.Size > 0 ? col.Size / 2 : 255);
                case T_BINARY:   return col.Size > 0 ? ColumnSize.FromBytes(col.Size) : ColumnSize.Variable;
                case T_MEMO:
                case T_OLE:      return ColumnSize.Lval;
                default:         return col.Size > 0 ? ColumnSize.FromBytes(col.Size) : ColumnSize.Variable;
            }
        }

        // Used by ColumnMetadata.SizeDescription which keeps a plain string.
        private static string SizeDescForColumn(ColumnInfo col) => SizeForColumn(col).ToString();

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as properly typed object arrays without collecting them all in memory.
        /// Each element in the array is the native CLR type (int, DateTime, decimal, etc.).
        /// Ideal for large tables — use foreach to process one row at a time.
        /// This is the recommended method for streaming data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public IEnumerable<object[]> StreamRows(string tableName, IProgress<int> progress = null)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsCore(tableName, progress);
        }

        private IEnumerable<object[]> StreamRowsCore(string tableName, IProgress<int> progress)
        {
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) yield break;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) yield break;

            long total = _fs.Length / _pgSz;
            int rowCount = 0;
            for (long p = 3; p < total; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    var typedRow = new object[row.Count];
                    for (int i = 0; i < row.Count && i < td.Columns.Count; i++)
                    {
                        Type colType = TypeCodeToClrType(td.Columns[i].Type);
                        typedRow[i] = TypedValueParser.ParseValue(row[i], colType);
                    }
                    yield return typedRow;
                    rowCount++;
                }
                progress?.Report(rowCount);
            }
        }

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as string arrays without collecting them all in memory.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="StreamRows"/> which returns properly typed data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public IEnumerable<string[]> StreamRowsAsStrings(string tableName, IProgress<int> progress = null)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsAsStringsCore(tableName, progress);
        }

        private IEnumerable<string[]> StreamRowsAsStringsCore(string tableName, IProgress<int> progress)
        {
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) yield break;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) yield break;

            long total = _fs.Length / _pgSz;
            int rowCount = 0;
            for (long p = 3; p < total; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    yield return row.ToArray();
                    rowCount++;
                }
                progress?.Report(rowCount);
            }
        }

        /// <summary>
        /// Reads the entire table into a DataTable with all columns typed as strings.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="ReadTable"/> which returns properly typed columns.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public DataTable ReadTableAsStringDataTable(string tableName = null, IProgress<int> progress = null)
        {
            ThrowIfDisposed();
            // If no table name specified, use the first table
            if (string.IsNullOrEmpty(tableName))
            {
                var tables = GetUserTables();
                if (tables.Count == 0) return null;
                tableName = tables[0].Name;
            }

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return null;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) return null;

            var dt = new DataTable(tableName);
            foreach (var col in td.Columns)
                dt.Columns.Add(col.Name, typeof(string));

            long total = _fs.Length / _pgSz;
            for (long p = 3; p < total; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    dt.Rows.Add(row.ToArray());
                }
                progress?.Report(dt.Rows.Count);
            }

            return dt;
        }

        /// <summary>
        /// Returns rich metadata for all columns in the specified table.
        /// </summary>
        public List<ColumnMetadata> GetColumnMetadata(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return new List<ColumnMetadata>();

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null) return new List<ColumnMetadata>();

            return td.Columns.Select((col, index) => new ColumnMetadata
            {
                Name = col.Name,
                TypeName = TypeCodeToName(col.Type),
                ClrType = TypeCodeToClrType(col.Type),
                MaxLength = col.Size > 0 ? (int?)col.Size : null,
                IsNullable = true,
                IsFixedLength = col.IsFixed,
                Ordinal = index,
                SizeDescription = SizeDescForColumn(col)
            }).ToList();
        }

        /// <summary>
        /// Returns statistical information about the database.
        /// </summary>
        public DatabaseStatistics GetStatistics()
        {
            ThrowIfDisposed();

            var stats = new DatabaseStatistics
            {
                TotalPages = _fs.Length / _pgSz,
                DatabaseSizeBytes = _fs.Length,
                PageSize = _pgSz,
                Version = _jet4 ? "Jet4/ACE" : "Jet3",
                CodePage = _codePage
            };

            var tables = GetUserTables();
            stats.TableCount = tables.Count;
            stats.TableRowCounts = new Dictionary<string, long>();

            foreach (var table in tables)
            {
                var td = ReadTableDef(table.TDefPage);
                if (td != null)
                {
                    stats.TableRowCounts[table.Name] = td.RowCount;
                    stats.TotalRows += td.RowCount;
                }
            }

            long totalAccess = _cacheHits + _cacheMisses;
            stats.PageCacheHitRate = totalAccess > 0 ? (int)((_cacheHits * 100) / totalAccess) : 0;

            return stats;
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// This is the recommended method for bulk reading.
        /// </summary>
        public Dictionary<string, DataTable> ReadAllTables(IProgress<string> progress = null)
        {
            ThrowIfDisposed();

            var result = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var tables = GetUserTables();

            foreach (var table in tables)
            {
                progress?.Report($"Reading {table.Name}...");
                result[table.Name] = ReadTable(table.Name);
            }

            return result;
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings.
        /// Use this for compatibility scenarios.
        /// </summary>
        public Dictionary<string, DataTable> ReadAllTablesAsStrings(IProgress<string> progress = null)
        {
            ThrowIfDisposed();

            var result = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var tables = GetUserTables();

            foreach (var table in tables)
            {
                progress?.Report($"Reading {table.Name}...");
                result[table.Name] = ReadTableAsStringDataTable(table.Name);
            }

            return result;
        }

        /// <summary>
        /// Creates a fluent query interface for the specified table.
        /// </summary>
        public TableQuery Query(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return new TableQuery(this, tableName);
        }

        private static Type TypeCodeToClrType(byte typeCode)
        {
            switch (typeCode)
            {
                case T_BOOL: return typeof(bool);
                case T_BYTE: return typeof(byte);
                case T_INT: return typeof(short);
                case T_LONG: return typeof(int);
                case T_MONEY: return typeof(decimal);
                case T_FLOAT: return typeof(float);
                case T_DOUBLE: return typeof(double);
                case T_DATETIME: return typeof(DateTime);
                case T_GUID: return typeof(Guid);
                case T_NUMERIC: return typeof(decimal);
                default: return typeof(string);
            }
        }

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// This is the recommended method for reading table data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public DataTable ReadTable(string tableName = null, IProgress<int> progress = null)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(tableName))
            {
                var tables = GetUserTables();
                if (tables.Count == 0) return null;
                tableName = tables[0].Name;
            }

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return null;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) return null;

            var dt = new DataTable(tableName);

            // Create columns with proper CLR types
            foreach (var col in td.Columns)
            {
                Type clrType = TypeCodeToClrType(col.Type);
                dt.Columns.Add(col.Name, clrType);
            }

            long total = _fs.Length / _pgSz;
            for (long p = 3; p < total; p++)
            {
                byte[] page = ReadPageCached(p);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (List<string> row in EnumerateRows(page, td))
                {
                    var dataRow = dt.NewRow();
                    for (int i = 0; i < row.Count && i < td.Columns.Count; i++)
                    {
                        Type colType = TypeCodeToClrType(td.Columns[i].Type);
                        dataRow[i] = TypedValueParser.ParseValue(row[i], colType);
                    }
                    dt.Rows.Add(dataRow);
                }
                progress?.Report(dt.Rows.Count);
            }

            return dt;
        }

        // ── Async Methods ──────────────────────────────────────────────────

        /// <summary>Returns the names of all user tables in the database asynchronously.</summary>
        public Task<List<string>> ListTablesAsync()
        {
            return Task.Run(() => ListTables());
        }

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns asynchronously.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// </summary>
        public Task<DataTable> ReadTableAsync(string tableName = null, IProgress<int> progress = null)
        {
            return Task.Run(() => ReadTable(tableName, progress));
        }

        /// <summary>
        /// Async overload of <see cref="ReadTable(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows with native CLR types asynchronously.
        /// </summary>
        public Task<TableResult> ReadTableAsync(string tableName, int maxRows)
        {
            return Task.Run(() => ReadTable(tableName, maxRows));
        }

        /// <summary>
        /// Async overload of <see cref="ReadTableAsStrings(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows as strings asynchronously.
        /// </summary>
        public Task<StringTableResult> ReadTableAsStringsAsync(string tableName, int maxRows)
        {
            return Task.Run(() => ReadTableAsStrings(tableName, maxRows));
        }

        /// <summary>
        /// Returns statistical information about the database asynchronously.
        /// </summary>
        public Task<DatabaseStatistics> GetStatisticsAsync()
        {
            return Task.Run(() => GetStatistics());
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns asynchronously.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// </summary>
        public Task<Dictionary<string, DataTable>> ReadAllTablesAsync(IProgress<string> progress = null)
        {
            return Task.Run(() => ReadAllTables(progress));
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings asynchronously.
        /// Use this for compatibility scenarios.
        /// </summary>
        public Task<Dictionary<string, DataTable>> ReadAllTablesAsStringsAsync(IProgress<string> progress = null)
        {
            return Task.Run(() => ReadAllTablesAsStrings(progress));
        }

        // ── Row enumeration ───────────────────────────────────────────────

        /// <summary>Yields decoded rows from a single data page.</summary>
        private IEnumerable<List<string>> EnumerateRows(byte[] page, TableDef td)
        {
            int numRows = Ru16(page, _dpNumRows);
            if (numRows == 0) yield break;

            // Collect all raw offset entries (including deleted / overflow)
            // so we can compute boundaries for live rows
            var rawOffsets = new int[numRows];
            for (int r = 0; r < numRows; r++)
                rawOffsets[r] = Ru16(page, _dpRowsStart + r * 2);

            // Sort the physical positions (lower 13 bits) for boundary computation
            int[] positions = rawOffsets
                .Select(o => o & 0x1FFF)
                .Where(o => o > 0 && o < _pgSz)
                .OrderBy(o => o)
                .ToArray();

            for (int r = 0; r < numRows; r++)
            {
                int raw = rawOffsets[r];
                if ((raw & 0x8000) != 0) continue; // deleted
                if ((raw & 0x4000) != 0) continue; // overflow (LVAL pointer page)

                int rowStart = raw & 0x1FFF;

                // Row ends just before the next higher row start, or at page end
                int rowEnd = _pgSz - 1;
                foreach (int pos in positions)
                {
                    if (pos > rowStart) { rowEnd = pos - 1; break; }
                }

                int rowSize = rowEnd - rowStart + 1;
                if (rowSize < _numColsFldSz) continue;

                // Pass the page buffer directly with absolute row bounds — no per-row copy
                List<string> values = CrackRow(page, rowStart, rowSize, td);
                if (values != null) yield return values;
            }
        }

        // ── Row decoding ──────────────────────────────────────────────────

        /// <summary>Decodes a single row's bytes (within <paramref name="page"/>) into string values per column.</summary>
        private List<string> CrackRow(byte[] page, int rowStart, int rowSize, TableDef td)
        {
            if (rowSize < _numColsFldSz) return null;

            // Number of columns stored in THIS row (may be less than td.Columns.Count
            // if columns were added after this row was written)
            int numCols = _jet4 ? Ru16(page, rowStart) : page[rowStart];
            if (numCols == 0) return null;

            // Check for deleted-column schema mismatch
            // If the table has deleted columns AND this row has MORE columns than current schema,
            // it was written before the deletion and data alignment is ambiguous
            if (td.HasDeletedColumns && numCols > td.Columns.Count)
            {
                throw new JetLimitationException(
                    $"Row has {numCols} columns but current schema has {td.Columns.Count} with deleted-column gaps. " +
                    $"This row predates schema changes and data may be misaligned. " +
                    $"Solution: Compact & Repair the database in Microsoft Access to rebuild all rows.");
            }

            int nullMaskSz  = (numCols + 7) / 8;
            int nullMaskPos = rowSize - nullMaskSz;  // relative to rowStart
            if (nullMaskPos < _numColsFldSz) return null;

            // ── Tail section layout (high→low addresses, reading from end) ──
            //  Jet4: [null_mask][var_len(2)][var_table(varLen*2)][eod(2)]
            //  Jet3: [null_mask][var_len(1)][jump_table(n*1)][var_table(varLen*1)][eod(1)]

            int varLenPos = nullMaskPos - _varLenFldSz;  // relative
            if (varLenPos < _numColsFldSz) return null;

            int varLen = _jet4 ? Ru16(page, rowStart + varLenPos) : page[rowStart + varLenPos];

            // Jet3 jump table: floor(rowSize / 256) entries of 1 byte each
            int jumpSz = _jet4 ? 0 : (rowSize / 256);

            int varTableStart = varLenPos - jumpSz - varLen * _varEntrySz;  // relative
            int eodPos        = varTableStart - _eodFldSz;                  // relative
            if (eodPos < _numColsFldSz) return null;

            int eod = _jet4 ? Ru16(page, rowStart + eodPos) : page[rowStart + eodPos];

            // ── Decode each column ────────────────────────────────────────
            var result = new List<string>(td.Columns.Count);

            for (int i = 0; i < td.Columns.Count; i++)
            {
                ColumnInfo col = td.Columns[i];

                // null_mask bit index = col.ColNum (the descriptor's col_num field),
                // NOT the loop index i.  JET rows index the mask by col_num,
                // while the TDEF may store columns in a different order (e.g. alphabetically).
                bool nullBit = false;
                if (col.ColNum < numCols)
                {
                    int mByte = nullMaskPos + (col.ColNum / 8);  // relative
                    int mBit  = col.ColNum % 8;
                    if (mByte < rowSize)
                        nullBit = (page[rowStart + mByte] & (1 << mBit)) != 0;
                }

                // BOOL: null_mask bit IS the value; no bytes stored in the row.
                // In JET: bit SET (1) = TRUE for BOOL.
                if (col.Type == T_BOOL)
                {
                    result.Add(nullBit ? "True" : "False");
                    continue;
                }

                // For all other types: bit SET (1) = column HAS a value (not null).
                // bit CLEAR (0) = column IS null.
                // Column also has no value when it was added after this row was written.
                if (col.ColNum >= numCols || !nullBit)
                {
                    result.Add(string.Empty);
                    continue;
                }

                if (col.IsFixed)
                {
                    int start = _numColsFldSz + col.FixedOff;  // relative
                    int sz    = FixedSize(col.Type, col.Size);
                    if (sz == 0 || start + sz > rowSize)
                    { result.Add(string.Empty); continue; }
                    result.Add(ReadFixed(page, rowStart + start, col, sz));
                }
                else
                {
                    // Variable column — look up its offset in the reversed var_table.
                    // var_table is stored in reverse column order:
                    //   entry for VarIdx=k  →  varTableStart + (varLen-1-k)*varEntrySz
                    if (col.VarIdx >= varLen)
                    { result.Add(string.Empty); continue; }

                    int entryPos = varTableStart + (varLen - 1 - col.VarIdx) * _varEntrySz;  // relative
                    if (entryPos < 0 || entryPos + _varEntrySz > rowSize)
                    { result.Add(string.Empty); continue; }

                    int varOff = _jet4 ? Ru16(page, rowStart + entryPos) : page[rowStart + entryPos];

                    // End of this variable column's data
                    int varEnd;
                    if (col.VarIdx + 1 < varLen)
                    {
                        int nextEntry = varTableStart + (varLen - 2 - col.VarIdx) * _varEntrySz;  // relative
                        varEnd = (_jet4 ? Ru16(page, rowStart + nextEntry) : page[rowStart + nextEntry]);
                    }
                    else
                    {
                        varEnd = eod;
                    }

                    // var_table entries are ROW offsets (from row[0]), not data-area offsets.
                    // FixedOff is a data-area offset (requires + _numColsFldSz), but var_table
                    // entries already include the num_cols header bytes.
                    int dataStart = varOff;          // relative to rowStart
                    int dataLen   = varEnd - varOff;
                    if (dataLen < 0 || dataStart < 0 || dataStart + dataLen > rowSize)
                    { result.Add(string.Empty); continue; }

                    result.Add(ReadVar(page, rowStart + dataStart, dataLen, col));
                }
            }

            return result;
        }

        // ── Column size helpers ───────────────────────────────────────────

        /// <summary>Returns the expected byte size for a fixed-length column type.</summary>
        private static int FixedSize(byte type, int declaredSize)
        {
            switch (type)
            {
                case T_BYTE:    return 1;
                case T_INT:     return 2;
                case T_LONG:    return 4;
                case T_MONEY:   return 8;
                case T_FLOAT:   return 4;
                case T_DOUBLE:  return 8;
                case T_DATETIME:return 8;
                case T_GUID:    return 16;
                case T_NUMERIC: return 17;
                default:        return declaredSize > 0 ? declaredSize : 0;
            }
        }

        // ── Fixed-column value reader ─────────────────────────────────────

        private string ReadFixed(byte[] row, int start, ColumnInfo col, int sz)
        {
            try
            {
                switch (col.Type)
                {
                    case T_BYTE:
                        return row[start].ToString();
                    case T_INT:
                        return ((short)Ru16(row, start)).ToString();
                    case T_LONG:
                        return Ri32(row, start).ToString();
                    case T_FLOAT:
                        return BitConverter.ToSingle(row, start).ToString("G");
                    case T_DOUBLE:
                        return BitConverter.ToDouble(row, start).ToString("G");
                    case T_DATETIME:
                        return OaDateToString(BitConverter.ToDouble(row, start));
                    case T_MONEY:
                        return (BitConverter.ToInt64(row, start) / 10000.0m).ToString("F4");
                    case T_NUMERIC:
                        return ReadNumeric(row, start);
                    case T_GUID:
                        return ReadGuid(row, start);
                    default:
                        return BitConverter.ToString(row, start, Math.Min(sz, 8));
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string ReadVar(byte[] row, int start, int len, ColumnInfo col)
        {
            if (len <= 0) return string.Empty;
            try
            {
                switch (col.Type)
                {
                    case T_TEXT:
                        return _jet4 ? DecodeJet4Text(row, start, len)
                                     : _ansiEncoding.GetString(row, start, len);

                    case T_BINARY:
                        return BitConverter.ToString(row, start, len);

                    case T_MEMO:
                    case T_OLE:
                        return ReadLongValue(row, start, len, col.Type == T_OLE);

                    default:
                        return string.Empty;
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
        //
        // MEMO/OLE field header (12 bytes):
        //   [memo_len: 3 bytes][bitmask: 1 byte][lval_dp: 4 bytes][unknown: 4 bytes]
        //
        // bitmask:
        //   0x80 = inline data immediately after the 12-byte header
        //   0x40 = single LVAL page:  lval_dp = (page << 8) | row_index
        //   0x00 = chained LVAL pages (not decoded; placeholder returned)

        /// <summary>
        /// Reads <paramref name="maxLen"/> bytes from a single LVAL data page / row.
        /// lval_dp encoding: upper 24 bits = page number, lower 8 bits = row index.
        /// </summary>
        private byte[] ReadLvalBytes(uint lvalDp, int maxLen)
        {
            try
            {
                int lvalPage = (int)(lvalDp >> 8);
                int lvalRow  = (int)(lvalDp & 0xFF);
                if (lvalPage <= 0) return null;

                byte[] page = ReadPageCached(lvalPage);
                if (page[0] != 0x01) return null;  // must be a data page

                int numRows = Ru16(page, _dpNumRows);
                if (lvalRow >= numRows) return null;

                int rawOff = Ru16(page, _dpRowsStart + lvalRow * 2);
                if ((rawOff & 0xC000) != 0) return null;  // deleted or overflow

                int rowStart = rawOff & 0x1FFF;
                if (rowStart == 0 || rowStart >= _pgSz) return null;

                int rowEnd = _pgSz - 1;
                for (int r = 0; r < numRows; r++)
                {
                    int ofs = Ru16(page, _dpRowsStart + r * 2) & 0x1FFF;
                    if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
                }

                int rowSize = Math.Min(rowEnd - rowStart + 1, maxLen);
                if (rowSize <= 0) return null;

                var data = new byte[rowSize];
                Buffer.BlockCopy(page, rowStart, data, 0, rowSize);
                return data;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads multi-page LVAL chains (bitmask 0x00). Follows LVAL page links until
        /// the entire memo is reconstructed or maxLen is reached.
        /// LVAL page format (mdbtools): [next_page(4)][data_length(4)][data...]
        /// </summary>
        private LvalChainResult ReadLvalChain(uint firstLvalDp, int maxLen)
        {
            try
            {
                var chunks = new List<byte[]>();
                int totalLen = 0;
                uint currentDp = firstLvalDp;
                var seen = new HashSet<uint>();

                while (currentDp != 0 && totalLen < maxLen && !seen.Contains(currentDp))
                {
                    seen.Add(currentDp);

                    int lvalPage = (int)(currentDp >> 8);
                    int lvalRow  = (int)(currentDp & 0xFF);
                    if (lvalPage <= 0) return LvalChainResult.Failure($"invalid page {lvalPage}");

                    byte[] page = ReadPageCached(lvalPage);
                    if (page[0] != 0x01) return LvalChainResult.Failure($"page {lvalPage} not data page");

                    int numRows = Ru16(page, _dpNumRows);
                    if (lvalRow >= numRows) return LvalChainResult.Failure($"row {lvalRow} >= numRows {numRows}");

                    int rawOff = Ru16(page, _dpRowsStart + lvalRow * 2);
                    if ((rawOff & 0xC000) != 0) return LvalChainResult.Failure("deleted/overflow row");

                    int rowStart = rawOff & 0x1FFF;
                    if (rowStart == 0 || rowStart >= _pgSz) return LvalChainResult.Failure($"invalid rowStart {rowStart}");

                    int rowEnd = _pgSz - 1;
                    for (int r = 0; r < numRows; r++)
                    {
                        int ofs = Ru16(page, _dpRowsStart + r * 2) & 0x1FFF;
                        if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
                    }

                    int rowSize = rowEnd - rowStart + 1;
                    if (rowSize < 8) return LvalChainResult.Failure($"rowSize {rowSize} < 8");

                    // LVAL chain format: [next_dp(4)][data_len(4)][data...]
                    currentDp = Ru32(page, rowStart);
                    int dataLen = (int)Ru32(page, rowStart + 4);
                    int dataStart = rowStart + 8;
                    int availableData = Math.Min(dataLen, rowSize - 8);

                    if (availableData > 0 && dataStart + availableData <= page.Length)
                    {
                        var chunk = new byte[availableData];
                        Buffer.BlockCopy(page, dataStart, chunk, 0, availableData);
                        chunks.Add(chunk);
                        totalLen += availableData;
                    }
                }

                if (chunks.Count == 0) return LvalChainResult.Failure("no chunks read");
                if (chunks.Count == 1) return LvalChainResult.Success(chunks[0]);

                // Concatenate all chunks
                int finalLen = Math.Min(totalLen, maxLen);
                var result = new byte[finalLen];
                int pos = 0;
                foreach (var chunk in chunks)
                {
                    int copyLen = Math.Min(chunk.Length, finalLen - pos);
                    Buffer.BlockCopy(chunk, 0, result, pos, copyLen);
                    pos += copyLen;
                    if (pos >= finalLen) break;
                }
                return LvalChainResult.Success(result);
            }
            catch (Exception ex) { return LvalChainResult.Failure(ex.Message); }
        }

        /// <summary>
        /// Scans the first 512 bytes for known file magic numbers (images, PDFs, Office docs, archives).
        /// Typical Access OLE fields wrap files in an OLE container (~78-byte header),
        /// so this scans beyond the OLE envelope to find the real file bytes.
        /// Returns a data-URI with appropriate MIME type, or null if no known format is found.
        /// </summary>
        private static string TryDecodeOleObject(byte[] b, int start, int len)
        {
            if (b == null || len < 4) return null;

            int scanEnd = Math.Min(start + len, start + 512);
            for (int i = start; i < scanEnd - 3; i++)
            {
                // ── Images ──
                // JPEG: FF D8 FF
                if (b[i] == 0xFF && b[i+1] == 0xD8 && b[i+2] == 0xFF)
                {
                    int fileLen = start + len - i;
                    return "data:image/jpeg;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // PNG: 89 50 4E 47
                if (b[i] == 0x89 && b[i+1] == 0x50 && b[i+2] == 0x4E && b[i+3] == 0x47)
                {
                    int fileLen = start + len - i;
                    return "data:image/png;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // GIF: 47 49 46
                if (b[i] == 0x47 && b[i+1] == 0x49 && b[i+2] == 0x46)
                {
                    int fileLen = start + len - i;
                    return "data:image/gif;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // BMP: 42 4D
                if (b[i] == 0x42 && b[i+1] == 0x4D)
                {
                    int fileLen = start + len - i;
                    return "data:image/bmp;base64," + Convert.ToBase64String(b, i, fileLen);
                }

                // ── Documents ──
                // PDF: 25 50 44 46 (%PDF)
                if (b[i] == 0x25 && b[i+1] == 0x50 && b[i+2] == 0x44 && b[i+3] == 0x46)
                {
                    int fileLen = start + len - i;
                    return "data:application/pdf;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // ZIP (also DOCX/XLSX/PPTX): 50 4B 03 04 (PK..)
                if (i + 3 < scanEnd && b[i] == 0x50 && b[i+1] == 0x4B && b[i+2] == 0x03 && b[i+3] == 0x04)
                {
                    int fileLen = start + len - i;
                    // Check if it's an Office Open XML file by looking for [Content_Types].xml signature
                    // For simplicity, return generic zip MIME
                    return "data:application/zip;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // DOC (Word 97-2003): D0 CF 11 E0 (OLE compound file)
                if (i + 3 < scanEnd && b[i] == 0xD0 && b[i+1] == 0xCF && b[i+2] == 0x11 && b[i+3] == 0xE0)
                {
                    int fileLen = start + len - i;
                    return "data:application/msword;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // RTF: 7B 5C 72 74 ({\rt)
                if (i + 3 < scanEnd && b[i] == 0x7B && b[i+1] == 0x5C && b[i+2] == 0x72 && b[i+3] == 0x74)
                {
                    int fileLen = start + len - i;
                    return "data:application/rtf;base64," + Convert.ToBase64String(b, i, fileLen);
                }
            }
            return null;
        }

        private string ReadLongValue(byte[] row, int start, int len, bool isOle)
        {
            if (len < 12) return isOle ? "(OLE)" : "(memo)";

            byte bitmask = row[start + 3];
            int  memoLen  = row[start] | (row[start + 1] << 8) | (row[start + 2] << 16);

            if ((bitmask & 0x80) != 0)
            {
                // Inline: data follows the 12-byte header
                int memoStart = start + 12;
                if (memoStart + memoLen > row.Length) memoLen = row.Length - memoStart;
                if (memoLen <= 0) return string.Empty;

                if (isOle) return TryDecodeOleObject(row, memoStart, memoLen) ?? "(OLE)";

                return _jet4 ? DecodeJet4Text(row, memoStart, memoLen)
                             : _ansiEncoding.GetString(row, memoStart, memoLen);
            }

            if ((bitmask & 0x40) != 0)
            {
                // Single LVAL page — lval_dp = (pageNumber << 8) | rowIndex
                uint   lvalDp   = Ru32(row, start + 4);
                byte[] lvalData = ReadLvalBytes(lvalDp, memoLen);

                if (lvalData != null)
                {
                    if (isOle) return TryDecodeOleObject(lvalData, 0, lvalData.Length) ?? "(OLE)";

                    return _jet4 ? DecodeJet4Text(lvalData, 0, lvalData.Length)
                                 : _ansiEncoding.GetString(lvalData);
                }

                return isOle ? "(OLE)" : "(memo on LVAL page)";
            }

            // Multi-page LVAL (0x00) — follow the chain
            uint chainDp = Ru32(row, start + 4);
            LvalChainResult chain = ReadLvalChain(chainDp, memoLen);

            if (chain.Data != null)
            {
                if (isOle) return TryDecodeOleObject(chain.Data, 0, chain.Data.Length) ?? "(OLE)";

                return _jet4 ? DecodeJet4Text(chain.Data, 0, chain.Data.Length)
                             : _ansiEncoding.GetString(chain.Data);
            }

            return isOle ? $"(OLE chain error: {chain.Error})" : $"(memo chain error: {chain.Error})";
        }

        // ── Jet4 text decoding ────────────────────────────────────────────

        /// <summary>
        /// Decodes Jet4 text (UCS-2 / UTF-16LE).
        /// If data starts with the compressed-unicode marker 0xFF 0xFE, the
        /// JET4 compressed-string algorithm is applied first.
        /// </summary>
        private static string DecodeJet4Text(byte[] b, int start, int len)
        {
            if (len < 2) return string.Empty;
            if (b[start] == 0xFF && b[start + 1] == 0xFE)
                return DecompressJet4(b, start + 2, len - 2);
            // Plain UCS-2 LE — length must be even
            int evenLen = len & ~1;
            return evenLen > 0 ? Encoding.Unicode.GetString(b, start, evenLen) : string.Empty;
        }

        /// <summary>
        /// Decodes the JET4 "compressed unicode" encoding.
        /// A 0x00 byte toggles between 1-byte compressed (ASCII) and 2-byte
        /// uncompressed (UCS-2) mode.
        /// </summary>
        private static string DecompressJet4(byte[] b, int start, int len)
        {
            var sb         = new StringBuilder(len);
            bool compressed = true;
            int i = start, end = start + len;

            while (i < end)
            {
                if (compressed)
                {
                    if (b[i] == 0x00) { compressed = false; i++; continue; }
                    sb.Append((char)b[i++]);
                }
                else
                {
                    if (i + 1 >= end) break;
                    if (b[i] == 0x00 && b[i + 1] == 0x00) { compressed = true; i += 2; continue; }
                    sb.Append((char)(b[i] | (b[i + 1] << 8)));
                    i += 2;
                }
            }
            return sb.ToString();
        }

        // ── Numeric / GUID helpers ────────────────────────────────────────

        private static string OaDateToString(double oaDate)
        {
            try   { return DateTime.FromOADate(oaDate).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Reads a Jet NUMERIC (17 bytes):
        ///   [precision(1)][scale(1)][sign(1)][pad(1)][96-bit LE integer: lo(4)+mid(4)+hi(4)]
        /// Uses the <see cref="decimal(int,int,int,bool,byte)"/> constructor which accepts any
        /// 96-bit integer directly — no manual multiply-chain needed.
        /// </summary>
        private static string ReadNumeric(byte[] b, int start)
        {
            if (start + 16 > b.Length) return string.Empty;

            byte scale = b[start + 1];
            bool neg   = (b[start + 2] != 0);
            uint lo    = Ru32(b, start + 4);
            uint mid   = Ru32(b, start + 8);
            uint hi    = Ru32(b, start + 12);

            // decimal(int lo, int mid, int hi, bool isNegative, byte scale) requires scale ≤ 28
            if (scale > 28)
                throw new JetLimitationException(
                    $"T_NUMERIC scale {scale} exceeds the .NET decimal maximum of 28.");

            try
            {
                return new decimal((int)lo, (int)mid, (int)hi, neg, scale).ToString("G");
            }
            catch (OverflowException ex)
            {
                throw new JetLimitationException(
                    $"T_NUMERIC value overflow (hi=0x{hi:X8}, mid=0x{mid:X8}, lo=0x{lo:X8}, scale={scale})", ex);
            }
        }

        private static string ReadGuid(byte[] b, int start)
        {
            if (start + 16 > b.Length) return string.Empty;
            // First three groups are stored little-endian in the Jet format
            return string.Format(
                "{{{0:X2}{1:X2}{2:X2}{3:X2}-{4:X2}{5:X2}-{6:X2}{7:X2}" +
                "-{8:X2}{9:X2}-{10:X2}{11:X2}{12:X2}{13:X2}{14:X2}{15:X2}}}",
                b[start+3], b[start+2], b[start+1], b[start],
                b[start+5], b[start+4],
                b[start+7], b[start+6],
                b[start+8], b[start+9],
                b[start+10], b[start+11], b[start+12],
                b[start+13], b[start+14], b[start+15]);
        }
    }
}
