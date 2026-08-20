using System.Globalization;

namespace MoonVeilDeobfuscator;
internal static class LuaTokenSimplifier
{
    private static readonly HashSet<string> BoundaryWords = new(StringComparer.Ordinal)
    {
        "then", "do", "else", "elseif", "end", "until"
    };

    public static string Simplify(string source, bool repairLegacy, out int changes)
    {
        changes = 0;
        var tokens = LuaLex.Tokenize(source)
            .Where(t => t.Kind is not LuaTokenKind.Whitespace and not LuaTokenKind.NewLine)
            .ToList();

        if (repairLegacy)
            RepairLegacyInvalidBooleanArithmetic(tokens, ref changes);

        for (var round = 0; round < 64; round++)
        {
            var before = changes;
            SimplifyUnaryNot(tokens, ref changes);
            SimplifyDuplicateLiteralOr(tokens, ref changes);
            SimplifyStandaloneShortCircuit(tokens, ref changes);
            if (changes == before) break;
        }

        return Render(tokens);
    }
    private static void RepairLegacyInvalidBooleanArithmetic(List<LuaToken> t, ref int changes)
    {
        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (!IsWord(t[i], "false") && !IsWord(t[i], "nil")) continue;
            if (!IsStandaloneOperandStart(t, i)) continue;
            if (t[i + 1].Text is not ("+" or "-" or "*" or "/" or "%")) continue;

            var orIndex = FindTopLevelWord(t, i + 2, "or");
            if (orIndex < 0) continue;

            t.RemoveRange(i + 1, orIndex - i - 1);
            changes++;
        }
    }

    private static void SimplifyUnaryNot(List<LuaToken> t, ref int changes)
    {
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (!IsWord(t[i], "not")) continue;
            if (IsWord(t[i + 1], "true"))
            {
                t[i] = new LuaToken(LuaTokenKind.Identifier, "false");
                t.RemoveAt(i + 1);
                changes++;
            }
            else if (IsWord(t[i + 1], "false") || IsWord(t[i + 1], "nil"))
            {
                t[i] = new LuaToken(LuaTokenKind.Identifier, "true");
                t.RemoveAt(i + 1);
                changes++;
            }
        }
    }

    private static void SimplifyDuplicateLiteralOr(List<LuaToken> t, ref int changes)
    {
        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (!IsLiteralAtom(t[i]) || !IsWord(t[i + 1], "or") || !SameLiteral(t[i], t[i + 2])) continue;
            if (!IsStandaloneOperandStart(t, i)) continue;
            t.RemoveRange(i + 1, 2);
            changes++;
        }
    }

    private static void SimplifyStandaloneShortCircuit(List<LuaToken> t, ref int changes)
    {
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (!IsStandaloneOperandStart(t, i)) continue;

            if ((IsWord(t[i], "false") || IsWord(t[i], "nil")) && IsWord(t[i + 1], "or"))
            {
                t.RemoveRange(i, 2);
                changes++;
                i = Math.Max(-1, i - 2);
                continue;
            }

            if (IsDefinitelyTruthyAtom(t[i]) && IsWord(t[i + 1], "and"))
            {
                t.RemoveRange(i, 2);
                changes++;
                i = Math.Max(-1, i - 2);
                continue;
            }

            if ((IsWord(t[i], "false") || IsWord(t[i], "nil")) && IsWord(t[i + 1], "and"))
            {
                var orIndex = FindTopLevelWord(t, i + 2, "or");
                if (orIndex >= 0)
                {
                    t.RemoveRange(i, orIndex - i + 1);
                    changes++;
                    i = Math.Max(-1, i - 2);
                    continue;
                }
            }

            // "797 or 740-E" without touching "x + 797 or ...".
            if (IsDefinitelyTruthyAtom(t[i]) && IsWord(t[i + 1], "or"))
            {
                var end = FindExpressionBoundary(t, i + 2);
                if (end > i + 1)
                {
                    t.RemoveRange(i + 1, end - (i + 1));
                    changes++;
                }
            }
        }
    }

    private static int FindTopLevelWord(List<LuaToken> t, int start, string word)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        for (var i = start; i < t.Count; i++)
        {
            var s = t[i].Text;
            if (s == "(") paren++;
            else if (s == ")") { if (paren == 0) return -1; paren--; }
            else if (s == "[") bracket++;
            else if (s == "]") { if (bracket == 0) return -1; bracket--; }
            else if (s == "{") brace++;
            else if (s == "}") { if (brace == 0) return -1; brace--; }

            if (paren == 0 && bracket == 0 && brace == 0)
            {
                if (IsWord(t[i], word)) return i;
                if (IsHardBoundary(t[i])) return -1;
            }
        }
        return -1;
    }

    // exclusive rhs exp
    private static int FindExpressionBoundary(List<LuaToken> t, int start)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        for (var i = start; i < t.Count; i++)
        {
            var s = t[i].Text;
            if (s == "(") paren++;
            else if (s == ")") { if (paren == 0) return i; paren--; }
            else if (s == "[") bracket++;
            else if (s == "]") { if (bracket == 0) return i; bracket--; }
            else if (s == "{") brace++;
            else if (s == "}") { if (brace == 0) return i; brace--; }

            if (paren == 0 && bracket == 0 && brace == 0 && IsHardBoundary(t[i])) return i;
        }
        return t.Count;
    }

    private static bool IsHardBoundary(LuaToken token) =>
        token.Text is "," or ";" ||
        (token.Kind == LuaTokenKind.Identifier && BoundaryWords.Contains(token.Text));

    private static bool IsStandaloneOperandStart(List<LuaToken> t, int i)
    {
        if (i == 0) return true;
        var p = t[i - 1];
        if (p.Text is "=" or "," or ";" or "(" or "{" or "[" or ":" or "return") return true;
        if (p.Kind == LuaTokenKind.Identifier && p.Text is "return" or "then" or "do" or "else" or "elseif" or "and" or "or") return true;
        return false;
    }

    private static bool IsDefinitelyTruthyAtom(LuaToken t) =>
        t.Kind is LuaTokenKind.Number or LuaTokenKind.String or LuaTokenKind.LongString || IsWord(t, "true");

    private static bool IsLiteralAtom(LuaToken t) =>
        IsDefinitelyTruthyAtom(t) || IsWord(t, "false") || IsWord(t, "nil");

    private static bool SameLiteral(LuaToken a, LuaToken b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Kind == LuaTokenKind.Number &&
            MoonVeilMath.TryParseLuaInteger(a.Text, out var ai) &&
            MoonVeilMath.TryParseLuaInteger(b.Text, out var bi)) return ai == bi;
        return string.Equals(a.Text, b.Text, StringComparison.Ordinal);
    }

    private static bool IsWord(LuaToken t, string word) =>
        t.Kind == LuaTokenKind.Identifier && string.Equals(t.Text, word, StringComparison.Ordinal);

    private static string Render(List<LuaToken> tokens)
    {
        var sb = new System.Text.StringBuilder();
        LuaToken? prev = null;
        foreach (var token in tokens)
        {
            if (prev is { } p && NeedsSpace(p, token)) sb.Append(' ');
            sb.Append(token.Text);
            prev = token;
        }
        return sb.ToString();
    }

    private static bool NeedsSpace(LuaToken p, LuaToken c)
    {
        var pWord = p.Kind is LuaTokenKind.Identifier or LuaTokenKind.Number or LuaTokenKind.String or LuaTokenKind.LongString;
        var cWord = c.Kind is LuaTokenKind.Identifier or LuaTokenKind.Number or LuaTokenKind.String or LuaTokenKind.LongString;
        if (pWord && cWord) return true;
        if ((p.Text is ")" or "]" or "}") && cWord) return true;
        if (pWord && c.Text == "{") return true;
        return false;
    }
}
