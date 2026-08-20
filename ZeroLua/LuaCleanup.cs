// works for 5.6 mostly
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroLuaDeobfuscator;

public static class LuaCleanup
{
    public static string Run(string source)
    {
        source = source.Replace("\r\n", "\n");
        var lines = source.Split('\n').ToList();

        for (int i = 0; i + 1 < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*goto\s+(L\d+)\s*$");
            if (m.Success && Regex.IsMatch(lines[i + 1], @"^\s*::" + Regex.Escape(m.Groups[1].Value) + @"::\s*$"))
                lines[i] = "";
        }

        var joined = string.Join("\n", lines);
        var targets = Regex.Matches(joined, @"\bgoto\s+(L\d+)\b")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*::(L\d+)::\s*$");
            if (m.Success && !targets.Contains(m.Groups[1].Value)) lines[i] = "";
        }

        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = Regex.Replace(lines[i], @"\(75 \* 75\) % 4", "1");
            lines[i] = Regex.Replace(lines[i], @"\(\(51 \* 6\) \+ 3\) % 3", "0");
            lines[i] = Regex.Replace(lines[i], @"\(\(36 \* 4\) \+ 2\) % 4", "2");
            lines[i] = Regex.Replace(lines[i], @"43 \* 0", "0");
        }

        var sb = new StringBuilder();
        bool blank = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            bool b = string.IsNullOrWhiteSpace(line);
            if (b && blank) continue;
            sb.AppendLine(line);
            blank = b;
        }
        return sb.ToString().Trim() + Environment.NewLine;
    }
}
