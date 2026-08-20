using System.Text;

namespace MoonVeilDeobfuscator;

internal sealed record MoonVeilOpcodeStats(
    byte Opcode,
    int Count,
    int ConstantTouches,
    int SmallA,
    int SmallB,
    int SmallC,
    int RelativeTargets,
    string Hint);

internal static class MoonVeilOpcodeProfiler
{
    public static IReadOnlyList<MoonVeilOpcodeStats> Analyze(IEnumerable<MoonVeilInstructionSet> sets)
    {
        var all = sets.SelectMany(x => x.Instructions).ToArray();
        return all
            .GroupBy(x => x.Op)
            .Select(g =>
            {
                var rows = g.ToArray();
                var constants = rows.Count(x => x.ConstantCandidates.Count > 0);
                var smallA = rows.Count(x => x.A < 32);
                var smallB = rows.Count(x => x.B < 32);
                var smallC = rows.Count(x => x.C < 32);
                var relative = rows.Count(x => IsSmallRelative(x.SBx) || IsSmallRelative(unchecked((sbyte)x.B)) || IsSmallRelative(unchecked((sbyte)x.C)));
                return new MoonVeilOpcodeStats(g.Key, rows.Length, constants, smallA, smallB, smallC, relative, InferHint(rows.Length, constants, smallA, smallB, smallC, relative));
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Opcode)
            .ToArray();
    }

    public static string BuildReport(IEnumerable<MoonVeilInstructionSet> sets)
    {
        var rows = Analyze(sets);
        var sb = new StringBuilder();
        sb.AppendLine("opcode profile");
        sb.AppendLine("hints are statistical only until a MoonVeil opcode map is proven");
        sb.AppendLine("opcode count constant-touch small-a small-b small-c rel-target hint");
        foreach (var row in rows)
            sb.AppendLine($"0x{row.Opcode:X2} {row.Count,5} {row.ConstantTouches,14} {row.SmallA,7} {row.SmallB,7} {row.SmallC,7} {row.RelativeTargets,10} {row.Hint}");
        return sb.ToString();
    }

    private static string InferHint(int count, int constants, int smallA, int smallB, int smallC, int relative)
    {
        if (count <= 0) return "unknown";
        var constantRatio = constants / (double)count;
        var relativeRatio = relative / (double)count;
        var registerRatio = (smallA + smallB + smallC) / (double)(count * 3);

        if (constantRatio >= 0.8 && registerRatio >= 0.45) return "constant-heavy / possible load-global-field op";
        if (relativeRatio >= 0.75 && constantRatio < 0.5) return "branch-like / relative operand candidate";
        if (registerRatio >= 0.85 && constantRatio < 0.5) return "register-heavy / possible move-arithmetic-call op";
        if (constantRatio >= 0.55) return "constant-touching op";
        if (relativeRatio >= 0.5) return "possible branch/test op";
        return "unknown";
    }

    private static bool IsSmallRelative(int value) => value != 0 && Math.Abs(value) <= 64;
}
