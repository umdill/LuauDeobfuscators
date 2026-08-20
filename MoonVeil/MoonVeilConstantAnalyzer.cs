using System.Text;

namespace MoonVeilDeobfuscator;

internal static class MoonVeilConstantAnalyzer
{
    public static string BuildReport(IReadOnlyList<MoonVeilPrototype> prototypes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("constant analysis");
        foreach (var p in prototypes)
        {
            sb.AppendLine($"prototype {p.Index}");
            for (var i = 0; i < p.StringConstants.Count; i++)
            {
                var value = p.StringConstants[i];
                sb.AppendLine($"  k[{i}] {Classify(value),-12} {Escape(value)}");
            }
        }
        return sb.ToString();
    }

    private static string Classify(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "text";
        if (value.All(c => char.IsLetterOrDigit(c) || c == '_') && (char.IsLetter(value[0]) || value[0] == '_'))
            return "identifier";
        if (value.Contains(' ') || value.Any(char.IsPunctuation))
            return "text";
        return "string";
    }

    private static string Escape(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
