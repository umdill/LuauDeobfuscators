namespace MoonVeilDeobfuscator;

internal static class MoonVeilSourceReconstructor
{
    public static string Reconstruct(byte[] payload, IReadOnlyList<MoonVeilPrototype> prototypes, out string quality)
    {
        var output = MoonVeilDecompilerPipeline.Decompile(payload, prototypes);
        quality = output.Quality;
        return output.Source;
    }
}
