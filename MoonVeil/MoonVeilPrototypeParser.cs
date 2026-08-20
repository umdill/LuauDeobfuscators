using System.Text;

namespace MoonVeilDeobfuscator;

internal sealed record MoonVeilPrototype(
    int Index,
    int StartOffset,
    int ConstantPoolOffset,
    int EndOffset,
    byte[] PrefixAndCode,
    IReadOnlyList<string> StringConstants);

internal static class MoonVeilPrototypeParser
{
    public static IReadOnlyList<MoonVeilPrototype> Parse(byte[] payload)
    {
        var pools = FindStringPools(payload);
        var result = new List<MoonVeilPrototype>();
        var recordStart = 0;

        for (var i = 0; i < pools.Count; i++)
        {
            var pool = pools[i];
            if (pool.Offset < recordStart) continue;
            var end = pool.EndOffset;
            var prefix = payload[recordStart..pool.Offset];
            result.Add(new MoonVeilPrototype(
                result.Count,
                recordStart,
                pool.Offset,
                end,
                prefix,
                pool.Strings));
            recordStart = end;
            if (recordStart < payload.Length && payload[recordStart] == 0)
                recordStart++;
        }

        return result;
    }

    public static string BuildReport(byte[] payload, IReadOnlyList<MoonVeilPrototype> prototypes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("proto analysis");
        sb.AppendLine($"decoded payload bytes: {payload.Length}");
        sb.AppendLine($"prototype-like records: {prototypes.Count}");
        sb.AppendLine();
        foreach (var p in prototypes)
        {
            sb.AppendLine($"prototype {p.Index}: 0x{p.StartOffset:X4}..0x{p.EndOffset - 1:X4}");
            sb.AppendLine($"  pre-constant/code bytes: {p.PrefixAndCode.Length}");
            sb.AppendLine($"  constant pool @ 0x{p.ConstantPoolOffset:X4}: {p.StringConstants.Count} string constant(s)");
            foreach (var c in p.StringConstants)
                sb.AppendLine("    \"" + Escape(c) + "\"");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private sealed record Pool(int Offset, int EndOffset, List<string> Strings);

    private static List<Pool> FindStringPools(byte[] data)
    {
        var candidates = new List<Pool>();
        for (var i = 0; i < data.Length; i++)
        {
            var count = data[i];
            if (count == 0 || count > 64) continue;
            var pos = i + 1;
            var strings = new List<string>();
            var ok = true;
            for (var n = 0; n < count; n++)
            {
                if (pos + 2 > data.Length || data[pos] != 0x04)
                {
                    ok = false;
                    break;
                }
                var len = data[pos + 1];
                pos += 2;
                if (len == 0 || pos + len > data.Length)
                {
                    ok = false;
                    break;
                }
                var span = data.AsSpan(pos, len);
                for (var k = 0; k < span.Length; k++)
                {
                    if (span[k] < 0x20 || span[k] > 0x7e)
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok) break;
                strings.Add(Encoding.UTF8.GetString(span));
                pos += len;
            }

            if (ok && strings.Count == count)
            {
                // filters accidental byte mathc./es
                var printableBytes = strings.Sum(x => x.Length);
                if (printableBytes >= 4)
                {
                    candidates.Add(new Pool(i, pos, strings));
                    i = pos - 1;
                }
            }
        }
        return candidates;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}
