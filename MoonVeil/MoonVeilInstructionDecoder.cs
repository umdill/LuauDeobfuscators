using System.Buffers.Binary;
using System.Text;

namespace MoonVeilDeobfuscator;

internal sealed record MoonVeilInstruction(
    int Index,
    int Offset,
    uint Raw,
    byte Op,
    byte A,
    byte B,
    byte C,
    ushort Bx,
    short SBx,
    int Ax,
    IReadOnlyList<int> ConstantCandidates);

internal sealed record MoonVeilInstructionSet(
    int HeaderBytes,
    int TrailingBytes,
    int OpcodeByte,
    double Score,
    IReadOnlyList<MoonVeilInstruction> Instructions);

internal static class MoonVeilInstructionDecoder
{
    public static MoonVeilInstructionSet Decode(MoonVeilPrototype prototype)
    {
        var bytes = prototype.PrefixAndCode;
        MoonVeilInstructionSet? best = null;
        var maxHeader = Math.Min(16, bytes.Length);

        for (var header = 0; header <= maxHeader; header++)
        {
            var count = (bytes.Length - header) / 4;
            if (count <= 0) continue;
            var trailing = (bytes.Length - header) % 4;

            for (var opcodeByte = 0; opcodeByte < 4; opcodeByte++)
            {
                var instructions = new List<MoonVeilInstruction>(count);
                var opCounts = new Dictionary<byte, int>();

                for (var i = 0; i < count; i++)
                {
                    var off = header + i * 4;
                    var word = bytes.AsSpan(off, 4);
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(word);
                    var op = word[opcodeByte];
                    Span<byte> operands = stackalloc byte[3];
                    var write = 0;
                    for (var p = 0; p < 4; p++)
                    {
                        if (p == opcodeByte) continue;
                        operands[write++] = word[p];
                    }

                    var a = operands[0];
                    var b = operands[1];
                    var c = operands[2];
                    var bx = (ushort)(b | (c << 8));
                    var sbx = unchecked((short)bx);
                    var ax = a | (b << 8) | (c << 16);
                    var constants = FindConstantCandidates(prototype.StringConstants.Count, a, b, c, bx);
                    instructions.Add(new MoonVeilInstruction(i, prototype.StartOffset + off, raw, op, a, b, c, bx, sbx, ax, constants));
                    opCounts[op] = opCounts.TryGetValue(op, out var n) ? n + 1 : 1;
                }

                var score = Score(bytes, header, trailing, opcodeByte, instructions, opCounts);
                var candidate = new MoonVeilInstructionSet(header, trailing, opcodeByte, score, instructions);
                if (best is null || candidate.Score > best.Score)
                    best = candidate;
            }
        }

        return best ?? new MoonVeilInstructionSet(0, bytes.Length, 0, 0, Array.Empty<MoonVeilInstruction>());
    }

    public static string BuildDisassembly(MoonVeilPrototype prototype, MoonVeilInstructionSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"prototype {prototype.Index} instruction view");
        sb.AppendLine($"header bytes: {set.HeaderBytes}");
        sb.AppendLine($"trailing bytes: {set.TrailingBytes}");
        sb.AppendLine($"opcode byte: {set.OpcodeByte}");
        sb.AppendLine($"layout score: {set.Score:F2}");
        sb.AppendLine($"instructions: {set.Instructions.Count}");
        sb.AppendLine();

        foreach (var ins in set.Instructions)
        {
            sb.Append($"  {ins.Index,4}  0x{ins.Offset:X4}  {ins.Raw:X8}  op={ins.Op,3} a={ins.A,3} b={ins.B,3} c={ins.C,3} bx={ins.Bx,5} sbx={ins.SBx,6} ax={ins.Ax,8}");
            if (ins.ConstantCandidates.Count > 0)
                sb.Append("  k?=" + string.Join(",", ins.ConstantCandidates));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static IReadOnlyList<int> FindConstantCandidates(int count, byte a, byte b, byte c, ushort bx)
    {
        if (count <= 0) return Array.Empty<int>();
        var found = new SortedSet<int>();
        Add(a);
        Add(b);
        Add(c);
        if (bx <= byte.MaxValue) Add((byte)bx);
        return found.ToArray();

        void Add(int value)
        {
            if (value >= 0 && value < count) found.Add(value);
            if (value >= 1 && value <= count) found.Add(value - 1);
        }
    }

    private static double Score(byte[] bytes, int header, int trailing, int opcodeByte, IReadOnlyList<MoonVeilInstruction> instructions, IReadOnlyDictionary<byte, int> opCounts)
    {
        var score = 0.0;
        score += trailing == 0 ? 8 : -trailing * 3;
        score += instructions.Count * 0.15;
        score -= header * 0.12;

        var common = instructions.Count(x => x.Op <= 0x7f);
        score += common * 0.08;
        var distinct = opCounts.Count;
        if (distinct >= 2 && distinct <= Math.Max(4, instructions.Count / 2 + 1)) score += 3;
        if (distinct == instructions.Count && instructions.Count > 8) score -= 2;
        if (opCounts.Values.Any(x => x >= Math.Max(2, instructions.Count / 5))) score += 1.25;

        if (header > 0)
        {
            var headerSmall = bytes.Take(header).Count(x => x <= 0x20);
            score += headerSmall * 0.25;
        }

        if (opcodeByte is 0 or 3) score += 0.1;
        return score;
    }
}
