#nullable disable
#pragma warning disable
using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Helper methods for input validation and guard clauses.
    /// </summary>
    internal static class Guard
    {
        public static void NotNull(object value, string paramName)
        {
            if (value == null)
                throw new ArgumentNullException(paramName);
        }

        public static void NotNullOrEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value cannot be null or empty", paramName);
        }

        public static void InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(paramName,
                    $"Value must be between {min} and {max}");
        }

        public static void Positive(int value, string paramName)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(paramName, "Value must be positive");
        }

        public static void NotDisposed(bool disposed, string objectName)
        {
            if (disposed)
                throw new ObjectDisposedException(objectName);
        }
    }
}
