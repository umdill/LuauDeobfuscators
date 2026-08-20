using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

public sealed class MoonVeilDeobfuscatorEngine
{
    private string _source;
    private readonly List<string> _notes = new();
    private int _changes;
    private MoonVeilProfile? _profile;
    private readonly bool _aggressive;
    private readonly Dictionary<string, string> _cacheValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cacheConflicts = new(StringComparer.Ordinal);
    private int _maxCacheFacts;
    private int _maxCacheConflicts;
    private const string Num = @"[-+]?(?:(?:0[xX][0-9A-Fa-f]+)|(?:0[bB][01]+)|(?:\d+(?:\.\d+)?(?:[eE][+-]?\d+)?))";

    public MoonVeilDeobfuscatorEngine(string source, bool aggressive = true)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _aggressive = aggressive;
    }

    public static string DetectVersion(string source)
    {
        var match = Regex.Match(source, @"MoonVeil\s+([0-9]+(?:\.[0-9]+){1,4})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    public DeobfuscationResult Deobfuscate()
    {
        Run("normalize line endings", NormalizeLineEndings);
        Run("repair legacy token-glue artifacts", RepairLegacyTokenGlue);
        Run("token-safe boolean repair", SimplifyBooleanTokens);
        Run("normalize binary integer literals", NormalizeBinaryLiterals);

        _profile = MoonVeilProfile.Analyze(_source);
        _notes.Add($"detected cache helper(s): {string.Join(", ", _profile.CacheHelpers.Select(x => $"{x.Name}:{x.Operation}"))}");
        _notes.Add($"mode: {(_aggressive ? "aggressive MoonVeil cleanup" : "safe generic cleanup")}");

        for (var round = 1; round <= 24; round++)
        {
            var before = _changes;
            Run($"round {round}: resolve arithmetic caches", EvaluateMoonVeilMathCaches);
            Run($"round {round}: propagate known cache values", PropagateKnownCacheValues);
            Run($"round {round}: remove resolved cache fallbacks", RemoveResolvedCacheFallbacks);
            Run($"round {round}: fold numeric expressions", FoldConstantArithmetic);
            Run($"round {round}: fold numeric comparisons", FoldConstantComparisons);
            Run($"round {round}: simplify literal boolean chains", SimplifyBooleanTokens);

            if (_aggressive)
            {
                Run($"round {round}: simplify MoonVeil opaque predicates", SimplifyOpaquePredicates);
                Run($"round {round}: simplify literal if branches", SimplifyConstantIfBranches);
            }

            if (_changes == before) break;
        }

        _notes.Add("proxy rewrite: skipped automatically (mutable MoonVeil wrapper tables are preserved unless a future scope-aware pass proves them safe)");

        for (var round = 1; round <= 12; round++)
        {
            var before = _changes;
            Run($"cleanup {round}: fold arithmetic", FoldConstantArithmetic);
            Run($"cleanup {round}: fold comparisons", FoldConstantComparisons);
            Run($"cleanup {round}: simplify boolean chains", SimplifyBooleanTokens);
            if (_aggressive) Run($"cleanup {round}: simplify literal branches", SimplifyConstantIfBranches);
            if (_changes == before) break;
        }

        Run("canonicalize numeric literals", CanonicalizeNumericLiterals);
        Run("normalize harmless syntax noise", NormalizeHarmlessSyntaxNoise);
        Run("lexer-backed format", FormatLua);
        Run("clean whitespace", CleanupText);

        var tokenCheck = LuaLex.HasSuspiciousGlue(_source, out var glueSamples);
        if (tokenCheck)
        {
            _notes.Add("WARNING: suspicious token glue remains after formatting");
            foreach (var sample in glueSamples) _notes.Add("  glue: " + sample);
        }
        else
        {
            _notes.Add("token-glue check: passed");
        }

        AddPayloadDiagnostics();
        foreach (var line in LuaSanityChecker.Check(_source)) _notes.Add(line);
        foreach (var line in MoonVeilDispatcherAnalyzer.Analyze(_source)) _notes.Add(line);

        var diagnostics = new StringBuilder();
        diagnostics.AppendLine($"MoonVeil version: {DetectVersion(_source)}");
        diagnostics.AppendLine($"Total rewrites: {_changes}");
        diagnostics.AppendLine();
        foreach (var note in _notes) diagnostics.AppendLine(note);

        return new DeobfuscationResult(_source.Trim() + Environment.NewLine, diagnostics.ToString(), _changes);
    }

    private void Run(string name, Action pass)
    {
        var beforeChanges = _changes;
        var beforeSource = _source;
        var beforeRisk = LuaIntegrity.Score(_source);
        pass();
        var afterRisk = LuaIntegrity.Score(_source);
        if (afterRisk > beforeRisk)
        {
            _source = beforeSource;
            _changes = beforeChanges;
            _notes.Add($"{name}: rolled back (integrity score {beforeRisk} -> {afterRisk})");
            return;
        }
        _notes.Add($"{name}: {_changes - beforeChanges} rewrite(s)");
    }

    private void NormalizeLineEndings() => _source = _source.Replace("\r\n", "\n").Replace('\r', '\n');


    private void RepairLegacyTokenGlue()
    {
        if (!LooksLikeMoonVeil(_source)) return;
        // v3 could partially match the decimal prefix of a hex literal, producing text such as
        // falsex3210 or 797or. repairs invalid-token
        _source = LuaText.TransformCode(_source, code =>
        {
            var before = code;
            code = Regex.Replace(code, @"\b(?<b>true|false)x(?<h>[0-9A-Fa-f]+)\b", "${b} and 0x${h}");
            code = Regex.Replace(code, @"(?<n>\d)(?<kw>and|or|then|do|repeat|end|else|elseif)\b", "${n} ${kw}");
            code = Regex.Replace(code, @"\b(?<a>true|false|nil)(?<kw>and|or)\b", "${a} ${kw}");
            if (code != before) _changes++;
            return code;
        });
    }

    private void NormalizeBinaryLiterals()
    {
        _source = LuaText.TransformCode(_source, code => Regex.Replace(code, @"(?<![\w])(?<s>[-+]?)0[bB](?<b>[01]+)(?![\w])", m =>
        {
            try
            {
                var value = Convert.ToInt64(m.Groups["b"].Value, 2);
                if (m.Groups["s"].Value == "-") value = -value;
                _changes++;
                return value.ToString(CultureInfo.InvariantCulture);
            }
            catch { return m.Value; }
        }));
    }

    private void EvaluateMoonVeilMathCaches()
    {
        if (!_aggressive || _profile is null) return;

        _cacheValues.Clear();
        _cacheConflicts.Clear();
        foreach (var helper in _profile.CacheHelpers) CollectCacheFacts(helper);
        _maxCacheFacts = Math.Max(_maxCacheFacts, _cacheValues.Count);
        _maxCacheConflicts = Math.Max(_maxCacheConflicts, _cacheConflicts.Count);
        foreach (var helper in _profile.CacheHelpers) ReplaceCacheHelper(helper);
    }

    private void CollectCacheFacts(CacheHelper helper)
    {
        var colonCall = $@"(?<obj>[A-Za-z_]\w*):{Regex.Escape(helper.Name)}\(\s*(?<x>{Num})\s*,\s*(?<y>{Num})\s*,\s*(?<key>{Num})\s*\)";
        var dotCall = $@"(?<obj>[A-Za-z_]\w*)\.{Regex.Escape(helper.Name)}\(\s*\k<obj>\s*,\s*(?<x>{Num})\s*,\s*(?<y>{Num})\s*,\s*(?<key>{Num})\s*\)";

        void Scan(string pattern)
        {
            foreach (Match m in Regex.Matches(_source, pattern))
            {
                if (!TryEvaluateCache(helper, m.Groups["x"].Value, m.Groups["y"].Value, out var value)) continue;
                var cacheKey = CacheKey(m.Groups["obj"].Value, m.Groups["key"].Value);
                if (_cacheValues.TryGetValue(cacheKey, out var old) && old != value)
                    _cacheConflicts.Add(cacheKey);
                else
                    _cacheValues[cacheKey] = value;
            }
        }

        Scan(colonCall);
        Scan(dotCall);
    }

    private void ReplaceCacheHelper(CacheHelper helper)
    {
        var colonCall = $@"(?<obj>[A-Za-z_]\w*):{Regex.Escape(helper.Name)}\(\s*(?<x>{Num})\s*,\s*(?<y>{Num})\s*,\s*(?<key>{Num})\s*\)";
        var dotCall = $@"(?<obj>[A-Za-z_]\w*)\.{Regex.Escape(helper.Name)}\(\s*\k<obj>\s*,\s*(?<x>{Num})\s*,\s*(?<y>{Num})\s*,\s*(?<key>{Num})\s*\)";

        string Eval(Match m)
        {
            var cacheKey = CacheKey(m.Groups["obj"].Value, m.Groups["key"].Value);
            if (_cacheConflicts.Contains(cacheKey) || !_cacheValues.TryGetValue(cacheKey, out var value)) return m.Value;
            _changes++;
            return " " + value + " ";
        }

        _source = LuaText.TransformCode(_source, code => Regex.Replace(Regex.Replace(code, colonCall, Eval), dotCall, Eval));
    }

    private void PropagateKnownCacheValues()
    {
        if (!_aggressive || _cacheValues.Count == 0) return;
        var rx = new Regex($@"(?<obj>[A-Za-z_]\w*)\.H\[\s*(?<key>{Num})\s*\](?!\s*=)");
        _source = LuaText.TransformCode(_source, code => rx.Replace(code, m =>
        {
            var cacheKey = CacheKey(m.Groups["obj"].Value, m.Groups["key"].Value);
            if (_cacheConflicts.Contains(cacheKey) || !_cacheValues.TryGetValue(cacheKey, out var value)) return m.Value;
            _changes++;
            return " " + value + " ";
        }));
    }

    private static string CacheKey(string obj, string key)
    {
        if (MoonVeilMath.TryParseLuaInteger(key, out var n)) return obj + ":" + n.ToString(CultureInfo.InvariantCulture);
        return obj + ":" + key.Trim();
    }

    private static bool TryEvaluateCache(CacheHelper helper, string xText, string yText, out string valueText)
    {
        valueText = string.Empty;
        if (!TryNumber(xText, out var x) || !TryNumber(yText, out var y)) return false;
        if (Math.Abs(x - Math.Round(x)) > 1e-12 || Math.Abs(y - Math.Round(y)) > 1e-12) return false;
        var value = helper.Operation switch
        {
            CacheOp.SubXor => x - MoonVeilMath.Xor((long)y, helper.Constant),
            CacheOp.XorDiv => y == 0 ? double.NaN : MoonVeilMath.Xor((long)x, helper.Constant) / y,
            CacheOp.XorAdd => MoonVeilMath.Xor((long)x, helper.Constant) + y,
            _ => double.NaN
        };
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        valueText = MoonVeilMath.Format(value);
        return true;
    }

    private void RemoveResolvedCacheFallbacks()
    {
        if (!_aggressive) return;
        // removes literals or [H]key
        var rx = new Regex(
            @"\b[A-Za-z_]\w*\.H\[\s*" + Num +
            @"\s*\]\s*or\s*\(?\s*(?<v>" + Num +
            @")\s*\)?(?=\s*(?:[,;)\]}]|\b(?:and|or|then|else|elseif|end|do)\b|$))");
        _source = LuaText.TransformCode(_source, code => rx.Replace(code, m =>
        {
            _changes++;
            return " " + m.Groups["v"].Value + " ";
        }));
    }

    private void FoldConstantArithmetic()
    {
        // prevents 0x3210->0
        var rx = new Regex($@"(?<![\w.])(?<a>{Num})\s*(?<op>[+\-*/%])\s*(?<b>{Num})(?![\w.])");
        for (var round = 0; round < 96; round++)
        {
            var changed = false;
            _source = LuaText.TransformCode(_source, code => rx.Replace(code, m =>
            {
                if (!TryNumber(m.Groups["a"].Value, out var a) || !TryNumber(m.Groups["b"].Value, out var b)) return m.Value;
                var value = m.Groups["op"].Value switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" when b != 0 => a / b,
                    "%" when b != 0 => a - Math.Floor(a / b) * b,
                    _ => double.NaN
                };
                if (double.IsNaN(value) || double.IsInfinity(value)) return m.Value;
                changed = true;
                _changes++;
                return " " + MoonVeilMath.Format(value) + " ";
            }));
            if (!changed) break;
        }
    }

    private void FoldConstantComparisons()
    {
        var rx = new Regex($@"(?<![\w.])(?<a>{Num})\s*(?<op><=|>=|==|~=|<|>)\s*(?<b>{Num})(?![\w.])");
        _source = LuaText.TransformCode(_source, code => rx.Replace(code, m =>
        {
            if (!TryNumber(m.Groups["a"].Value, out var a) || !TryNumber(m.Groups["b"].Value, out var b)) return m.Value;
            var result = m.Groups["op"].Value switch
            {
                "<" => a < b,
                ">" => a > b,
                "<=" => a <= b,
                ">=" => a >= b,
                "==" => a == b,
                "~=" => a != b,
                _ => false
            };
            _changes++;
            return result ? " true " : " false ";
        }));
    }

    private void SimplifyOpaquePredicates()
    {
        if (!LooksLikeMoonVeil(_source)) return;
        var same = new Regex(@"\b(?<v>[A-Za-z_]\w*)\s*(?<op>~=|==)\s*\k<v>\b");
        _source = LuaText.TransformCode(_source, code => same.Replace(code, m =>
        {
            _changes++;
            return m.Groups["op"].Value == "~=" ? " false " : " true ";
        }));
    }


    private void SimplifyBooleanTokens()
    {
        var simplified = LuaTokenSimplifier.Simplify(_source, repairLegacy: _aggressive, out var count);
        if (count <= 0) return;
        _source = simplified;
        _changes += count;
    }

    private void SimplifyBooleanNoise()
    {
        var literal = $@"(?:{Num}|true|false|nil)";
        for (var i = 0; i < 24; i++)
        {
            var before = _changes;
            _source = LuaText.TransformCode(_source, code =>
            {
                code = Regex.Replace(code, @"\bnot\s+true\b", _ => { _changes++; return " false "; });
                code = Regex.Replace(code, @"\bnot\s+false\b", _ => { _changes++; return " true "; });

                code = Regex.Replace(code, $@"\bfalse\s+and\s+(?<x>{literal})(?![\w])", _ => { _changes++; return " false "; });
                code = Regex.Replace(code, $@"\bfalse\s+or\s+(?<x>{literal})(?![\w])", m => { _changes++; return " " + m.Groups["x"].Value + " "; });
                code = Regex.Replace(code, $@"\btrue\s+and\s+(?<x>{literal})(?![\w])", m => { _changes++; return " " + m.Groups["x"].Value + " "; });
                code = Regex.Replace(code, $@"\btrue\s+or\s+(?<x>{literal})(?![\w])", _ => { _changes++; return " true "; });


                code = Regex.Replace(code, $@"\btrue\s+and\s+(?<a>{Num}|true)\s+or\s+(?<b>{literal})(?![\w])", m => { _changes++; return " " + m.Groups["a"].Value + " "; });
                code = Regex.Replace(code, $@"\bfalse\s+and\s+(?<a>{literal})\s+or\s+(?<b>{literal})(?![\w])", m => { _changes++; return " " + m.Groups["b"].Value + " "; });
                code = Regex.Replace(code, $@"\bnil\s+and\s+(?<a>{literal})\s+or\s+(?<b>{literal})(?![\w])", m => { _changes++; return " " + m.Groups["b"].Value + " "; });
                return code;
            });
            if (_changes == before) break;
        }
    }

    private void SimplifyConstantIfBranches()
    {
        // never crossing nested (prune)
        for (var round = 0; round < 48; round++)
        {
            var before = _changes;
            _source = LuaText.TransformCode(_source, code =>
            {
                var withElse = new Regex(@"\bif\s+(?<c>true|false)\s+then\s+(?<a>(?:(?!\bif\b|\bend\b).)*?)\s+else\s+(?<b>(?:(?!\bif\b|\bend\b).)*?)\s+end\b", RegexOptions.Singleline);
                code = withElse.Replace(code, m => { _changes++; return m.Groups["c"].Value == "true" ? " " + m.Groups["a"].Value + " " : " " + m.Groups["b"].Value + " "; });
                var noElse = new Regex(@"\bif\s+(?<c>true|false)\s+then\s+(?<a>(?:(?!\bif\b|\bend\b).)*?)\s+end\b", RegexOptions.Singleline);
                code = noElse.Replace(code, m => { _changes++; return m.Groups["c"].Value == "true" ? " " + m.Groups["a"].Value + " " : " "; });
                return code;
            });
            if (_changes == before) break;
        }
    }

    private void CanonicalizeNumericLiterals()
    {
        _source = LuaText.TransformCode(_source, code => Regex.Replace(code, @"(?<![\w])(?<s>[+-]?)0[xX](?<h>[0-9A-Fa-f]+)(?![\w])", m =>
        {
            if (!ulong.TryParse(m.Groups["h"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)) return m.Value;
            var v = unchecked((long)u);
            if (m.Groups["s"].Value == "-") v = -v;
            _changes++;
            return v.ToString(CultureInfo.InvariantCulture);
        }));
    }

    private void NormalizeHarmlessSyntaxNoise()
    {
        _source = LuaText.TransformCode(_source, code =>
        {
            var before = code;
            code = Regex.Replace(code, @"\+\s*-\s*(?<n>\d)", "-${n}");
            code = Regex.Replace(code, @";{2,}", ";");
            if (code != before) _changes++;
            return code;
        });
    }

    private void FormatLua() => _source = LuaPrettyPrinter.Format(_source);

    private static bool TryNumber(string text, out double value)
    {
        if (MoonVeilMath.TryParseLuaInteger(text, out var i)) { value = i; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool LooksLikeMoonVeil(string source) =>
        source.Contains("MoonVeil", StringComparison.OrdinalIgnoreCase) ||
        (source.Contains(".H[", StringComparison.Ordinal) && source.Contains("bit32", StringComparison.Ordinal));

    private void AddPayloadDiagnostics()
    {
        var payload = Regex.Match(_source, "\\.k\\s*\"(?<p>[^\"]{100,})\"", RegexOptions.Singleline);
        if (payload.Success) _notes.Add($"encoded payload blob: {payload.Groups["p"].Value.Length:N0} characters");
        _notes.Add($"remaining cache references: {Regex.Matches(_source, @"\.H\[").Count}");
        _notes.Add($"remaining proxy dereferences: {Regex.Matches(_source, @"\[2\]\s*\[\s*[A-Za-z_]\w*\s*\[1\]\s*\]").Count}");
        _notes.Add($"remaining flattened loops: {Regex.Matches(_source, @"\b(?:while\s+true\s+do|repeat)\b", RegexOptions.IgnoreCase).Count}");
        _notes.Add($"cache facts discovered: {_maxCacheFacts}, conflicting cache keys: {_maxCacheConflicts}");
        _notes.Add($"remaining helper calls: {(_profile is null ? 0 : _profile.CacheHelpers.Sum(h => Regex.Matches(_source, $@":{Regex.Escape(h.Name)}\(").Count))}");
    }

    private void CleanupText()
    {
        _source = Regex.Replace(_source, @"[ \t]+\n", "\n");
        _source = Regex.Replace(_source, @"\n{3,}", "\n\n");
    }
}
