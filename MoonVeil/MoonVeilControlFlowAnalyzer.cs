using System.Text;

namespace MoonVeilDeobfuscator;

internal sealed record MoonVeilFlowEdge(int From, int To, string Kind, int Confidence);

internal sealed record MoonVeilControlFlow(IReadOnlyList<MoonVeilFlowEdge> Edges, IReadOnlyList<int> Leaders);

internal static class MoonVeilControlFlowAnalyzer
{
    public static MoonVeilControlFlow Analyze(MoonVeilInstructionSet set)
    {
        var edges = new List<MoonVeilFlowEdge>();
        var leaders = new SortedSet<int>();
        if (set.Instructions.Count > 0) leaders.Add(0);

        for (var i = 0; i < set.Instructions.Count; i++)
        {
            if (i + 1 < set.Instructions.Count)
                edges.Add(new MoonVeilFlowEdge(i, i + 1, "fallthrough", 100));

            var ins = set.Instructions[i];
            var targets = RelativeTargets(i, ins, set.Instructions.Count);
            foreach (var target in targets)
            {
                edges.Add(new MoonVeilFlowEdge(i, target, "relative-candidate", 30));
                leaders.Add(target);
                if (i + 1 < set.Instructions.Count) leaders.Add(i + 1);
            }
        }

        return new MoonVeilControlFlow(edges, leaders.ToArray());
    }

    public static string BuildReport(MoonVeilPrototype prototype, MoonVeilInstructionSet set, MoonVeilControlFlow flow)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"prototype {prototype.Index} control-flow candidates");
        sb.AppendLine("relative edges are heuristic until the MoonVeil opcode map is known");
        sb.AppendLine("leaders: " + string.Join(", ", flow.Leaders));
        foreach (var edge in flow.Edges.Where(x => x.Kind != "fallthrough"))
            sb.AppendLine($"  {edge.From} -> {edge.To}  {edge.Kind}  confidence={edge.Confidence}%");
        return sb.ToString();
    }

    private static IReadOnlyList<int> RelativeTargets(int index, MoonVeilInstruction ins, int count)
    {
        var found = new SortedSet<int>();
        Add(ins.SBx);
        Add(unchecked((sbyte)ins.C));
        Add(unchecked((sbyte)ins.B));
        return found.ToArray();

        void Add(int delta)
        {
            if (delta == 0 || Math.Abs(delta) > count) return;
            var target = index + 1 + delta;
            if (target >= 0 && target < count) found.Add(target);
        }
    }
}
