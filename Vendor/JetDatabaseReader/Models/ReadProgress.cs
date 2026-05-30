#nullable disable
#pragma warning disable
using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Detailed progress information for long-running read operations.
    /// </summary>
    public sealed class ReadProgress
    {
        /// <summary>Number of pages read so far.</summary>
        public long PagesRead { get; set; }

        /// <summary>Total number of pages to read.</summary>
        public long TotalPages { get; set; }

        /// <summary>Number of rows processed so far.</summary>
        public int RowsProcessed { get; set; }

        /// <summary>Time elapsed since the operation started.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>Percentage of pages read (0-100).</summary>
        public double PercentComplete => TotalPages > 0 ? (PagesRead * 100.0 / TotalPages) : 0;

        /// <summary>Estimated number of rows remaining (based on average rows per page).</summary>
        public int EstimatedRowsRemaining { get; set; }

        /// <summary>Current table being processed.</summary>
        public string TableName { get; set; }
    }
}
