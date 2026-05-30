#nullable disable
#pragma warning disable
using System.Collections.Generic;

namespace JetDatabaseReader
{
    internal sealed class ColumnInfo
    {
        public byte   Type;
        public int    ColNum;    // col_num: absolute column number (includes deleted cols)
        public int    VarIdx;    // offset_V: 0-based index in var_table
        public int    FixedOff;  // offset_F: byte offset within the fixed area
        public int    Size;      // col_len (0 for MEMO/OLE/variable)
        public byte   Flags;
        public string Name = string.Empty;

        // Inherently fixed-length types are always fixed regardless of FLAG_FIXED.
        // Variable-length types (TEXT, BINARY, MEMO, OLE) are always variable.
        // For any other type, fall back to the FLAG_FIXED bit in the descriptor.
        public bool IsFixed
        {
            get
            {
                switch (Type)
                {
                    case 0x01: // T_BOOL
                    case 0x02: // T_BYTE
                    case 0x03: // T_INT
                    case 0x04: // T_LONG
                    case 0x05: // T_MONEY
                    case 0x06: // T_FLOAT
                    case 0x07: // T_DOUBLE
                    case 0x08: // T_DATETIME
                    case 0x0F: // T_GUID
                    case 0x10: // T_NUMERIC
                        return true;
                    case 0x0A: // T_TEXT
                    case 0x09: // T_BINARY
                    case 0x0C: // T_MEMO
                    case 0x0B: // T_OLE
                        return false;
                    default:
                        return (Flags & 0x01) != 0; // FLAG_FIXED
                }
            }
        }
    }
}
