#nullable disable
#pragma warning disable
namespace JetDatabaseReader
{
    /// <summary>Unit of measurement for a <see cref="ColumnSize"/> value.</summary>
    public enum ColumnSizeUnit
    {
        /// <summary>Size in bits (e.g., Yes/No stores 1 bit in the null mask).</summary>
        Bits,
        /// <summary>Size in bytes.</summary>
        Bytes,
        /// <summary>Maximum character count for text columns.</summary>
        Chars,
        /// <summary>Variable-length with no declared maximum.</summary>
        Variable,
        /// <summary>Large-value data stored on LVAL pages (MEMO / OLE).</summary>
        Lval
    }
}
