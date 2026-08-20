using System.Text;
using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

internal sealed record PayloadExtraction(
    string Encoded,
    byte[] Stage1,
    byte[] Stage2,
    IReadOnlyList<PayloadString> Strings,
    string Report);

internal sealed record PayloadString(int Offset, string Value);

internal static class MoonVeilPayloadExtractor
{
    // used by moonveil itself
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";

    public static bool TryExtract(string source, out PayloadExtraction extraction)
    {
        extraction = null!;
        var match = Regex.Match(source, "\\.k\\s*\"(?<blob>[^\"]{100,})\"", RegexOptions.Singleline);
        if (!match.Success) return false;
        var blob = match.Groups["blob"].Value;
        if (blob.Any(c => Alphabet.IndexOf(c) < 0)) return false;

        try
        {
            var stage1 = DecodeBase85(blob);
            var stage2 = DecodeLz(stage1);
            var strings = ExtractTaggedStrings(stage2);
            var report = BuildReport(blob, stage1, stage2, strings);
            extraction = new(blob, stage1, stage2, strings, report);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] DecodeBase85(string input)
    {
        var rem = input.Length % 5;
        var padded = rem == 0 ? input : input + new string('~', 5 - rem);
        using var ms = new MemoryStream(padded.Length / 5 * 4);
        for (var i = 0; i < padded.Length; i += 5)
        {
            ulong value = 0;
            for (var j = 0; j < 5; j++)
            {
                var digit = Alphabet.IndexOf(padded[i + j]);
                if (digit < 0) throw new InvalidDataException("invalid MoonVeil base85 digit");
                value = value * 85UL + (uint)digit;
            }
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }
        var bytes = ms.ToArray();
        if (rem == 0) return bytes;
        // rem-1 (ascii85)
        var trim = 5 - rem;
        var keep = Math.Max(0, bytes.Length - trim);
        return bytes[..keep];
    }

    public static byte[] DecodeLz(byte[] input)
    {
        var output = new List<byte>(input.Length * 2);
        var window = new List<byte>(2048);
        var pos = 0;

        void Append(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                output.Add(b);
                window.Add(b);
                if (window.Count > 2048) window.RemoveAt(0);
            }
        }

        while (pos < input.Length)
        {
            var flags = input[pos++];
            for (var bit = 0; bit < 8 && pos < input.Length; bit++)
            {
                if ((flags & 1) != 0)
                {
                    Append(input.AsSpan(pos, 1));
                    pos++;
                }
                else
                {
                    if (pos + 1 >= input.Length) break;
                    var packed = (input[pos] << 8) | input[pos + 1];
                    pos += 2;
                    var distance = packed >> 5;
                    var length = (packed & 31) + 3;
                    var luaStart = window.Count - distance; // 1-based
                    var start = luaStart - 1;               // zero-based
                    if (start < 0 || start >= window.Count)
                        throw new InvalidDataException($"invalid MoonVeil LZ reference distance={distance}, window={window.Count}");
                    var temp = new byte[Math.Min(length, window.Count - start)];
                    for (var i = 0; i < temp.Length; i++) temp[i] = window[start + i];
                    Append(temp);
                }
                flags >>= 1;
            }
        }

        return output.ToArray();
    }

    private static List<PayloadString> ExtractTaggedStrings(byte[] payload)
    {
        var list = new List<PayloadString>();
        for (var i = 0; i + 2 <= payload.Length; i++)
        {
            if (payload[i] != 0x04) continue;
            var length = payload[i + 1];
            if (length == 0 || i + 2 + length > payload.Length) continue;
            var span = payload.AsSpan(i + 2, length);
            if (!span.ToArray().All(b => b is >= 0x20 and <= 0x7e)) continue;
            var value = Encoding.UTF8.GetString(span);
            list.Add(new(i, value));
            i += 1 + length;
        }
        return list;
    }

    private static string BuildReport(string blob, byte[] stage1, byte[] stage2, IReadOnlyList<PayloadString> strings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"encoded characters: {blob.Length}");
        sb.AppendLine($"stage 1 (base85) bytes: {stage1.Length}");
        sb.AppendLine($"stage 2 (LZ) bytes: {stage2.Length}");
        sb.AppendLine($"tagged printable strings: {strings.Count}");
        sb.AppendLine();
        sb.AppendLine("strings from decoded payload:");
        foreach (var s in strings)
            sb.AppendLine($"  0x{s.Offset:X4}  {Escape(s.Value)}");
        return sb.ToString();
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
