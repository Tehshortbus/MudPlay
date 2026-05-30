#nullable disable
#pragma warning disable
namespace JetDatabaseReader
{
    // Result type for the internal LVAL chain reader.
    internal sealed class LvalChainResult
    {
        public byte[] Data { get; }
        public string Error { get; }

        private LvalChainResult(byte[] data, string error) { Data = data; Error = error; }

        public static LvalChainResult Success(byte[] data) => new LvalChainResult(data, null);
        public static LvalChainResult Failure(string error) => new LvalChainResult(null, error);
    }
}
