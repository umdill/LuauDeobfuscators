using System.Text;

namespace WRDDeobfuscator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Prometheus Deobfuscator";

        Console.WriteLine("WRD / Prometheus Deobfuscator - C# / .NET 8");
        Console.WriteLine("WRD / Prometheus VM deobfuscator");
        Console.WriteLine();

        bool debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

        string[] files = ResolveInputs(args);
        if (files.Length == 0)
        {
            Console.WriteLine("Drag a .lua, .luau, or .txt file onto WRD-Deobfuscator.exe");
            Console.Write("Or paste a path here: ");
            string? line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line))
                files = ResolveInputs([line.Trim().Trim('"')]);
        }

        if (files.Length == 0)
        {
            Console.WriteLine("no input files found !?");
            PauseIfInteractive(args);
            return 1;
        }

        int failures = 0;
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            Console.WriteLine($"[{i + 1}/{files.Length}] {Path.GetFileName(file)}");
            try
            {
                string source = File.ReadAllText(file);
                var result = Deobfuscator.Run(source, msg => Console.WriteLine($"  {msg}"));

                string output = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(file))!,
                    Path.GetFileNameWithoutExtension(file) + "_deobfuscated.lua");

                File.WriteAllText(output, result.FinalSource, new UTF8Encoding(false));

                string debugDir = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(file))!,
                    Path.GetFileNameWithoutExtension(file) + "_deob_debug");

                if (debug || !result.DeterministicRecovered)
                {
                    Directory.CreateDirectory(debugDir);
                    File.WriteAllText(Path.Combine(debugDir, "01_static.lua"), result.StaticSource, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(debugDir, "02_trace.txt"), result.TraceText, new UTF8Encoding(false));
                }

                Console.WriteLine($"  decoded: {result.DecodedStrings} strings, {result.ResolvedLookups} lookups");
                Console.WriteLine($"  functions: {result.RecoveredFunctions}");
                Console.WriteLine($"  recovery: {(result.StructuralRecovered ? "structured" : result.DeterministicRecovered ? "runtime" : "incomplete")}");
                Console.WriteLine($"  Saved: {Path.GetFileName(output)}");
                if (!result.DeterministicRecovered)
                    Console.WriteLine($"  debug saved: {Path.GetFileName(debugDir)}\\02_trace.txt");
            }
            catch (Exception ex)
            {
                failures++; // gg this shouldnt happen btw so its the script
                Console.WriteLine($"  failed: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine(failures == 0 ? "finished." : $"finished with {failures} failure(s)."); //plsnofail
        PauseIfInteractive(args);
        return failures == 0 ? 0 : 2;
    }

    private static string[] ResolveInputs(string[] args)
    {
        var result = new List<string>();
        foreach (string raw in args)
        {
            if (raw.StartsWith("--", StringComparison.Ordinal)) continue;
            string p = raw.Trim().Trim('"');
            if (File.Exists(p))
            {
                if (IsInput(p)) result.Add(Path.GetFullPath(p));
            }
            else if (Directory.Exists(p))
            {
                result.AddRange(Directory.EnumerateFiles(p)
                    .Where(IsInput)
                    .Select(Path.GetFullPath));
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsInput(string p)
        => Path.GetExtension(p).ToLowerInvariant() is ".lua" or ".luau" or ".txt"; // extgension dectection

    private static void PauseIfInteractive(string[] args)
    {
        if (args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase)) return;
        if (OperatingSystem.IsWindows())
        {
            Console.Write("press enter to close");
            try { Console.ReadLine(); } catch { }
        }
    }
}
