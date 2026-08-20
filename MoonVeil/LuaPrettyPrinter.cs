namespace MoonVeilDeobfuscator;

internal static class LuaPrettyPrinter
{
    public static string Format(string source) => LuaLex.RepairAndFormat(source);
}
