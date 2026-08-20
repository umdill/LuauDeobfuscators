using System.Text;

namespace WRDDeobfuscator;

internal static class LuaText
{
    public static int FindMatching(string text, int openPos, char open, char close)
    {
        int depth = 0;
        char quote = '\0';
        bool inString = false;
        for (int i = openPos; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c == open) depth++;
            else if (c == close && --depth == 0) return i;
        }
        return -1;
    }

    public static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0, start = 0;
        char quote = '\0';
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (depth == 0 && c is ',' or ';')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    public static List<string> ParseQuotedStrings(string text)
    {
        var result = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\'' or '"')) continue;
            char q = text[i++];
            var raw = new StringBuilder();
            while (i < text.Length && text[i] != q)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    raw.Append(text[i++]);
                    raw.Append(text[i]);
                    if (char.IsDigit(text[i]))
                    {
                        int extra = 0;
                        while (i + 1 < text.Length && extra < 2 && char.IsDigit(text[i + 1]))
                        { raw.Append(text[++i]); extra++; }
                    }
                }
                else raw.Append(text[i]);
                i++;
            }
            result.Add(Unescape(raw.ToString()));
        }
        return result;
    }

    public static string Unescape(string raw)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                if (char.IsDigit(raw[i + 1]))
                {
                    int j = i + 1, n = 0, count = 0;
                    while (j < raw.Length && count < 3 && char.IsDigit(raw[j]))
                    { n = n * 10 + raw[j] - '0'; j++; count++; }
                    sb.Append((char)(n & 0xFF)); i = j - 1; continue;
                }
                char e = raw[++i];
                sb.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', '\'' => '\'', _ => e });
                continue;
            }
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    public static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32 || c > 126) sb.Append('\\').Append(((int)c & 255).ToString("D3"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
