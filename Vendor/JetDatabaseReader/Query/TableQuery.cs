#nullable disable
#pragma warning disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace JetDatabaseReader
{
    /// <summary>
    /// Provides a fluent API for querying table data with filtering and limiting.
    /// Supports both typed (<see cref="object[]"/>) and string (<see cref="string[]"/>) row access.
    ///
    /// Typed chain  (recommended): <see cref="Where"/>      → <see cref="Execute"/>              / <see cref="FirstOrDefault"/>        / <see cref="Count"/>
    /// String chain (compat):      <see cref="WhereAsStrings"/> → <see cref="ExecuteAsStrings"/> / <see cref="FirstOrDefaultAsStrings"/> / <see cref="CountAsStrings"/>
    /// </summary>
    public sealed class TableQuery
    {
        private readonly AccessReader _reader;
        private readonly string _tableName;
        private int? _limit;
        private Func<object[], bool> _typedFilter;
        private Func<string[], bool> _stringFilter;

        internal TableQuery(AccessReader reader, string tableName)
        {
            _reader = reader;
            _tableName = tableName;
        }

        // ── Filter ────────────────────────────────────────────────────────

        /// <summary>
        /// Filters rows using a predicate over properly typed values.
        /// Works with <see cref="Execute"/>, <see cref="FirstOrDefault"/>, and <see cref="Count"/>.
        /// </summary>
        public TableQuery Where(Func<object[], bool> predicate)
        {
            Guard.NotNull(predicate, nameof(predicate));
            _typedFilter = predicate;
            return this;
        }

        /// <summary>
        /// Filters rows using a predicate over string values.
        /// Works with <see cref="ExecuteAsStrings"/>, <see cref="FirstOrDefaultAsStrings"/>, and <see cref="CountAsStrings"/>.
        /// </summary>
        public TableQuery WhereAsStrings(Func<string[], bool> predicate)
        {
            Guard.NotNull(predicate, nameof(predicate));
            _stringFilter = predicate;
            return this;
        }

        // ── Limit ─────────────────────────────────────────────────────────

        /// <summary>
        /// Limits the number of rows returned by any Execute method.
        /// </summary>
        public TableQuery Take(int count)
        {
            Guard.Positive(count, nameof(count));
            _limit = count;
            return this;
        }

        // ── Typed execution ───────────────────────────────────────────────

        /// <summary>
        /// Executes the query and returns matching rows as properly typed object arrays.
        /// Each element is the native CLR type (int, DateTime, decimal, etc.) or <see cref="DBNull.Value"/> for nulls.
        /// </summary>
        public IEnumerable<object[]> Execute()
        {
            IEnumerable<object[]> rows = _reader.StreamRows(_tableName);

            if (_typedFilter != null)
                rows = rows.Where(_typedFilter);

            if (_limit.HasValue)
                rows = rows.Take(_limit.Value);

            return rows;
        }

        /// <summary>
        /// Executes the query and returns the first matching typed row, or null if no matches.
        /// </summary>
        public object[] FirstOrDefault()
        {
            return Execute().FirstOrDefault();
        }

        /// <summary>
        /// Executes the query and returns the count of typed rows matching the filter.
        /// </summary>
        public int Count()
        {
            return Execute().Count();
        }

        // ── String execution ──────────────────────────────────────────────

        /// <summary>
        /// Executes the query and returns matching rows as string arrays.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="Execute"/> which returns properly typed data.
        /// </summary>
        public IEnumerable<string[]> ExecuteAsStrings()
        {
            IEnumerable<string[]> rows = _reader.StreamRowsAsStrings(_tableName);

            if (_stringFilter != null)
                rows = rows.Where(_stringFilter);

            if (_limit.HasValue)
                rows = rows.Take(_limit.Value);

            return rows;
        }

        /// <summary>
        /// Executes the query and returns the first matching string row, or null if no matches.
        /// </summary>
        public string[] FirstOrDefaultAsStrings()
        {
            return ExecuteAsStrings().FirstOrDefault();
        }

        /// <summary>
        /// Executes the query and returns the count of string rows matching the filter.
        /// </summary>
        public int CountAsStrings()
        {
            return ExecuteAsStrings().Count();
        }
    }
}
