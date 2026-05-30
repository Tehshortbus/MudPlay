#nullable disable
#pragma warning disable
using System.Collections.Generic;
using System.Data;

namespace JetDatabaseReader
{
    /// <summary>
    /// Result of a string-mode table read (<see cref="IAccessReader.ReadTableAsStrings"/>).
    /// All column values are returned as strings in <see cref="Rows"/>.
    /// Use <see cref="TableResult"/> when native CLR types are preferred.
    /// </summary>
    public class StringTableResult
    {
        /// <summary>Ordered list of column names.</summary>
        public List<string> Headers { get; set; }

        /// <summary>Up to <c>maxRows</c> rows, each row a list of string values (one per column).</summary>
        public List<List<string>> Rows { get; set; }

        /// <summary>Per-column schema information in the same order as <see cref="Headers"/>.</summary>
        public List<TableColumn> Schema { get; set; }

        /// <summary>Name of the table this result was read from.</summary>
        public string TableName { get; set; }

        /// <summary>Total number of rows in the result.</summary>
        public int RowCount => Rows?.Count ?? 0;

        /// <summary>
        /// Converts this result to a <see cref="DataTable"/> with all columns typed as <see cref="string"/>.
        /// </summary>
        public DataTable ToDataTable()
        {
            var dt = new DataTable(TableName);

            if (Headers == null) return dt;

            foreach (string header in Headers)
                dt.Columns.Add(header, typeof(string));

            if (Rows == null) return dt;

            foreach (List<string> row in Rows)
                dt.Rows.Add(row.ToArray());

            return dt;
        }
    }
}
