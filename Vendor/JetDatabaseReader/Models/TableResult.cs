#nullable disable
#pragma warning disable
using System;
using System.Collections.Generic;
using System.Data;

namespace JetDatabaseReader
{
    /// <summary>
    /// Result of a typed table read (<see cref="IAccessReader.ReadTable(string, int)"/>).
    /// Column values are returned as native CLR types (int, DateTime, decimal, etc.) in <see cref="Rows"/>.
    /// Use <see cref="StringTableResult"/> when raw string values are needed.
    /// </summary>
    public class TableResult
    {
        /// <summary>Ordered list of column names.</summary>
        public List<string> Headers { get; set; }

        /// <summary>Up to <c>maxRows</c> rows with native CLR types (int, DateTime, decimal, etc.).</summary>
        public List<object[]> Rows { get; set; }

        /// <summary>Per-column schema information in the same order as <see cref="Headers"/>.</summary>
        public List<TableColumn> Schema { get; set; }

        /// <summary>Name of the table this result was read from.</summary>
        public string TableName { get; set; }

        /// <summary>Total number of rows in the result.</summary>
        public int RowCount => Rows?.Count ?? 0;

        /// <summary>
        /// Converts this result to a <see cref="DataTable"/> with properly typed columns.
        /// Column types are taken from <see cref="Schema"/>; falls back to <see cref="object"/> when schema is unavailable.
        /// </summary>
        public DataTable ToDataTable()
        {
            var dt = new DataTable(TableName);

            if (Headers == null) return dt;

            for (int i = 0; i < Headers.Count; i++)
            {
                Type colType = (Schema != null && i < Schema.Count && Schema[i]?.Type != null)
                    ? Schema[i].Type
                    : typeof(object);
                dt.Columns.Add(Headers[i], colType);
            }

            if (Rows == null) return dt;

            foreach (object[] row in Rows)
            {
                DataRow dr = dt.NewRow();
                for (int i = 0; i < row.Length && i < dt.Columns.Count; i++)
                    dr[i] = row[i] ?? DBNull.Value;
                dt.Rows.Add(dr);
            }

            return dt;
        }
    }
}

