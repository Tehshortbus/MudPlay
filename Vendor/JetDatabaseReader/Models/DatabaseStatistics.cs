#nullable disable
#pragma warning disable
using System.Collections.Generic;

namespace JetDatabaseReader
{
    /// <summary>
    /// Statistical information about the database including size, table counts, and cache performance.
    /// </summary>
    public sealed class DatabaseStatistics
    {
        /// <summary>Total number of pages in the database.</summary>
        public long TotalPages { get; set; }

        /// <summary>Database file size in bytes.</summary>
        public long DatabaseSizeBytes { get; set; }

        /// <summary>Number of user tables in the database.</summary>
        public int TableCount { get; set; }

        /// <summary>Total number of rows across all tables (from TDEF, may be stale).</summary>
        public long TotalRows { get; set; }

        /// <summary>Row count for each table.</summary>
        public Dictionary<string, long> TableRowCounts { get; set; }

        /// <summary>Page cache hit rate percentage (0-100).</summary>
        public int PageCacheHitRate { get; set; }

        /// <summary>JET version string (e.g., "Jet4/ACE", "Jet3").</summary>
        public string Version { get; set; }

        /// <summary>Page size in bytes (2048 for Jet3, 4096 for Jet4).</summary>
        public int PageSize { get; set; }

        /// <summary>Code page identifier used for text encoding.</summary>
        public int CodePage { get; set; }
    }
}
