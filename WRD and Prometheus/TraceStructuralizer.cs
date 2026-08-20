using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WRDDeobfuscator;

internal sealed record StructuralLiftResult(string Source, int Functions, int Loops, int Locals, int RewrittenLines)
{
    public bool AddedStructure => Functions > 0 || Loops > 0 || Locals > 0;
}

internal static class TraceStructuralizer
{
    private sealed record NumericPrint(int Index, string Prefix, long Value, string Suffix, string Original);

    public static StructuralLiftResult Lift(IReadOnlyList<string> input)
    {
        List<string> lines = input
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return new StructuralLiftResult("", 0, 0, 0, 0);

        var consumed = new HashSet<int>();
        var replacements = new Dictionary<int, List<string>>();
        var prelude = new List<string>();
        int functions = 0, loops = 0, locals = 0, rewritten = 0;

        for (int i = 0; i < lines.Count;)
        {
            if (!TryParseNumericPrint(lines[i], i, out NumericPrint? first))
            {
                i++;
                continue;
            }

            var run = new List<NumericPrint> { first };
            int j = i + 1;
            while (j < lines.Count &&
                   TryParseNumericPrint(lines[j], j, out NumericPrint? next) &&
                   next.Prefix == first.Prefix && next.Suffix == first.Suffix)
            {
                run.Add(next);
                j++;
            }

            if (run.Count < 3)
            {
                i++;
                continue;
            }

            string noun = InferNoun(first.Prefix, first.Suffix);
            string functionName = InferFunctionName(first.Prefix, first.Suffix, noun);
            string argumentName = InferArgumentName(noun);
            string? accumulator = FindMatchingFinalAccumulator(lines, j, noun, run.Select(x => x.Value).ToArray());

            if (accumulator is not null)
            {
                prelude.Add($"local {accumulator} = 0");
                locals++;
            }

            var block = new List<string>();
            block.Add($"local function {functionName}({argumentName})");
            if (accumulator is not null)
                block.Add($"    {accumulator} = {accumulator} + {argumentName}");
            block.Add($"    print({BuildInterpolatedPrint(first.Prefix, argumentName, first.Suffix)})");
            block.Add("end");
            block.Add("");

            if (IsArithmeticProgression(run.Select(x => x.Value).ToArray(), out long step))
            {
                block.Add($"for {argumentName} = {run[0].Value.ToString(CultureInfo.InvariantCulture)}, {run[^1].Value.ToString(CultureInfo.InvariantCulture)}, {step.ToString(CultureInfo.InvariantCulture)} do");
                block.Add($"    {functionName}({argumentName})");
                block.Add("end");
                loops++;
            }
            else
            {
                foreach (NumericPrint item in run)
                    block.Add($"{functionName}({item.Value.ToString(CultureInfo.InvariantCulture)})");
            }

            replacements[i] = block;
            foreach (NumericPrint item in run)
                consumed.Add(item.Index);

            functions++;
            rewritten += run.Count;
            i = j;
        }

        var numericByLabel = new Dictionary<string, List<NumericPrint>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            if (!TryParseNumericPrint(lines[i], i, out NumericPrint? p)) continue;
            string label = NormalizeLabel(p.Prefix, p.Suffix);
            if (label.Length == 0 || label.StartsWith("Final ", StringComparison.OrdinalIgnoreCase)) continue;
            if (!numericByLabel.TryGetValue(label, out List<NumericPrint>? list))
                numericByLabel[label] = list = [];
            list.Add(p);
        }

        foreach ((string label, List<NumericPrint> seq) in numericByLabel)
        {
            if (seq.Count < 2) continue;
            string variable = SafeIdentifier(label);
            if (string.IsNullOrWhiteSpace(variable)) continue;

            NumericPrint last = seq[^1];
            int finalIndex = -1;
            for (int i = last.Index + 1; i < lines.Count; i++)
            {
                if (TryParseNumericPrint(lines[i], i, out NumericPrint? p) &&
                    NormalizeLabel(p.Prefix, p.Suffix).Equals("Final " + label, StringComparison.OrdinalIgnoreCase) &&
                    p.Value == last.Value)
                {
                    finalIndex = i;
                    break;
                }
            }

            if (finalIndex < 0) continue;

            var block = new List<string>();
            block.Add($"local {variable} = {seq[0].Value.ToString(CultureInfo.InvariantCulture)}");
            block.Add($"print({BuildInterpolatedPrint(seq[0].Prefix, variable, seq[0].Suffix)})");
            for (int k = 1; k < seq.Count; k++)
            {
                block.Add($"{variable} = {seq[k].Value.ToString(CultureInfo.InvariantCulture)}");
                block.Add($"print({BuildInterpolatedPrint(seq[k].Prefix, variable, seq[k].Suffix)})");
            }
            block.Add($"print({BuildInterpolatedPrint("Final " + seq[0].Prefix, variable, seq[0].Suffix)})");

            replacements[seq[0].Index] = block;
            foreach (NumericPrint p in seq) consumed.Add(p.Index);
            consumed.Add(finalIndex);
            locals++;
            rewritten += seq.Count + 1;
        }

        foreach (string localLine in prelude)
        {
            Match m = Regex.Match(localLine, @"^local\s+(?<name>[A-Za-z_]\w*)\s*=\s*0$");
            if (!m.Success) continue;
            string variable = m.Groups["name"].Value;
            string title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(variable.Replace("_", " "));
            for (int i = 0; i < lines.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                if (TryParseNumericPrint(lines[i], i, out NumericPrint? p) &&
                    NormalizeLabel(p.Prefix, p.Suffix).Equals("Final " + title, StringComparison.OrdinalIgnoreCase))
                {
                    replacements[i] = [$"print({BuildInterpolatedPrint(p.Prefix, variable, p.Suffix)})"];
                    rewritten++;
                }
            }
        }

        var output = new List<string>();
        if (prelude.Count > 0)
        {
            output.AddRange(prelude.Distinct(StringComparer.Ordinal));
            output.Add("");
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (replacements.TryGetValue(i, out List<string>? block))
            {
                output.AddRange(block);
                if (block.Count > 0 && block[^1].Length != 0) output.Add("");
                continue;
            }
            if (consumed.Contains(i)) continue;
            output.Add(lines[i]);
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);

        return new StructuralLiftResult(string.Join("\n", output), functions, loops, locals, rewritten);
    }

    private static bool TryParseNumericPrint(string line, int index, out NumericPrint? result)
    {
        result = null;
        Match m = Regex.Match(line,
            "^print\\(\\\"(?<text>(?:\\\\.|[^\\\"])*)\\\"\\)$",
            RegexOptions.Singleline);
        if (!m.Success) return false;

        string text = LuaText.Unescape(m.Groups["text"].Value);
        Match n = Regex.Match(text, @"^(?<prefix>.*?)(?<value>-?\d+)(?<suffix>[^\d]*)$", RegexOptions.Singleline);
        if (!n.Success) return false;

        if (!long.TryParse(n.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            return false;

        result = new NumericPrint(index, n.Groups["prefix"].Value, value, n.Groups["suffix"].Value, line);
        return true;
    }

    private static bool IsArithmeticProgression(long[] values, out long step)
    {
        step = 0;
        if (values.Length < 2) return false;
        step = values[1] - values[0];
        if (step == 0) return false;
        for (int i = 2; i < values.Length; i++)
            if (values[i] - values[i - 1] != step) return false;
        return true;
    }

    private static string BuildInterpolatedPrint(string prefix, string expression, string suffix)
    {
        var parts = new List<string>();
        if (prefix.Length > 0) parts.Add(LuaText.Quote(prefix));
        parts.Add($"tostring({expression})");
        if (suffix.Length > 0) parts.Add(LuaText.Quote(suffix));
        return string.Join(" .. ", parts);
    }

    private static string NormalizeLabel(string prefix, string suffix)
    {
        string combined = (prefix + " " + suffix)
            .Replace(":", " ")
            .Replace(".", " ")
            .Replace("-", " ");
        combined = Regex.Replace(combined, @"\s+", " ").Trim();
        return combined;
    }

    private static string InferNoun(string prefix, string suffix)
    {
        string p = NormalizeLabel(prefix, suffix);
        string[] words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words.Reverse())
        {
            if (!word.Equals("added", StringComparison.OrdinalIgnoreCase) &&
                !word.Equals("add", StringComparison.OrdinalIgnoreCase) &&
                !word.Equals("set", StringComparison.OrdinalIgnoreCase))
                return word;
        }
        return "value";
    }

    private static string InferArgumentName(string noun)
        => noun.Equals("coins", StringComparison.OrdinalIgnoreCase) ? "amount" : "value";

    private static string InferFunctionName(string prefix, string suffix, string noun)
    {
        if (prefix.Trim().StartsWith("Added", StringComparison.OrdinalIgnoreCase))
            return "add" + Pascal(Singular(noun));
        if (prefix.Contains("Damage", StringComparison.OrdinalIgnoreCase))
            return "applyDamage";
        return "handle" + Pascal(Singular(noun));
    }

    private static string? FindMatchingFinalAccumulator(List<string> lines, int start, string noun, long[] values)
    {
        long sum = values.Sum();
        string expected = "Final " + Pascal(noun);
        for (int i = start; i < lines.Count; i++)
        {
            if (!TryParseNumericPrint(lines[i], i, out NumericPrint? p)) continue;
            string label = NormalizeLabel(p.Prefix, p.Suffix);
            if (label.Equals(expected, StringComparison.OrdinalIgnoreCase) && p.Value == sum)
                return SafeIdentifier(noun);
        }
        return null;
    }

    private static string Singular(string value)
        => value.EndsWith("s", StringComparison.OrdinalIgnoreCase) && value.Length > 1 ? value[..^1] : value;

    private static string Pascal(string value)
    {
        string[] words = Regex.Split(value, @"[^A-Za-z0-9]+")
            .Where(x => x.Length > 0)
            .ToArray();
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string SafeIdentifier(string value)
    {
        string s = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');
        if (s.Length == 0) return "value";
        if (char.IsDigit(s[0])) s = "v_" + s;
        return s;
    }
}
