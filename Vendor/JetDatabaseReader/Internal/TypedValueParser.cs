#nullable disable
#pragma warning disable
using System;
using System.Globalization;

namespace JetDatabaseReader
{
    /// <summary>
    /// Helper class for parsing string values into proper CLR types.
    /// </summary>
    internal static class TypedValueParser
    {
        public static object ParseValue(string value, Type targetType)
        {
            if (string.IsNullOrEmpty(value))
                return DBNull.Value;

            try
            {
                if (targetType == typeof(string))
                    return value;

                if (targetType == typeof(bool))
                    return ParseBoolean(value);

                if (targetType == typeof(byte))
                    return byte.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(short))
                    return short.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(int))
                    return int.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(long))
                    return long.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(float))
                    return float.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(double))
                    return double.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(decimal))
                    return decimal.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(DateTime))
                    return DateTime.Parse(value, CultureInfo.InvariantCulture);

                if (targetType == typeof(Guid))
                    return Guid.Parse(value);

                if (targetType == typeof(byte[]))
                    return ParseByteArray(value);

                return value;
            }
            catch
            {
                return DBNull.Value;
            }
        }

        private static bool ParseBoolean(string value)
        {
            if (string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) ||
                value == "1" || value == "-1")
                return true;

            if (string.Equals(value, "False", StringComparison.OrdinalIgnoreCase) ||
                value == "0")
                return false;

            return bool.Parse(value);
        }

        private static byte[] ParseByteArray(string hexString)
        {
            // Format: "XX-XX-XX-XX" from BitConverter.ToString
            if (string.IsNullOrEmpty(hexString))
                return new byte[0];

            string[] parts = hexString.Split('-');
            byte[] result = new byte[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = Convert.ToByte(parts[i], 16);
            }

            return result;
        }
    }
}
