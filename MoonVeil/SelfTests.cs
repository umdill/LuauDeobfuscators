namespace MoonVeilDeobfuscator;
// MoonVeilDeobfuscator --self-test
internal static class SelfTests
{
    public static int Run()
    {
        var failures = new List<string>();

        Test("cache helper + colon self", failures, () =>
        {
            var src = "-- This script was generated using MoonVeil 2.0.21\n" +
                      "return({J=function(a,b,c,d)a.H[d]=b-a.x(c,0x4388)return a.H[d]end,H={},x=bit32.bxor," +
                      "B=function(ma,b)return function()local E E=ma.H[-0x9b6] or ma:J(0x2bb5,0x68cf,-0x9b6) return E end end})";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (!output.Contains("E=110", StringComparison.Ordinal) && !output.Contains("E =110", StringComparison.Ordinal) && !output.Contains("E= 110", StringComparison.Ordinal))
                throw new Exception("expected dispatcher seed 110");
        });

        Test("hex boolean operand is not partially matched", failures, () =>
        {
            var src = "-- MoonVeil 2.0.21\nlocal p=17 p=p~=p and 0x3210/p or 0x30a8/p return p";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (output.Contains("falsex", StringComparison.OrdinalIgnoreCase))
                throw new Exception("created falsex token glue");
        });

        Test("legacy token glue repair", failures, () =>
        {
            var src = "-- MoonVeil 2.0.21\nlocal E=110repeat local p=falsex3210/p or 116 until false";
            var output = new MoonVeilDeobfuscatorEngine(src, false).Deobfuscate().Source;
            if (output.Contains("110repeat", StringComparison.Ordinal) || output.Contains("falsex3210", StringComparison.Ordinal))
                throw new Exception("legacy glue was not repaired");
        });

        Test("false complex ternary folds as a whole", failures, () =>
        {
            var src = "-- MoonVeil 2.0.21\nlocal p=17 p=false and 0x3210/p or 0x30a8/p return p";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (output.Contains("false /", StringComparison.Ordinal) || output.Contains("false/", StringComparison.Ordinal))
                throw new Exception("partially simplified false-and operand");
            if (!output.Contains("12456", StringComparison.Ordinal))
                throw new Exception("expected surviving rhs 12456/p");
        });

        Test("nil comparison ternary is preserved", failures, () =>
        {
            var src = "-- MoonVeil 2.0.21\nlocal E,_ E=_==nil and 234 or 57 return E";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (!output.Contains("nil", StringComparison.Ordinal) || !output.Contains("234", StringComparison.Ordinal) || !output.Contains("57", StringComparison.Ordinal))
                throw new Exception("comparison ternary was incorrectly collapsed");
        });

        Test("truthy cache fallback collapses", failures, () =>
        {
            var src = "-- MoonVeil 2.0.21\nlocal E=110 or 110 repeat E=797 or 740-E until false";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (output.Contains("110 or", StringComparison.Ordinal) || output.Contains("797 or", StringComparison.Ordinal))
                throw new Exception("truthy literal OR fallback remained");
        });

        Test("strings are protected", failures, () =>
        {
            const string marker = "false and 0x3210 73-5 0b1010";
            var src = "-- MoonVeil 2.0.21\nlocal s=\"" + marker + "\" return s";
            var output = new MoonVeilDeobfuscatorEngine(src, true).Deobfuscate().Source;
            if (!output.Contains(marker, StringComparison.Ordinal))
                throw new Exception("quoted string changed");
        });


        Test("payload base85/lz decoder", failures, () =>
        {
            const string sample = "return({f=function(a,b)return a.k \"{{aO601~YxH~#_l9y<aZ9eCaVzylo}6ak<F8vi^dUlc1aC;{IAUlS`a0KpY4{W1W+C;|Z}0|EaUJ}R9%FfFeCG&m6i1PerU|7~q~P;6m&W&d&n1WsXXWd#2QNM&JcbZ7+s1w(IXZgT(w{{aU90UNwG4F78Z5LG8R0=)k>5LGEV1A7bq2s|Ah6rDLS{Vxw8yf+P1COG~O0qO-NDjjPC?+&~-4h6sk2L}Ho00jm*oj5T6E~-L483YFff4nyjRVX?G0;c~wTo5D&1O#opVGdveZDDv22XOClX>N29r66Q!-(_SW1PBwOIw1c5009O80Se0RI|nNY3E%+~4*N10AwU6Z0uIvuLjwdD00sv*v<zhoAOsoY1O*DC2LC~1WMyO^1PALNV;sRQ3BdpZA^$!Y0RjY_H!$xm2LmG*BLn~h`vo}yAt??qDlq>Y9Tg=z93Q;@I1U{j9!M?`_avP<8vsqbISVHp94!Q)0U|sF;R!%%1q29V1_)&b2MVP52nwYifae_}0|@>S<rq_RVRCe7?`~%xWHTWkb7TK<X>fEdE-nP^2sEQQAT;C+Mrn_3VQe5Yq5^<5!vrx70;43QDTM$4\" end})";
            if (!MoonVeilPayloadExtractor.TryExtract(sample, out var x)) throw new Exception("payload not detected");
            if (x.Stage1.Length != 472 || x.Stage2.Length != 560) throw new Exception($"unexpected lengths {x.Stage1.Length}/{x.Stage2.Length}");
            if (x.Strings.Count == 0)
                throw new Exception("expected decoded tagged strings");
            var prototypes = MoonVeilPrototypeParser.Parse(x.Stage2);
            if (prototypes.Count == 0)
                throw new Exception("expected at least one decoded prototype record");
            var decompiled = MoonVeilDecompilerPipeline.Decompile(x.Stage2, prototypes);
            if (decompiled.Prototypes.Count != prototypes.Count)
                throw new Exception("generic decompiler lost prototype records");
            if (!decompiled.Source.Contains("prototype_", StringComparison.Ordinal))
                throw new Exception("generic decompiler did not emit prototype source");
            if (decompiled.Prototypes.Sum(p => p.Instructions.Instructions.Count) == 0)
                throw new Exception("generic instruction decoder emitted no words");
        });

        if (failures.Count == 0)
        {
            Console.WriteLine("st: pass");
            return 0;
        }

        Console.Error.WriteLine("st: fail");
        foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
        return 3;
    }

    private static void Test(string name, List<string> failures, Action body)
    {
        try { body(); }
        catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
    }
}
