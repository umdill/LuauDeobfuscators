using System.Text;

namespace MoonVeilDeobfuscator;

internal static class LuaText
{
    public static string CodeOnly(string source) => MaskProtected(source);

    private static string MaskProtected(string source)
    {
        var output = new StringBuilder(source.Length);
        var code = new StringBuilder();
        void FlushCode() { if (code.Length == 0) return; output.Append(code); code.Clear(); }
        for (var i = 0; i < source.Length;)
        {
            var c = source[i];
            if (c is '\'' or '"')
            {
                FlushCode();
                var start = i; var quote = c; i++;
                while (i < source.Length) { c = source[i++]; if (c == '\\' && i < source.Length) { i++; continue; } if (c == quote) break; }
                output.Append(' ', i - start);
                continue;
            }
            if (c == '[' && TryLongBracket(source, i, out var close, out var openLength))
            {
                FlushCode(); var start = i; var end = source.IndexOf(close, i + openLength, StringComparison.Ordinal); i = end < 0 ? source.Length : end + close.Length; output.Append(' ', i - start); continue;
            }
            if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                FlushCode(); var start = i;
                if (i + 2 < source.Length && source[i + 2] == '[' && TryLongBracket(source, i + 2, out var commentClose, out var commentOpenLength))
                { var end = source.IndexOf(commentClose, i + 2 + commentOpenLength, StringComparison.Ordinal); i = end < 0 ? source.Length : end + commentClose.Length; }
                else { var end = source.IndexOf('\n', i); i = end < 0 ? source.Length : end; }
                output.Append(' ', i - start); continue;
            }
            code.Append(c); i++;
        }
        FlushCode();
        return output.ToString();
    }

    public static string TransformCode(string source, Func<string, string> transform)
    {
        var output = new StringBuilder(source.Length);
        var code = new StringBuilder();

        void FlushCode()
        {
            if (code.Length == 0) return;
            output.Append(transform(code.ToString()));
            code.Clear();
        }

        for (var i = 0; i < source.Length;)
        {
            var c = source[i];

            if (c is '\'' or '"')
            {
                FlushCode();
                var quote = c;
                output.Append(c);
                i++;
                while (i < source.Length)
                {
                    c = source[i++];
                    output.Append(c);
                    if (c == '\\' && i < source.Length)
                    {
                        output.Append(source[i++]);
                        continue;
                    }
                    if (c == quote) break;
                }
                continue;
            }

            if (c == '[' && TryLongBracket(source, i, out var close, out var openLength))
            {
                FlushCode();
                var end = source.IndexOf(close, i + openLength, StringComparison.Ordinal);
                if (end < 0)
                {
                    output.Append(source.AsSpan(i));
                    break;
                }
                var length = end + close.Length - i;
                output.Append(source.AsSpan(i, length));
                i += length;
                continue;
            }

            if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                FlushCode();
                if (i + 2 < source.Length && source[i + 2] == '[' && TryLongBracket(source, i + 2, out var commentClose, out var commentOpenLength))
                {
                    var end = source.IndexOf(commentClose, i + 2 + commentOpenLength, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        output.Append(source.AsSpan(i));
                        break;
                    }
                    var length = end + commentClose.Length - i;
                    output.Append(source.AsSpan(i, length));
                    i += length;
                }
                else
                {
                    var end = source.IndexOf('\n', i);
                    if (end < 0)
                    {
                        output.Append(source.AsSpan(i));
                        break;
                    }
                    output.Append(source.AsSpan(i, end - i));
                    i = end;
                }
                continue;
            }

            code.Append(c);
            i++;
        }

        FlushCode();
        return output.ToString();
    }

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
