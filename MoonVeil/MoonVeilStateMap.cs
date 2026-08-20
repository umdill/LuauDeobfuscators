using System.Text;
using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;
internal static class MoonVeilStateMap
{
    public static string Build(string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("statemap");
        sb.AppendLine("::::::::::::::::::::::::::");
        sb.AppendLine();

        var code = LuaText.CodeOnly(source);
        var seeds = Regex.Matches(code, @"\b(?<v>[A-Za-z_]\w*)\s*=\s*(?<n>-?\d+)\s*(?=(?:while\s+true\s+do|repeat)\b)", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .GroupBy(m => m.Groups["v"].Value, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (seeds.Count == 0)
        {
            sb.AppendLine("no seeded dispatch loops detected..");
            return sb.ToString();
        }

        foreach (var seed in seeds)
        {
            var v = seed.Groups["v"].Value;
            var escaped = Regex.Escape(v);
            sb.AppendLine($"dispatched {v} (seed {seed.Groups["n"].Value})");
            sb.AppendLine(new string('-', 36));

            var literals = Regex.Matches(code, $@"\b{escaped}\s*=\s*(?<n>-?\d+)\b")
                .Cast<Match>()
                .Select(m => long.TryParse(m.Groups["n"].Value, out var n) ? (long?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            sb.AppendLine($"literal states/transitions ({literals.Count}):");
            if (literals.Count > 0)
            {
                for (var i = 0; i < literals.Count; i += 16)
                    sb.AppendLine("  " + string.Join(", ", literals.Skip(i).Take(16)));
            }

            var comparisons = Regex.Matches(code, $@"\b{escaped}\s*(?<op><=|>=|==|~=|<|>)\s*(?<n>-?\d+)")
                .Cast<Match>()
                .Select(m => m.Groups["op"].Value + m.Groups["n"].Value)
                .Distinct()
                .ToList();
            sb.AppendLine($"dec tests: {comparisons.Count}");

            var computed = Regex.Matches(code, $@"\b{escaped}\s*=\s*(?<rhs>[^\r\n;,]+)")
                .Cast<Match>()
                .Select(m => m.Groups["rhs"].Value.Trim())
                .Where(rhs => !Regex.IsMatch(rhs, @"^-?\d+$"))
                .Distinct()
                .Take(120)
                .ToList();
            sb.AppendLine($"transition forms (showing {computed.Count}):");
            foreach (var rhs in computed) sb.AppendLine("  " + rhs);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
