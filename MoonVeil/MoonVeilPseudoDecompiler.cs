using System.Text;

namespace MoonVeilDeobfuscator;

internal static class MoonVeilPseudoDecompiler
{
    public static string Decompile(MoonVeilPrototype prototype, MoonVeilInstructionSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"local function prototype_{prototype.Index}(...)");
        sb.AppendLine("    local r = {}");

        if (prototype.StringConstants.Count > 0)
        {
            sb.AppendLine("    local k = {");
            for (var i = 0; i < prototype.StringConstants.Count; i++)
                sb.AppendLine($"        [{i}] = {Quote(prototype.StringConstants[i])},");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine("    local k = {}");
        }

        sb.AppendLine();
        if (set.HeaderBytes > 0)
            sb.AppendLine($"    -- {set.HeaderBytes} header byte(s) were separated before instruction grouping opcode byte={set.OpcodeByte}");

        foreach (var ins in set.Instructions)
        {
            var refs = BuildConstantNote(prototype, ins);
            sb.AppendLine($"    -- [{ins.Index:D3}] op_{ins.Op:X2} a={ins.A} b={ins.B} c={ins.C} bx={ins.Bx} sbx={ins.SBx}{refs}");
        }

        if (set.TrailingBytes > 0)
            sb.AppendLine($"    -- {set.TrailingBytes} trailing byte(s) remain outside the inferred 4-byte instruction layout");

        sb.AppendLine("    return r[0]");
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static string BuildConstantNote(MoonVeilPrototype prototype, MoonVeilInstruction ins)
    {
        if (ins.ConstantCandidates.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var index in ins.ConstantCandidates)
        {
            if (index < 0 || index >= prototype.StringConstants.Count) continue;
            parts.Add($"k[{index}]={Quote(prototype.StringConstants[index])}");
        }
        return parts.Count == 0 ? string.Empty : "  -- " + string.Join(", ", parts);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
