#nullable disable
#pragma warning disable
using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Schema entry for a single column in a <see cref="TableResult"/>.
    /// </summary>
    public sealed class TableColumn
    {
        /// <summary>Column name.</summary>
        public string Name { get; set; }

        /// <summary>CLR type that best represents this column (e.g., <see cref="string"/>, <see cref="int"/>, <see cref="DateTime"/>).</summary>
        public Type Type { get; set; }

        /// <summary>Structured size — use <see cref="ColumnSize.ToString"/> for a human-readable description.</summary>
        public ColumnSize Size { get; set; }
    }
}
