using System.Globalization;

namespace MoonVeilDeobfuscator;

internal static class MoonVeilMath
{
    public static uint Xor(long a, long b) => unchecked((uint)a) ^ unchecked((uint)b);

    public static string Format(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 1e-12)
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static bool TryParseLuaInteger(string text, out long value)
    {
        text = text.Trim();
        var sign = 1L;
        if (text.StartsWith("-", StringComparison.Ordinal))
        {
            sign = -1;
            text = text[1..];
        }
        else if (text.StartsWith("+", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
            {
                value = unchecked((long)u) * sign;
                return true;
            }
        }
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = Convert.ToInt64(text[2..], 2) * sign;
                return true;
            }
            catch { }
        }
        else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value *= sign;
            return true;
        }

        value = 0;
        return false;
    }
}
