using System.Globalization;

namespace NKeeper.Api.Databases;

/// <summary>Values as text for the results grid, whatever the database: dates as yyyy-MM-dd HH:mm:ss.fff, binary as 0x….</summary>
internal static class DbValueText
{
    public static string Format(object value, int maxChars)
    {
        var text = value switch
        {
            bool b => b ? "1" : "0",
            byte[] bytes => Hex(bytes, maxChars),
            Guid guid => guid.ToString("D").ToUpperInvariant(),
            DateTime date => date.TimeOfDay == TimeSpan.Zero && date.Millisecond == 0
                ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    public static string Hex(byte[] bytes, int maxChars)
    {
        var take = Math.Min(bytes.Length, (maxChars - 2) / 2);
        return "0x" + Convert.ToHexString(bytes, 0, take);
    }

    /// <summary>Whether a column of this .NET type holds numbers (shown right-aligned).</summary>
    public static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) || type == typeof(decimal)
        || type == typeof(double) || type == typeof(float) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong) || type == typeof(sbyte);
}
