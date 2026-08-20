using System.Text;

namespace MoonVeilDeobfuscator;

internal static class MoonVeilHeaderAnalyzer
{
    public static string BuildReport(IReadOnlyList<MoonVeilDecompiledPrototype> prototypes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("prototype header analysis");
        foreach (var item in prototypes)
        {
            var count = Math.Min(item.Instructions.HeaderBytes, item.Prototype.PrefixAndCode.Length);
            var header = item.Prototype.PrefixAndCode.Take(count).ToArray();
            sb.AppendLine($"prototype {item.Prototype.Index}: {count} inferred header byte(s)");
            sb.AppendLine("  " + (header.Length == 0 ? "<none>" : string.Join(" ", header.Select(x => x.ToString("X2")))));
            if (header.Length > 0)
                sb.AppendLine("  decimal: " + string.Join(" ", header));
        }
        return sb.ToString();
    }
}
