using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

internal static class LuaIntegrity
{
    public static int Score(string source)
    {
        var code = LuaText.CodeOnly(source);
        var score = 0;
        score += Regex.Matches(code, @"\b(?:false|true|nil)\s*[+\-*/%]").Count * 25;
        score += Regex.Matches(code, @"\b(?:false|true|nil)x[0-9A-Fa-f]+\b").Count * 50;
        score += Regex.Matches(code, @"\d(?:and|or|then|do|repeat|end|else|elseif)\b").Count * 30;

        var stack = new Stack<string>();
        foreach (var token in LuaLex.Tokenize(source))
        {
            if (token.Kind is LuaTokenKind.String or LuaTokenKind.LongString or LuaTokenKind.Comment) continue;
            if (token.Text is "(" or "[" or "{") stack.Push(token.Text);
            else if (token.Text is ")" or "]" or "}")
            {
                if (stack.Count == 0) { score += 100; continue; }
                var open = stack.Pop();
                if (!Matches(open, token.Text)) score += 100;
            }
        }
        score += stack.Count * 100;
        return score;
    }

    private static bool Matches(string open, string close) =>
        (open, close) is ("(", ")") or ("[", "]") or ("{", "}");
}
