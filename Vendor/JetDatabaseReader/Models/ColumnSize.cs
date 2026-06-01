#nullable disable
#pragma warning disable
namespace JetDatabaseReader
{
    /// <summary>
    /// Structured column size: a numeric <see cref="Value"/> paired with a <see cref="ColumnSizeUnit"/>.
    /// Use <see cref="ToString"/> for a human-readable description.
    /// </summary>
    public readonly struct ColumnSize
    {
        /// <summary>Numeric count; <c>null</c> for <see cref="ColumnSizeUnit.Variable"/> and <see cref="ColumnSizeUnit.Lval"/>.</summary>
        public int? Value { get; }

        /// <summary>The unit in which <see cref="Value"/> is expressed.</summary>
        public ColumnSizeUnit Unit { get; }

        private ColumnSize(int? value, ColumnSizeUnit unit) { Value = value; Unit = unit; }

        /// <summary>Creates a fixed size expressed in bits.</summary>
        public static ColumnSize FromBits(int count)  => new ColumnSize(count, ColumnSizeUnit.Bits);

        /// <summary>Creates a fixed size expressed in bytes.</summary>
        public static ColumnSize FromBytes(int count) => new ColumnSize(count, ColumnSizeUnit.Bytes);

        /// <summary>Creates a maximum character count for a text column.</summary>
        public static ColumnSize FromChars(int count) => new ColumnSize(count, ColumnSizeUnit.Chars);

        /// <summary>Variable-length with no declared maximum.</summary>
        public static readonly ColumnSize Variable = new ColumnSize(null, ColumnSizeUnit.Variable);

        /// <summary>Large-value data stored on LVAL pages (MEMO / OLE).</summary>
        public static readonly ColumnSize Lval = new ColumnSize(null, ColumnSizeUnit.Lval);

        /// <inheritdoc/>
        public override string ToString()
        {
            switch (Unit)
            {
                case ColumnSizeUnit.Bits:     return Value == 1 ? "1 bit"  : $"{Value} bits";
                case ColumnSizeUnit.Bytes:    return Value == 1 ? "1 byte" : $"{Value} bytes";
                case ColumnSizeUnit.Chars:    return $"{Value} chars";
                case ColumnSizeUnit.Variable: return "variable";
                case ColumnSizeUnit.Lval:     return "LVAL";
                default:                      return string.Empty;
            }
        }
    }
}
