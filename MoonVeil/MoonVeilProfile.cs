using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

internal enum CacheOp { SubXor, XorDiv, XorAdd }
internal sealed record CacheHelper(string Name, CacheOp Operation, long Constant);

internal sealed class MoonVeilProfile
{
    public List<CacheHelper> CacheHelpers { get; } = new();

    public static MoonVeilProfile Analyze(string source)
    {
        var p = new MoonVeilProfile();
        var n = @"[-+]?(?:0x[0-9A-Fa-f]+|\d+)";
        var patterns = new (CacheOp Op, string Pattern)[]
        {
            (CacheOp.SubXor, $@"(?<name>[A-Za-z_]\w*)\s*=\s*function\(\s*(?<s>[A-Za-z_]\w*)\s*,\s*(?<x>[A-Za-z_]\w*)\s*,\s*(?<y>[A-Za-z_]\w*)\s*,\s*(?<k>[A-Za-z_]\w*)\s*\)\s*\k<s>\.H\[\k<k>\]\s*=\s*\k<x>\s*-\s*\k<s>\.(?<xor>[A-Za-z_]\w*)\(\s*\k<y>\s*,\s*(?<c>{n})\s*\)"),
            (CacheOp.XorDiv, $@"(?<name>[A-Za-z_]\w*)\s*=\s*function\(\s*(?<s>[A-Za-z_]\w*)\s*,\s*(?<x>[A-Za-z_]\w*)\s*,\s*(?<y>[A-Za-z_]\w*)\s*,\s*(?<k>[A-Za-z_]\w*)\s*\)\s*\k<s>\.H\[\k<k>\]\s*=\s*\k<s>\.(?<xor>[A-Za-z_]\w*)\(\s*\k<x>\s*,\s*(?<c>{n})\s*\)\s*/\s*\k<y>"),
            (CacheOp.XorAdd, $@"(?<name>[A-Za-z_]\w*)\s*=\s*function\(\s*(?<s>[A-Za-z_]\w*)\s*,\s*(?<x>[A-Za-z_]\w*)\s*,\s*(?<y>[A-Za-z_]\w*)\s*,\s*(?<k>[A-Za-z_]\w*)\s*\)\s*\k<s>\.H\[\k<k>\]\s*=\s*\k<s>\.(?<xor>[A-Za-z_]\w*)\(\s*\k<x>\s*,\s*(?<c>{n})\s*\)\s*\+\s*\k<y>"),
        };

        foreach (var (op, pattern) in patterns)
        {
            foreach (Match m in Regex.Matches(source, pattern, RegexOptions.Singleline))
            {
                if (!MoonVeilMath.TryParseLuaInteger(m.Groups["c"].Value, out var c)) continue;
                if (p.CacheHelpers.Any(h => h.Name == m.Groups["name"].Value)) continue;
                p.CacheHelpers.Add(new CacheHelper(m.Groups["name"].Value, op, c));
            }
        }
        var looksMoonVeil = source.Contains("MoonVeil", StringComparison.OrdinalIgnoreCase) || source.Contains(".H[", StringComparison.Ordinal);
        if (looksMoonVeil)
        {
            if (!p.CacheHelpers.Any(h => h.Operation == CacheOp.SubXor) && Regex.IsMatch(source, @"\bJ\s*=\s*function\b"))
                p.CacheHelpers.Add(new("J", CacheOp.SubXor, 0x4388));
            if (!p.CacheHelpers.Any(h => h.Operation == CacheOp.XorDiv) && Regex.IsMatch(source, @"\bI\s*=\s*function\b"))
                p.CacheHelpers.Add(new("I", CacheOp.XorDiv, 0xb886));
            if (!p.CacheHelpers.Any(h => h.Operation == CacheOp.XorAdd) && Regex.IsMatch(source, @"\bK\s*=\s*function\b"))
                p.CacheHelpers.Add(new("K", CacheOp.XorAdd, 0x524a));
        }

        return p;
    }
}
