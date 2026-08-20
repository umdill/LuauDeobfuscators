using System.Text;
using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

internal static class MoonVeilDispatcherAnalyzer
{
    public static IEnumerable<string> Analyze(string source)
    {
        var results = new List<string>();
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(source, @"\b(?<v>[A-Za-z_]\w*)\s*=\s*(?<seed>-?\d+)\s*(?=(?:while\s+true\s+do|repeat)\b)", RegexOptions.IgnoreCase))
        {
            var v = m.Groups["v"].Value;
            if (!candidates.ContainsKey(v)) candidates[v] = new Candidate(v, m.Groups["seed"].Value);
        }

        foreach (var c in candidates.Values)
        {
            var escaped = Regex.Escape(c.Name);
            c.Comparisons = Regex.Matches(source, $@"\b{escaped}\s*(?:<=|>=|==|~=|<|>)\s*-?\d+").Count;
            c.LiteralAssignments = Regex.Matches(source, $@"\b{escaped}\s*=\s*-?\d+\b").Count;
            c.ComputedAssignments = Regex.Matches(source, $@"\b{escaped}\s*=\s*(?!-?\d+\b)[^\r\n;,]+", RegexOptions.IgnoreCase).Count;
        }

        foreach (var c in candidates.Values.OrderByDescending(x => x.Comparisons).Take(8))
            results.Add($"dispatcher candidate {c.Name}: seed={c.Seed}, comparisons={c.Comparisons}, literal transitions={c.LiteralAssignments}, computed transitions={c.ComputedAssignments}");

        if (candidates.Count == 0)
            results.Add("dispatcher analysis: no literal-seeded flattened loop detected");

        return results;
    }

    private sealed class Candidate
    {
        public Candidate(string name, string seed) { Name = name; Seed = seed; }
        public string Name { get; }
        public string Seed { get; }
        public int Comparisons { get; set; }
        public int LiteralAssignments { get; set; }
        public int ComputedAssignments { get; set; }
    }
}
