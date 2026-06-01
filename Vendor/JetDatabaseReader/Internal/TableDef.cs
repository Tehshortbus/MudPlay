#nullable disable
#pragma warning disable
using System.Collections.Generic;

namespace JetDatabaseReader
{
    internal sealed class TableDef
    {
        public List<ColumnInfo> Columns = new List<ColumnInfo>();
        public long RowCount;           // num_rows from TDEF page offset 16
        public bool HasDeletedColumns;  // true if ColNum sequence has gaps
    }
}
