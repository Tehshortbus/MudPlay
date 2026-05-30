#nullable disable
#pragma warning disable
namespace JetDatabaseReader
{
    /// <summary>
    /// Name, stored row-count, and column-count for a single user table.
    /// Returned as an element of the list from <see cref="IAccessReader.GetTableStats"/>.
    /// </summary>
    public sealed class TableStat
    {
        /// <summary>Table name.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Row count stored in the TDEF page.
        /// May be stale after bulk deletes or imports without a Compact &amp; Repair.
        /// Use <see cref="IAccessReader.GetRealRowCount"/> for an accurate count.
        /// </summary>
        public long RowCount { get; set; }

        /// <summary>Number of columns defined in the table schema.</summary>
        public int ColumnCount { get; set; }
    }
}
