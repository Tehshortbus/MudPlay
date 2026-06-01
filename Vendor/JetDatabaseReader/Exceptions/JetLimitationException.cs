#nullable disable
#pragma warning disable
using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Exception thrown when a JET database limitation is encountered that prevents correct data reading.
    /// </summary>
    public sealed class JetLimitationException : Exception
    {
        public JetLimitationException(string message) : base(message) { }
        public JetLimitationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
