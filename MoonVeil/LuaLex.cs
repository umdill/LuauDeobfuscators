using System.Text;

namespace MoonVeilDeobfuscator;

internal enum LuaTokenKind
{
    Identifier,
    Number,
    String,
    LongString,
    Comment,
    Symbol,
    Whitespace,
    NewLine
}

internal readonly record struct LuaToken(LuaTokenKind Kind, string Text)
{
    public bool IsWord => Kind is LuaTokenKind.Identifier or LuaTokenKind.Number;
}

internal static class LuaLex
{
    private static readonly string[] MultiSymbols =
    {
        "...", "..=", "//", "<<", ">>", "<=", ">=", "==", "~=", "::", "+=", "-=", "*=", "/=", "%=" , "->", ".."
    };

    public static List<LuaToken> Tokenize(string source)
    {
        var tokens = new List<LuaToken>(Math.Max(32, source.Length / 3));
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                tokens.Add(new(LuaTokenKind.NewLine, "\n"));
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var start = i++;
                while (i < source.Length && source[i] is not '\r' and not '\n' && char.IsWhiteSpace(source[i])) i++;
                tokens.Add(new(LuaTokenKind.Whitespace, source[start..i]));
                continue;
            }

            if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                var start = i;
                i += 2;
                if (i < source.Length && source[i] == '[' && TryLongBracket(source, i, out var close, out var openLen))
                {
                    var end = source.IndexOf(close, i + openLen, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + close.Length;
                }
                else
                {
                    while (i < source.Length && source[i] is not '\r' and not '\n') i++;
                }
                tokens.Add(new(LuaTokenKind.Comment, source[start..i]));
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i;
                var quote = c;
                i++;
                while (i < source.Length)
                {
                    c = source[i++];
                    if (c == '\\' && i < source.Length) { i++; continue; }
                    if (c == quote) break;
                }
                tokens.Add(new(LuaTokenKind.String, source[start..i]));
                continue;
            }

            if (c == '[' && TryLongBracket(source, i, out var longClose, out var longOpenLen))
            {
                var start = i;
                var end = source.IndexOf(longClose, i + longOpenLen, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + longClose.Length;
                tokens.Add(new(LuaTokenKind.LongString, source[start..i]));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                var start = i++;
                while (i < source.Length && IsIdentifierPart(source[i])) i++;
                tokens.Add(new(LuaTokenKind.Identifier, source[start..i]));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                var start = i;
                ReadNumber(source, ref i);
                tokens.Add(new(LuaTokenKind.Number, source[start..i]));
                continue;
            }

            string? symbol = null;
            foreach (var candidate in MultiSymbols)
            {
                if (i + candidate.Length <= source.Length && source.AsSpan(i, candidate.Length).SequenceEqual(candidate.AsSpan()))
                {
                    symbol = candidate;
                    break;
                }
            }
            symbol ??= c.ToString();
            tokens.Add(new(LuaTokenKind.Symbol, symbol));
            i += symbol.Length;
        }
        return tokens;
    }

    public static string RepairAndFormat(string source)
    {
        var tokens = Tokenize(source).Where(t => t.Kind != LuaTokenKind.Whitespace && t.Kind != LuaTokenKind.NewLine).ToList();
        var sb = new StringBuilder(source.Length + source.Length / 5);
        var indent = 0;
        var atLineStart = true;
        var functionDepthPending = 0;
        LuaToken? previous = null;

        void NewLine()
        {
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            if (sb.Length == 0 || sb[^1] != '\n') sb.Append('\n');
            atLineStart = true;
            previous = null;
        }

        void WriteIndent()
        {
            if (!atLineStart) return;
            sb.Append(' ', Math.Max(0, indent) * 4);
            atLineStart = false;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            var t = tokens[index];
            var text = t.Text;
            var lower = t.Kind == LuaTokenKind.Identifier ? text.ToLowerInvariant() : text;

            if (lower is "end" or "until" or "else" or "elseif")
            {
                NewLine();
                indent = Math.Max(0, indent - 1);
            }
            else if (lower is "local" or "return")
            {
                if (!atLineStart) NewLine();
            }

            WriteIndent();

            var needSpace = NeedsSpace(previous, t);
            if (needSpace && sb.Length > 0 && sb[^1] is not ' ' and not '\n') sb.Append(' ');
            sb.Append(text);

            if (lower == "function") functionDepthPending++;
            if (functionDepthPending > 0 && text == ")")
            {
                functionDepthPending--;
                NewLine();
                indent++;
                continue;
            }

            if (t.Kind == LuaTokenKind.Comment)
            {
                NewLine();
                continue;
            }

            if (text == ";")
            {
                NewLine();
                continue;
            }

            if (lower is "then" or "do")
            {
                NewLine();
                indent++;
            }
            else if (lower == "repeat")
            {
                NewLine();
                indent++;
            }
            else if (lower == "else")
            {
                NewLine();
                indent++;
            }
            else if (lower == "elseif")
            {
                // elseif continue b
            }
            else if (lower is "end" or "until")
            {
                NewLine();
            }

            previous = t;
        }

        NewLine();
        return sb.ToString();
    }

    public static bool HasSuspiciousGlue(string source, out List<string> samples)
    {
        samples = new List<string>();
        foreach (var bad in new[] { "falsex", "truex" })
        {
            var pos = 0;
            while ((pos = source.IndexOf(bad, pos, StringComparison.Ordinal)) >= 0)
            {
                samples.Add(source.Substring(Math.Max(0, pos - 16), Math.Min(source.Length - Math.Max(0, pos - 16), 48)).Replace('\n', ' '));
                pos += bad.Length;
                if (samples.Count >= 8) return true;
            }
        }
        return samples.Count > 0;
    }

    private static bool NeedsSpace(LuaToken? previous, LuaToken current)
    {
        if (previous is null) return false;
        var p = previous.Value;
        if ((p.IsWord || p.Kind == LuaTokenKind.String || p.Kind == LuaTokenKind.LongString) &&
            (current.IsWord || current.Kind == LuaTokenKind.String || current.Kind == LuaTokenKind.LongString)) return true;

        if (p.Kind == LuaTokenKind.Identifier && current.Text == "(") return false;
        if ((p.Text is ")" or "]" or "}") && current.IsWord) return true;
        if (p.IsWord && current.Text is "{" ) return true;
        return false;
    }

    private static void ReadNumber(string s, ref int i)
    {
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] is 'x' or 'X'))
        {
            i += 2;
            while (i < s.Length && (Uri.IsHexDigit(s[i]) || s[i] == '_')) i++;
            if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && (Uri.IsHexDigit(s[i]) || s[i] == '_')) i++; }
            if (i < s.Length && (s[i] is 'p' or 'P'))
            {
                i++; if (i < s.Length && s[i] is '+' or '-') i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '_')) i++;
            }
            return;
        }
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] is 'b' or 'B'))
        {
            i += 2;
            while (i < s.Length && ((s[i] is '0' or '1') || s[i] == '_')) i++;
            return;
        }

        var seenDot = false;
        if (s[i] == '.') { seenDot = true; i++; }
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '_')) i++;
        if (!seenDot && i < s.Length && s[i] == '.' && !(i + 1 < s.Length && s[i + 1] == '.'))
        {
            seenDot = true; i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '_')) i++;
        }
        if (i < s.Length && (s[i] is 'e' or 'E'))
        {
            i++; if (i < s.Length && s[i] is '+' or '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '_')) i++;
        }
    }

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool TryLongBracket(string source, int index, out string close, out int openLength)
    {
        close = string.Empty;
        openLength = 0;
        if (index >= source.Length || source[index] != '[') return false;
        var j = index + 1;
        while (j < source.Length && source[j] == '=') j++;
        if (j >= source.Length || source[j] != '[') return false;
        var equals = j - index - 1;
        close = "]" + new string('=', equals) + "]";
        openLength = equals + 2;
        return true;
    }
}
