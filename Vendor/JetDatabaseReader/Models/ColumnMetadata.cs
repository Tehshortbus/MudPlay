#nullable disable
#pragma warning disable
using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Rich metadata about a database column including type, size, and nullability information.
    /// </summary>
    public sealed class ColumnMetadata
    {
        /// <summary>Column name.</summary>
        public string Name { get; set; }

        /// <summary>Access-friendly type name (e.g., "Text", "Long Integer", "Date/Time").</summary>
        public string TypeName { get; set; }

        /// <summary>CLR type that best represents this column.</summary>
        public Type ClrType { get; set; }

        /// <summary>Maximum length for variable-length columns, or null for fixed-length.</summary>
        public int? MaxLength { get; set; }

        /// <summary>True if the column allows null values (always true for JET databases).</summary>
        public bool IsNullable { get; set; }

        /// <summary>True if the column is fixed-length.</summary>
        public bool IsFixedLength { get; set; }

        /// <summary>Zero-based ordinal position in the table.</summary>
        public int Ordinal { get; set; }

        /// <summary>Size description (e.g., "2 bytes", "255 chars", "LVAL").</summary>
        public string SizeDescription { get; set; }
    }
}
