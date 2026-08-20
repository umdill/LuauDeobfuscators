using ZeroLuaDeobfuscator;

if (args.Length is < 1 or > 2)
{
    PrintUsage();
    return 1;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"input file not found: {inputPath}");
        return 1;
    }

    var outputPath = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(
            Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
            $"{Path.GetFileNameWithoutExtension(inputPath)}.deobfuscated.lua");

    var source = File.ReadAllText(inputPath);
    var deobfuscated = new ZeroLua52(source).Deobfuscate();

    File.WriteAllText(outputPath, deobfuscated);
    Console.WriteLine($"output: {outputPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Zero Lua Deobfuscator");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  ZeroLuaDeobfuscator <input.lua> [output.lua]");
    Console.WriteLine();
    Console.WriteLine("You can also drag a lua file onto run.bat or the  exe once you build it.");
}
