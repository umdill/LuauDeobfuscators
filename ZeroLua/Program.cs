using ZeroLuaDeobfuscator;

if (args.Length is < 1 or > 2)
{
    PrintUsage();
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"input file not found at {inputPath}");
    return 1;
}

var outputPath = args.Length == 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(
        Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
        $"{Path.GetFileNameWithoutExtension(inputPath)}.deobfuscated.lua");
var diagnosticsPath = outputPath + ".diagnostics.txt"; // for diagnostics 

try
{
    Console.WriteLine("Getting input");
    var source = File.ReadAllText(inputPath);
    var version = DetectVersion(source);
    Console.WriteLine($"Detected ZeroLua: {VersionOrUnknown(version)}");
    Console.WriteLine("Deobfuscating...");

    string deobfuscated = version switch
    {
        "7.0" => ZeroLua70.Deobfuscate(source),
        "5.6" => new ZeroLua56(source).Deobfuscate(),
        "5.2" => new ZeroLua52(source).Deobfuscate(),
        _ => TryFallback(source)
    };

    if (string.IsNullOrWhiteSpace(deobfuscated))
        throw new InvalidDataException("decoder failure got nil");

    Console.WriteLine("Post deobf cleanup running, shouldn't take long");
    deobfuscated = LuaCleanup.Run(deobfuscated);
    File.WriteAllText(outputPath, deobfuscated);
    if (File.Exists(diagnosticsPath)) File.Delete(diagnosticsPath);

    Console.WriteLine($"completed - {outputPath}");
    Console.WriteLine($"output size: {deobfuscated.Length:N0}");
    return 0;
}
catch (Exception ex)
{
    var diagnostic = $"deobfuscation failed.\ninput: {inputPath}\noutput: {outputPath}\n\n{ex}\n";
    try { File.WriteAllText(diagnosticsPath, diagnostic); } catch { }

    Console.Error.WriteLine();
    Console.Error.WriteLine("deobfuscation failed.");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine($"diagnostics: {diagnosticsPath}");
    return 2;
}

static string DetectVersion(string source)
{ // version detection
    if (source.Contains("ZERO LUA V7.0", StringComparison.OrdinalIgnoreCase)) return "7.0";
    if (source.Contains("ZERO LUA V5.6", StringComparison.OrdinalIgnoreCase)) return "5.6";
    if (source.Contains("ZERO LUA V5.2", StringComparison.OrdinalIgnoreCase)) return "5.2";
    if (source.Contains("local vC=", StringComparison.Ordinal) && source.Contains(";local vD=", StringComparison.Ordinal)) return "5.2";
    if (source.Contains("ZeroLua Security Engine", StringComparison.OrdinalIgnoreCase)) return "7.0"; // if banner is removed for 7.0
    return "unknown";
}

static string TryFallback(string source)
{
    var errors = new List<Exception>();

    try
    {
        return ZeroLua70.Deobfuscate(source);
    }
    catch (Exception ex)
    {
        errors.Add(ex);
    }

    try
    {
        return new ZeroLua56(source).Deobfuscate();
    }
    catch (Exception ex)
    {
        errors.Add(ex);
    }

    try
    {
        return new ZeroLua52(source).Deobfuscate();
    }
    catch (Exception ex)
    {
        errors.Add(ex);
    }

    throw new AggregateException(
        "Has to be ZeroLua 7.0, 5.6, or 5.2.",
        errors);
}

static string VersionOrUnknown(string v)
{
    return v == "unknown" ? "auto-detect" : v;
}

static void PrintUsage()
{
    Console.WriteLine("ZeroLua Deobfuscator (5.2, 5.6 and 7.0)");
    Console.WriteLine("Usage: ZeroLuaDeobfuscator <input.lua> [output.lua]");
}