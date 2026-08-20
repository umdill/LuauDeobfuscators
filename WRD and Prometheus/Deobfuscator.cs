namespace WRDDeobfuscator;

internal static class Deobfuscator
{
    public static DeobfuscationResult Run(string source, Action<string>? status = null)
    {
        status?.Invoke("[1/3] peeling...");

        StaticPeelResult peeled;
        try
        {
            peeled = StaticPeeler.Run(source);
            status?.Invoke($"      decoded {peeled.Strings.Count} strings, resolved {peeled.Lookups} lookups");
        }
        catch (Exception ex)
        {
            status?.Invoke($"      partial static peel: {ex.Message}");
            status?.Invoke("      continuing from the original vm");
            peeled = new StaticPeelResult(source, new List<string>(), 0, 0);
        }

        status?.Invoke("[2/3] vm devirtualization");
        VmTraceResult trace = VmTracer.TryRun(source, peeled.Source, status);

        bool recovered = trace.PayloadOperations > 0 && !string.IsNullOrWhiteSpace(trace.Source);
        bool structural = recovered && trace.RecoveredFunctions > 0;

        status?.Invoke("[3/3] writing reconstructed lua (not 100000000000000000000% perfect)...");

        string final;
        if (recovered)
        {
            final = trace.Source;
        }
        else
        {
            final = "-- recovery incomplete; see _deob_debug\n\n" + peeled.Source; // oh god
        }

        return new DeobfuscationResult(
            peeled.Source,
            final,
            trace.TraceText,
            peeled.Strings.Count,
            peeled.Lookups,
            peeled.Folds,
            trace.EventCount,
            recovered,
            structural,
            trace.RecoveredFunctions);
    }
}
