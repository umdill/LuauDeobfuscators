namespace WRDDeobfuscator;

internal sealed record DeobfuscationResult(
    string StaticSource,
    string FinalSource,
    string TraceText,
    int DecodedStrings,
    int ResolvedLookups,
    int FoldedExpressions,
    int TraceEvents,
    bool DeterministicRecovered,
    bool StructuralRecovered,
    int RecoveredFunctions);
