using MoonVeilDeobfuscator;

if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
    return SelfTests.Run();

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var inputArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
if (string.IsNullOrWhiteSpace(inputArg))
{
    PrintUsage();
    return 1;
}

var mode = args.Any(a => a.Equals("--safe", StringComparison.OrdinalIgnoreCase)) ? "safe"
    : args.Any(a => a.Equals("--aggressive", StringComparison.OrdinalIgnoreCase)) ? "aggressive"
    : "both";

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var inputPath = Path.GetFullPath(positional[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"input file not found: {inputPath}");
    return 1;
}

var baseOutput = positional.Length >= 2
    ? Path.GetFullPath(positional[1])
    : Path.Combine(
        Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
        $"{Path.GetFileNameWithoutExtension(inputPath)}.deobfuscated.lua");

try
{
    var source = File.ReadAllText(inputPath);
    var version = MoonVeilDeobfuscatorEngine.DetectVersion(source);
    Console.WriteLine($"MoonVeil {version}");

    var reconstructedMain = false;
    if (MoonVeilPayloadExtractor.TryExtract(source, out var payload))
    {
        var payloadBase = Path.Combine(
            Path.GetDirectoryName(baseOutput) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(baseOutput) + ".payload");
        File.WriteAllBytes(payloadBase + ".stage1.bin", payload.Stage1);
        File.WriteAllBytes(payloadBase + ".decoded.bin", payload.Stage2);
        File.WriteAllText(payloadBase + ".strings.txt", string.Join(Environment.NewLine, payload.Strings.Select(x => $"0x{x.Offset:X4}  {x.Value}")) + Environment.NewLine);
        File.WriteAllText(payloadBase + ".report.txt", payload.Report);

        var prototypes = MoonVeilPrototypeParser.Parse(payload.Stage2);
        var protoReport = MoonVeilPrototypeParser.BuildReport(payload.Stage2, prototypes);
        File.WriteAllText(payloadBase + ".prototypes.txt", protoReport);

        if (prototypes.Count > 0)
        {
            var decompiled = MoonVeilDecompilerPipeline.Decompile(payload.Stage2, prototypes);
            File.WriteAllText(baseOutput, decompiled.Source);
            File.WriteAllText(baseOutput + ".reconstruction.txt", decompiled.Quality + Environment.NewLine);
            File.WriteAllText(payloadBase + ".disasm.txt", decompiled.Disassembly);
            File.WriteAllText(payloadBase + ".cfg.txt", decompiled.ControlFlow);
            File.WriteAllText(payloadBase + ".opcodes.txt", decompiled.OpcodeProfile);
            File.WriteAllText(payloadBase + ".headers.txt", decompiled.HeaderReport);
            File.WriteAllText(payloadBase + ".constants.txt", MoonVeilConstantAnalyzer.BuildReport(prototypes));
            reconstructedMain = true;
            Console.WriteLine($"decoded payload: {payload.Stage1.Length:N0} -> {payload.Stage2.Length:N0} bytes, {payload.Strings.Count:N0} tagged strings");
            Console.WriteLine($"prototype parser: {prototypes.Count:N0} record(s)");
            Console.WriteLine($"reconstruct: {decompiled.Quality}");
            Console.WriteLine($"  {baseOutput}");
            Console.WriteLine($"  {payloadBase}.prototypes.txt");
            Console.WriteLine($"  {payloadBase}.disasm.txt");
            Console.WriteLine($"  {payloadBase}.cfg.txt");
            Console.WriteLine($"  {payloadBase}.opcodes.txt");
            Console.WriteLine($"  {payloadBase}.headers.txt");
            Console.WriteLine($"  {payloadBase}.constants.txt");
        }
        else
        {
            Console.WriteLine($"decoded payload: {payload.Stage1.Length:N0} -> {payload.Stage2.Length:N0} bytes, {payload.Strings.Count:N0} tagged strings");
            Console.WriteLine("Prototype parser: no supported record layout detected; wrapper fallback will be written");
        }
    }
    else
    {
        Console.WriteLine("payload extractor: no supported moonveil base85/lz detected"); // if not moonveil
    }

    if (mode is "both" or "aggressive")
    {
        Console.WriteLine("aggressive wrapper cleanup...");
        var result = new MoonVeilDeobfuscatorEngine(source, aggressive: true).Deobfuscate();
        var wrapperOutput = reconstructedMain
            ? Path.Combine(Path.GetDirectoryName(baseOutput) ?? Environment.CurrentDirectory, Path.GetFileNameWithoutExtension(baseOutput) + ".wrapper" + Path.GetExtension(baseOutput))
            : baseOutput;
        File.WriteAllText(wrapperOutput, result.Source);
        File.WriteAllText(wrapperOutput + ".diagnostics.txt", result.Diagnostics);
        File.WriteAllText(wrapperOutput + ".states.txt", MoonVeilStateMap.Build(result.Source));
        Console.WriteLine($"  {wrapperOutput}");
        Console.WriteLine($"  {result.ChangeCount:N0} rewrite(s)");
    }

    if (mode is "both" or "safe")
    {
        var safeOutput = mode == "safe"
            ? baseOutput
            : Path.Combine(
                Path.GetDirectoryName(baseOutput) ?? Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(baseOutput)}.safe{Path.GetExtension(baseOutput)}");

        Console.WriteLine("Safe cleanup...");
        var safe = new MoonVeilDeobfuscatorEngine(source, aggressive: false).Deobfuscate();
        File.WriteAllText(safeOutput, safe.Source);
        File.WriteAllText(safeOutput + ".diagnostics.txt", safe.Diagnostics);
        File.WriteAllText(safeOutput + ".states.txt", MoonVeilStateMap.Build(safe.Source));
        Console.WriteLine($"  {safeOutput}");
        Console.WriteLine($"  {safe.ChangeCount:N0} rewrite(s)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("deobfuscation failed");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ex.ToString());
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("MoonVeil Deobfuscator");
    Console.WriteLine("Usage: MoonVeilDeobfuscator <input.lua> [output.lua] [--safe|--aggressive]");
    Console.WriteLine();
    Console.WriteLine("default: extracts the decoded MoonVeil payload, for safe & states..");
    Console.WriteLine("self-test: MoonVeilDeobfuscator --self-test");
}
