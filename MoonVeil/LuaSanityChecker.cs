using System.Text.RegularExpressions;

namespace MoonVeilDeobfuscator;

internal static class LuaSanityChecker
{
    public static IEnumerable<string> Check(string source)
    {
        var notes = new List<string>();
        var code = LuaText.CodeOnly(source);

        var badBoolMath = Regex.Matches(code, @"\b(?:false|true|nil)\s*[+\-*/%]").Count;
        if (badBoolMath > 0) notes.Add($"WARNING: {badBoolMath} boolean/nil arithmetic expression(s) remain; these usually indicate damaged legacy input");

        var duplicateOr = Regex.Matches(code, @"\b(-?\d+)\s+or\s+\1\b").Count;
        if (duplicateOr > 0) notes.Add($"WARNING: {duplicateOr} duplicate literal OR expression(s) remain");

        var tokens = LuaLex.Tokenize(source);
        var stack = new Stack<string>();
        foreach (var token in tokens)
        {
            if (token.Kind is LuaTokenKind.String or LuaTokenKind.LongString or LuaTokenKind.Comment) continue;
            if (token.Text is "(" or "[" or "{") stack.Push(token.Text);
            else if (token.Text is ")" or "]" or "}")
            {
                if (stack.Count == 0) { notes.Add("WARNING: unmatched closing delimiter detected"); break; }
                var open = stack.Pop();
                if (!Matches(open, token.Text)) { notes.Add("WARNING: mismatched delimiter detected"); break; }
            }
        }
        if (stack.Count > 0) notes.Add($"WARNING: {stack.Count} unmatched opening delimiter(s) detected");
        if (notes.Count == 0) notes.Add("basic token/delimiter sanity check: passed");
        return notes;
    }

    private static bool Matches(string open, string close) =>
        (open, close) is ("(", ")") or ("[", "]") or ("{", "}");
}
