using System.Text;

namespace MoonVeilDeobfuscator;

internal sealed record MoonVeilDecompiledPrototype(
    MoonVeilPrototype Prototype,
    MoonVeilInstructionSet Instructions,
    MoonVeilControlFlow ControlFlow,
    string PseudoLua);

internal sealed record MoonVeilDecompilerOutput(
    IReadOnlyList<MoonVeilDecompiledPrototype> Prototypes,
    string Source,
    string Disassembly,
    string ControlFlow,
    string OpcodeProfile,
    string HeaderReport,
    string Quality);

internal static class MoonVeilDecompilerPipeline
{
    public static MoonVeilDecompilerOutput Decompile(byte[] payload, IReadOnlyList<MoonVeilPrototype> prototypes)
    {
        var decoded = new List<MoonVeilDecompiledPrototype>();
        var disassembly = new StringBuilder();
        var flowReport = new StringBuilder();
        var source = new StringBuilder();
        source.AppendLine();

        foreach (var prototype in prototypes)
        {
            var instructions = MoonVeilInstructionDecoder.Decode(prototype);
            var flow = MoonVeilControlFlowAnalyzer.Analyze(instructions);
            var pseudo = MoonVeilPseudoDecompiler.Decompile(prototype, instructions);
            decoded.Add(new MoonVeilDecompiledPrototype(prototype, instructions, flow, pseudo));

            source.AppendLine(pseudo);
            disassembly.AppendLine(MoonVeilInstructionDecoder.BuildDisassembly(prototype, instructions));
            flowReport.AppendLine(MoonVeilControlFlowAnalyzer.BuildReport(prototype, instructions, flow));
        }

        if (decoded.Count > 0)
        {
            source.AppendLine("-- root prototype candidate");
            source.AppendLine($"return prototype_{decoded[^1].Prototype.Index}()");
        }

        var profile = MoonVeilOpcodeProfiler.BuildReport(decoded.Select(x => x.Instructions));
        var headers = MoonVeilHeaderAnalyzer.BuildReport(decoded);
        var exactLayouts = decoded.Count(x => x.Instructions.TrailingBytes == 0);
        var quality = $"generic MoonVeil decompiler: {decoded.Count} prototype(s), {decoded.Sum(x => x.Instructions.Instructions.Count)} inferred instruction word(s), {exactLayouts}/{decoded.Count} exact 4-byte layout(s); opcode position/header/alignment are inferred, opcode semantics remain unknown unless annotated";

        return new MoonVeilDecompilerOutput(decoded, source.ToString(), disassembly.ToString(), flowReport.ToString(), profile, headers, quality);
    }
}
