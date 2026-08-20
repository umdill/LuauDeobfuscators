using System.Text;
using System.Text.RegularExpressions;

namespace WRDDeobfuscator;

internal sealed record StaticPeelResult(string Source, List<string> Strings, int Lookups, int Folds);

internal static class StaticPeeler
{
    public static StaticPeelResult Run(string source)
    {
        if (!source.Contains("wearedevs.net/obfuscator", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("wrd v1 signature not detected.");

        (string tableName, List<string> encoded) = ExtractInitialStringTable(source);
        ApplyShuffles(source, encoded);
        Dictionary<char, int> alphabet = FindAlphabetStructurally(source);
        List<string> decoded = encoded.Select(s => Decode64(s, alphabet)).ToList();

        LookupInfo? lookup = FindLookupHelper(source, tableName);
        string stage = source;
        int resolved = 0;
        if (lookup is not null)
            stage = ReplaceLookups(stage, lookup, decoded, out resolved);

        stage = FoldNumericParentheses(stage, out int folds);
        stage = RewriteEscapedStringLiterals(stage);
        stage = ConservativeFormat(stage);

        return new StaticPeelResult(stage, decoded, resolved, folds);
    }

    private static (string Name, List<string> Items) ExtractInitialStringTable(string source)
    {
        foreach (Match m in Regex.Matches(source, @"local\s+([A-Za-z_]\w*)\s*=\s*\{"))
        {
            int open = source.IndexOf('{', m.Index);
            int close = LuaText.FindMatching(source, open, '{', '}');
            if (open < 0 || close < 0) continue;

            List<string> items = LuaText.ParseQuotedStrings(source[(open + 1)..close]);
            if (items.Count >= 8)
                return (m.Groups[1].Value, items);
        }

        throw new InvalidDataException("no initial local tables");
    }

    private static void ApplyShuffles(string source, List<string> values)
    {
        int marker = source.IndexOf("ipairs({", StringComparison.Ordinal);
        if (marker < 0) return;
        int open = source.IndexOf('{', marker);
        int close = LuaText.FindMatching(source, open, '{', '}');
        if (open < 0 || close < 0) return;
        string body = source[(open + 1)..close];

        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] != '{') continue;
            int end = LuaText.FindMatching(body, i, '{', '}');
            if (end < 0) break;
            string[] p = LuaText.SplitTopLevel(body[(i + 1)..end]).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (p.Length >= 2 && IntegerExpression.TryEvaluate(p[0], out long a) && IntegerExpression.TryEvaluate(p[1], out long b)
                && a >= 1 && b >= 1 && a <= values.Count && b <= values.Count)
            {
                int l = (int)Math.Min(a, b) - 1, r = (int)Math.Max(a, b) - 1;
                while (l < r) { (values[l], values[r]) = (values[r], values[l]); l++; r--; }
            }
            i = end;
        }
    }

    private static Dictionary<char, int> FindAlphabetStructurally(string source)
    {
        foreach (Match m in Regex.Matches(source, @"local\s+([A-Za-z_]\w*)\s*=\s*\{"))
        {
            int open = source.IndexOf('{', m.Index);
            int close = LuaText.FindMatching(source, open, '{', '}');
            if (open < 0 || close < 0) continue;
            string body = source[(open + 1)..close];
            Dictionary<char, int> map = ParseAlphabetCandidate(body);
            if (map.Count == 64 && map.Values.Distinct().Order().SequenceEqual(Enumerable.Range(0, 64)))
                return map;
        }

        throw new InvalidDataException("no decoder for 64-symbol detected");
    }

    private static Dictionary<char, int> ParseAlphabetCandidate(string body)
    {
        var map = new Dictionary<char, int>();
        foreach (string raw in LuaText.SplitTopLevel(body))
        {
            string item = raw.Trim();
            int eq = item.IndexOf('=');
            if (eq <= 0) continue;
            string keyText = item[..eq].Trim();
            string valText = item[(eq + 1)..].Trim();
            if (!IntegerExpression.TryEvaluate(valText, out long val) || val is < 0 or > 63) continue;

            string? key = null;
            if (keyText.StartsWith("[\"") || keyText.StartsWith("['"))
            {
                int q = keyText.IndexOfAny(['"', '\'']);
                if (q < 0) continue;
                char quote = keyText[q];
                int endQ = keyText.LastIndexOf(quote);
                if (endQ > q) key = LuaText.Unescape(keyText[(q + 1)..endQ]);
            }
            else if (keyText.Length == 1)
            {
                key = keyText;
            }

            if (key is { Length: 1 }) map[key[0]] = (int)val;
        }
        return map;
    }

    private static string Decode64(string input, Dictionary<char, int> map)
    {
        var bytes = new List<byte>();
        long acc = 0; int group = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (map.TryGetValue(c, out int v))
            {
                acc += v * Pow64(3 - group); group++;
                if (group == 4)
                {
                    bytes.Add((byte)(acc / 65536));
                    bytes.Add((byte)((acc % 65536) / 256));
                    bytes.Add((byte)(acc % 256));
                    acc = 0; group = 0;
                }
            }
            else if (c == '=')
            {
                bytes.Add((byte)(acc / 65536));
                if (i + 1 >= input.Length || input[i + 1] != '=') bytes.Add((byte)((acc % 65536) / 256));
                break;
            }
        }
        return Encoding.Latin1.GetString(bytes.ToArray());
    }

    private static long Pow64(int n) { long v = 1; while (n-- > 0) v *= 64; return v; }

    private sealed record LookupInfo(string Name, long Offset);

    private static LookupInfo? FindLookupHelper(string source, string encodedTableName)
    {
        var rx = new Regex(
            @"local\s+function\s+(?<fn>[A-Za-z_]\w*)\s*\(\s*(?<arg>[A-Za-z_]\w*)\s*\)\s*" +
            @"return\s+(?<table>[A-Za-z_]\w*)\s*\[\s*(?<index>[^\]]+)\s*\]\s*end",
            RegexOptions.Singleline);

        foreach (Match m in rx.Matches(source))
        {
            if (!m.Groups["table"].Value.Equals(encodedTableName, StringComparison.Ordinal))
                continue;

            string arg = m.Groups["arg"].Value;
            string index = m.Groups["index"].Value.Trim();
            if (!index.StartsWith(arg, StringComparison.Ordinal))
                continue;

            string remainder = index[arg.Length..].Trim();
            if (remainder.Length == 0)
                return new LookupInfo(m.Groups["fn"].Value, 0);

            if (remainder[0] is not ('+' or '-'))
                continue;

            if (IntegerExpression.TryEvaluate(remainder, out long offset))
                return new LookupInfo(m.Groups["fn"].Value, offset);
        }

        int pos = 0;
        while ((pos = source.IndexOf("local function", pos, StringComparison.Ordinal)) >= 0)
        {
            int nameStart = pos + "local function".Length;
            while (nameStart < source.Length && char.IsWhiteSpace(source[nameStart])) nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < source.Length && (char.IsLetterOrDigit(source[nameEnd]) || source[nameEnd] == '_')) nameEnd++;
            if (nameEnd == nameStart) { pos = nameStart; continue; }
            string fn = source[nameStart..nameEnd];

            int openParen = source.IndexOf('(', nameEnd);
            if (openParen < 0) break;
            int closeParen = LuaText.FindMatching(source, openParen, '(', ')');
            if (closeParen < 0) break;
            string arg = source[(openParen + 1)..closeParen].Trim();
            if (!Regex.IsMatch(arg, @"^[A-Za-z_]\w*$")) { pos = closeParen + 1; continue; }

            int returnPos = source.IndexOf("return", closeParen, StringComparison.Ordinal);
            if (returnPos < 0 || returnPos - closeParen > 128) { pos = closeParen + 1; continue; }
            int bracket = source.IndexOf('[', returnPos);
            if (bracket < 0 || bracket - returnPos > 128) { pos = closeParen + 1; continue; }
            string table = source[(returnPos + 6)..bracket].Trim();
            if (!table.Equals(encodedTableName, StringComparison.Ordinal)) { pos = closeParen + 1; continue; }
            int closeBracket = LuaText.FindMatching(source, bracket, '[', ']');
            if (closeBracket < 0) { pos = closeParen + 1; continue; }
            string index = source[(bracket + 1)..closeBracket].Trim();
            if (index.StartsWith(arg, StringComparison.Ordinal))
            {
                string remainder = index[arg.Length..].Trim();
                if (remainder.Length == 0) return new LookupInfo(fn, 0);
                if (remainder[0] is '+' or '-')
                {
                    if (IntegerExpression.TryEvaluate(remainder, out long offset))
                        return new LookupInfo(fn, offset);
                }
            }
            pos = closeParen + 1;
        }

        return null;
    }

    private static string ReplaceLookups(string source, LookupInfo lookup, IReadOnlyList<string> decoded, out int count)
    {
        count = 0;
        var sb = new StringBuilder(source.Length);
        int i = 0;
        string prefix = lookup.Name + "(";
        while (i < source.Length)
        {
            bool hit = source.AsSpan(i).StartsWith(prefix, StringComparison.Ordinal)
                && (i == 0 || !(char.IsLetterOrDigit(source[i - 1]) || source[i - 1] == '_'));
            if (!hit) { sb.Append(source[i++]); continue; }
            int open = i + lookup.Name.Length;
            int close = LuaText.FindMatching(source, open, '(', ')');
            if (close < 0) { sb.Append(source[i++]); continue; }
            string expr = source[(open + 1)..close];
            if (IntegerExpression.TryEvaluate(expr, out long arg))
            {
                long index = arg + lookup.Offset;
                if (index >= 1 && index <= decoded.Count)
                {
                    sb.Append(LuaText.Quote(decoded[(int)index - 1]));
                    count++; i = close + 1; continue;
                }
            }
            sb.Append(source, i, close - i + 1); i = close + 1;
        }
        return sb.ToString();
    }

    private static string FoldNumericParentheses(string source, out int total)
    {
        total = 0; string current = source;
        for (int pass = 0; pass < 12; pass++)
        {
            bool changed = false; var sb = new StringBuilder(current.Length);
            for (int i = 0; i < current.Length;)
            {
                if (current[i] == '(')
                {
                    int close = LuaText.FindMatching(current, i, '(', ')');
                    if (close > i && IntegerExpression.TryEvaluate(current[(i + 1)..close], out long v))
                    {
                        if (v < 0) sb.Append('(').Append(v).Append(')');
                        else sb.Append(v);
                        total++; changed = true; i = close + 1; continue;
                    }
                }
                sb.Append(current[i++]);
            }
            current = sb.ToString(); if (!changed) break;
        }
        return current;
    }

    private static string RewriteEscapedStringLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is not ('\'' or '"')) { sb.Append(source[i]); continue; }
            char q = source[i]; int start = i; i++;
            var raw = new StringBuilder(); bool escapedDigits = false;
            while (i < source.Length && source[i] != q)
            {
                if (source[i] == '\\' && i + 1 < source.Length)
                {
                    raw.Append(source[i++]); raw.Append(source[i]);
                    if (char.IsDigit(source[i]))
                    {
                        escapedDigits = true; int extra = 0;
                        while (i + 1 < source.Length && extra < 2 && char.IsDigit(source[i + 1])) { raw.Append(source[++i]); extra++; }
                    }
                }
                else raw.Append(source[i]);
                i++;
            }
            if (i >= source.Length) { sb.Append(source[start..]); break; }
            sb.Append(escapedDigits ? LuaText.Quote(LuaText.Unescape(raw.ToString())) : source[start..(i + 1)]);
        }
        return sb.ToString();
    }

    private static string ConservativeFormat(string s)
    {
        s = Regex.Replace(s, @"\bthen\s+", "then\n");
        s = Regex.Replace(s, @"\belse\s+", "else\n");
        s = Regex.Replace(s, @";(?=\s*[A-Za-z_])", ";\n");
        return s;
    }
}
