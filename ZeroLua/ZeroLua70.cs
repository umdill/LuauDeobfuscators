using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ZeroLuaDeobfuscator;

public static class ZeroLua70
{
    public sealed record Instruction(int Offset, int EncodedLength, int Opcode, int A, int B, int C, int Mask)
    {
        public string Name => OpcodeNames.TryGetValue(Opcode, out var n) ? n : $"OP_{Opcode}";
    }

    public sealed class Prototype
    {
        public string Path { get; init; } = "root";
        public LuaTable Raw { get; init; } = new();
        public int Seed { get; init; }
        public int ParameterCount { get; init; }
        public int RegisterStride { get; init; }
        public int RegisterOffset { get; init; }
        public int OperandShuffle { get; init; }
        public byte[] Bytecode { get; init; } = Array.Empty<byte>();
        public List<Instruction> Instructions { get; } = new();
        public List<Prototype> Children { get; } = new();
    }

    public sealed class DecodeResult
    {
        public LuaTable ConstantPool { get; init; } = new();
        public List<Prototype> Prototypes { get; } = new();
        public string Disassembly { get; init; } = string.Empty;
    }

    // opcodes
    private static readonly Dictionary<int, string> OpcodeNames = new()
    {
        [5] = "SETUPVAL", [13] = "RETURN", [14] = "SUB", [17] = "FORPREP",
        [22] = "LEN", [24] = "SETTABLEK", [40] = "ADDK", [45] = "SUB",
        [51] = "SETTABLE", [55] = "CALL", [56] = "JMP", [58] = "LOADK",
        [62] = "GETGLOBAL", [71] = "ADD", [74] = "GETUPVAL", [83] = "GETTABLEK",
        [94] = "ADD/JMP", [102] = "CONCAT", [110] = "FORLOOP", [115] = "MOVE",
        [119] = "GETSERVICE", [121] = "LT", [129] = "CALL", [132] = "LOADNIL",
        [141] = "SETTABLEKK", [163] = "NOT", [176] = "CLOSURE", [189] = "UNM",
        [190] = "DIV", [196] = "EQ", [198] = "POW", [209] = "CONCAT/JMP",
        [212] = "LOADBOOL", [213] = "GETTABLE", [214] = "MOD", [216] = "GETGLOBAL",
        [218] = "SETGLOBAL", [226] = "GETTABLE", [233] = "MOVE", [237] = "MUL",
        [243] = "VARARG", [251] = "ADD", [252] = "LE", [254] = "NEWTABLE"
    };

    private static readonly string BaseAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_[]{|}";

    private static readonly byte[] Key = Ts(7027, 28, 1);
    private static readonly string Alphabet = BuildAlphabet();
    private static readonly Dictionary<char, int> AlphabetMap =
        Alphabet.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

    public static bool IsMatch(string source) =>
        source.Contains("[ ZERO LUA V7.0 ]", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("ZERO LUA V7.0", StringComparison.OrdinalIgnoreCase);

    public static string Deobfuscate(string source) => Decode(source).Disassembly;

    public static DecodeResult Decode(string source)
    {
        if (!IsMatch(source))
            throw new InvalidDataException("input is not v7.0");

        var pool = ExtractConstantPool(source);
        var protos = new List<Prototype>();
        var seen = new HashSet<LuaTable>(ReferenceEqualityComparer.Instance);
        Walk(pool, "EG", protos, seen);

        var sb = new StringBuilder();
        sb.AppendLine("-- vm dump for v7.0");
        sb.AppendLine("-- vm wrappers normalized, cleanup yourself");
        sb.AppendLine();
        DumpConstants(pool, sb);
        sb.AppendLine();
        foreach (var p in protos)
        {
            DumpPrototype(p, pool, sb);
            sb.AppendLine();
        }

        var result = new DecodeResult { ConstantPool = pool, Disassembly = sb.ToString() };
        result.Prototypes.AddRange(protos);
        return result;
    }

    private static void Walk(LuaTable table, string path, List<Prototype> output, HashSet<LuaTable> seen)
    {
        if (!seen.Add(table)) return;
        if (IsPrototype(table))
        {
            var p = DecodePrototype(table, path);
            output.Add(p);
        }
        for (int i = 0; i < table.Array.Count; i++)
            if (table.Array[i] is LuaTable child)
                Walk(child, path + "/" + (i + 1), output, seen);
        foreach (var kv in table.Fields)
            if (kv.Value is LuaTable child)
                Walk(child, path + "/" + kv.Key, output, seen);
    }

    private static bool IsPrototype(LuaTable t)
    {
        if (t.Array.Count < 3 || t.Array[0] is not string || t.Array[1] is not LuaTable meta || t.Array[2] is not LuaTable)
            return false;
        return meta.Array.Count >= 4 && meta.Array.Take(4).All(IsNumber);
    }

    private static Prototype DecodePrototype(LuaTable proto, string path)
    {
        var encoded = Sl((string)proto.Array[0]!);
        var meta = (LuaTable)proto.Array[1]!;
        int nN = Int(meta.Array[0]);
        int oU = Int(meta.Array[1]);
        int p1 = Int(meta.Array[2]);
        int q8 = Int(meta.Array[3]);

        int re = Mod(nN - 4570875, 65536);
        int uz = Mod(p1 - 5108625, 65536);
        int vG = Mod(q8 - 6184125, 65536);
        int wN = uz % 4; if (wN == 0) wN = 1;
        int y1 = (uz / 4) % 8;
        int fn = vG / 256; if ((fn & 1) == 0) fn++;
        int gu = vG % 256;
        int i8 = Mod(re * 31 + 17, 256); if ((i8 & 1) == 0) i8++;
        int je = Mod(re * 43 + 71, 256);
        var ls = Ts(re, encoded.Length, wN);
        if (ls.Length == 0) ls = new byte[] { 17, 31, 53, 97 };
        int ng = Mod(re * 19 + 71, 256);
        int on = Mod(re * 37 + 137, 256);
        int puSeed = Mod(re * 43, 256);

        var p = new Prototype
        {
            Path = path, Raw = proto, Seed = re, ParameterCount = oU,
            RegisterStride = fn, RegisterOffset = gu, OperandShuffle = y1,
            Bytecode = encoded
        };

        int pc = 1; // 1-based byte offset
        while (pc <= encoded.Length)
        {
            int start = pc;
            int e = SeIndex(start, ls, ng, on, puSeed);
            byte k0 = SeByte(start, e + 0, ls, ng, on, puSeed);
            byte k1 = SeByte(start, e + 1, ls, ng, on, puSeed);
            int mG = encoded[start - 1];
            int nNByte = ByteAt(encoded, start + 1);
            int oUWord = Mod(mG - k0, 256);
            int p1Word = Mod(nNByte - k1, 256);
            int q8Word = p1Word * 256 + oUWord - 32768;
            int reOp = FloorDiv(q8Word, 4);
            int sl = Mod(q8Word, 4);
            int words = sl + 2; // 2,3,4 16-bit words -> 4/6/8 bytes

            int op1 = DecodeWord(encoded, start + 2,
                SeByte(start, e + 2, ls, ng, on, puSeed),
                SeByte(start, e + 3, ls, ng, on, puSeed));
            int op2 = 0, op3 = 0;
            if (words >= 3)
                op2 = DecodeWord(encoded, start + 4,
                    SeByte(start, e + 4, ls, ng, on, puSeed),
                    SeByte(start, e + 5, ls, ng, on, puSeed));
            if (words == 4)
                op3 = DecodeWord(encoded, start + 6,
                    SeByte(start, e + 6, ls, ng, on, puSeed),
                    SeByte(start, e + 7, ls, ng, on, puSeed));

            int mask = Mod(reOp * i8 + je + Mod(start * 13, 256), 256);
            int a, b, c;
            if (words == 4)
            {
                switch (y1)
                {
                    case 1: c = op1; a = op2; b = op3; break;
                    case 2: b = op1; c = op2; a = op3; break;
                    case 3: a = op1; c = op2; b = op3; break;
                    case 4: b = op1; a = op2; c = op3; break;
                    case 5: c = op1; b = op2; a = op3; break;
                    default: a = op1; b = op2; c = op3; break;
                }
            }
            else { a = op1; b = op2; c = op3; }

            p.Instructions.Add(new Instruction(start, words * 2, mask, a, b, c, mask));
            pc = start + words * 2;
        }
        return p;
    }

    private static int DecodeWord(byte[] bytes, int pos, byte loMask, byte hiMask)
    {
        int lo = Mod(ByteAt(bytes, pos) - loMask, 256);
        int hi = Mod(ByteAt(bytes, pos + 1) - hiMask, 256);
        return hi * 256 + lo - 32768;
    }

    private static int SeIndex(int pos, byte[] ls, int ng, int on, int pu)
        => Mod(pos - 1, 64);

    private static byte SeByte(int pos, int relativeIndex, byte[] ls, int ng, int on, int pu)
    {
        // _Se = Cache
        int block = FloorDiv(pos - 1, 64);
        int basePos = block * 64 + 1;
        int d = basePos + relativeIndex;
        int e = ls[Mod(d - 1, ls.Length)];
        int f = ls[Mod(d * 3 - 1, ls.Length)];
        int g = Mod(e * 37 + ng + d * 23, 256);
        int h = Mod(f * 53 + on + d * 31, 256);
        int ie = Mod(g * 73 + h * 89 + e * 31 + pu, 256);
        int jl = Mod(h * 47 + g * 59 + f * 17 + d * 29, 256);
        return (byte)Mod(ie * 53 + jl * 67 + d * 41, 256);
    }

    private static void DumpConstants(LuaTable pool, StringBuilder sb)
    {
        sb.AppendLine("-- decoded constants");
        for (int i = 1; i <= pool.Array.Count; i++)
        {
            object? raw = pool.Array[i - 1];
            string val;
            if (raw is LuaTable t && IsPrototype(t)) val = $"<PROTO EG/{i}>";
            else
            {
                object? decoded = DecodeConstant(pool, i);
                val = FormatValue(decoded);
            }
            sb.Append("-- C[").Append(i).Append("] = ").AppendLine(val);
        }
    }

    private static void DumpPrototype(Prototype p, LuaTable pool, StringBuilder sb)
    {
        sb.Append("-- PROTO ").Append(p.Path)
          .Append(" seed=").Append(p.Seed)
          .Append(" params=").Append(p.ParameterCount)
          .Append(" regStride=").Append(p.RegisterStride)
          .Append(" regOffset=").Append(p.RegisterOffset)
          .Append(" shuffle=").Append(p.OperandShuffle).AppendLine();

        foreach (var ins in p.Instructions)
        {
            sb.Append(ins.Offset.ToString("D5", CultureInfo.InvariantCulture)).Append("  ")
              .Append(ins.Name.PadRight(12)).Append(' ')
              .Append(ins.A.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(ins.B.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(ins.C.ToString(CultureInfo.InvariantCulture));

            if (ins.Opcode is 58 or 62 or 83 or 119 or 141 or 216 or 218)
            {
                int ci = ins.Opcode switch
                {
                    58 => ins.B + 1,
                    62 or 216 => ins.B + 1,
                    83 => ins.C + 1,
                    119 => ins.C + 1,
                    141 => ins.B + 1,
                    218 => ins.B + 1,
                    _ => 0
                };
                if (ci > 0 && ci <= pool.Array.Count)
                {
                    sb.Append("    ; C[").Append(ci).Append("]=")
                      .Append(FormatValue(DecodeConstant(pool, ci)));
                }
            }
            sb.AppendLine();
        }
    }

    private static readonly Dictionary<(LuaTable, int), object?> ConstantCache = new(new RefTupleComparer());

    public static object? DecodeConstant(LuaTable pool, int oneBasedIndex)
    {
        if (oneBasedIndex <= 0 || oneBasedIndex > pool.Array.Count) return null;
        var key = (pool, oneBasedIndex);
        if (ConstantCache.TryGetValue(key, out var cached)) return cached;

        object? d = pool.Array[oneBasedIndex - 1];
        object? result = d;
        if (d is LuaTable t)
        {
            if (IsPrototype(t)) result = t;
            else if (t.Fields.TryGetValue("_arith", out var ar) && ar is LuaTable at)
                result = DecodeArithmetic(at);
            else if (t.Array.Count > 0 && IsNumber(t.Array[0]))
                result = DecodeArithmetic(t);
            else if (t.Array.Count > 0 && t.Array[0] is string &&
                     (t.Array.Count < 2 || t.Array[1] is not LuaTable) &&
                     (t.Array.Count < 3 || !IsNumber(t.Array[2])))
                result = DecodeSegmentedString(t, oneBasedIndex);
        }
        else if (d is string s)
            result = DecodeSimpleString(s, oneBasedIndex);

        ConstantCache[key] = result;
        return result;
    }

    private static string DecodeArithmetic(LuaTable t)
    {
        var bytes = t.Array.Select(Int).ToArray();
        var outBytes = new byte[bytes.Length];
        int state = Mod(8335125 + bytes.Length * 17, 256);
        for (int i = 0; i < bytes.Length; i++)
        {
            int pos = i + 1;
            int e = Mod(4570875 + pos * 19 + state * 7, 256);
            int f = Mod(bytes[i] * 131 + e, 256);
            outBytes[i] = (byte)f;
            state = Mod(state * 41 + bytes[i] * 13 + f * 7, 256);
        }
        return Encoding.Latin1.GetString(outBytes);
    }

    private static string DecodeSimpleString(string encoded, int index)
    {
        var bytes = Sl(encoded);
        var outBytes = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            int pos = i + 1;
            int f = Mod(3495375 + index * 17 + pos * 19 + Key[Mod(index + pos - 1, Key.Length)], 256);
            outBytes[i] = (byte)Mod(bytes[i] - f, 256);
        }
        return Encoding.Latin1.GetString(outBytes);
    }

    private static string DecodeSegmentedString(LuaTable t, int index)
    {
        int mode = Mod(index * 13 + 119459, 4) + 1;
        var result = new List<byte>();
        for (int segmentIndex = 0; segmentIndex < t.Array.Count; segmentIndex++)
        {
            if (t.Array[segmentIndex] is not string segment) continue;
            var bytes = Sl(segment);
            int seg = segmentIndex + 1;
            int state = Mod(index * 37 + seg * 41 + 49189 + Mod(index * index * 3, 256), 256);
            for (int i = 0; i < bytes.Length; i++)
            {
                int pos = i + 1;
                int e = bytes[i];
                int decoded;
                if (mode == 1)
                {
                    int k = Key[Mod(index * 7 + seg * 11 + pos * 13 + state, Key.Length)];
                    int f = Mod(5108625 + index * 31 + seg * 47 + pos * 23 + k * 17 + state * 13, 256);
                    decoded = Mod(e - f, 256);
                    state = Mod(state * 31 + e * 17 + decoded * 7, 256);
                }
                else if (mode == 2)
                {
                    int k = Key[Mod(index * 17 + seg * 19 + pos * 23 + state, Key.Length)];
                    int inner = Mod(pos * 17 + index * 23 + seg * 43 + state * 7 + 75, 256);
                    int f = Mod(inner * pos + k * 13 + 59, 256);
                    decoded = Mod(e - f, 256);
                    state = Mod(state * 41 + e * 23 + decoded * 11 + 17, 256);
                }
                else if (mode == 3)
                {
                    int k = Key[Mod(index * 31 + seg * 19 + pos * 29 + state, Key.Length)];
                    int f = Mod(7797375 + index * 43 + seg * 53 + pos * 37 + k * 19 + state * 11 + 43, 256);
                    decoded = Mod(e - f, 256);
                    state = Mod(state * 47 + e * 29 + decoded * 13 + 31, 256);
                }
                else
                {
                    int k = Key[Mod(index * 13 + seg * 29 + pos * 17 + state, Key.Length)];
                    int f = Mod(2957625 + index * 19 + seg * 23 + pos * 31 + k * 7 + state * 5, 256);
                    decoded = Mod(Mod(e - f, 256) * 43, 256);
                    state = Mod(state * 53 + e * 29 + decoded * 13 + 37, 256);
                }
                result.Add((byte)decoded);
            }
        }
        return Encoding.Latin1.GetString(result.ToArray());
    }

    private static byte[] Sl(string s)
    {
        var output = new List<byte>();
        int pos = 0;
        while (pos < s.Length)
        {
            int count = Math.Min(5, s.Length - pos);
            long value = 0;
            for (int i = 0; i < 5; i++)
            {
                int digit = Alphabet.Length - 1;
                if (i < count && AlphabetMap.TryGetValue(s[pos + i], out int d)) digit = d;
                value = value * Alphabet.Length + digit;
            }
            pos += count;
            int outCount = count - 1;
            byte b4 = (byte)(value % 256); value /= 256;
            byte b3 = (byte)(value % 256); value /= 256;
            byte b2 = (byte)(value % 256); value /= 256;
            byte b1 = (byte)(value % 256);
            if (outCount >= 1) output.Add(b1);
            if (outCount >= 2) output.Add(b2);
            if (outCount >= 3) output.Add(b3);
            if (outCount >= 4) output.Add(b4);
        }
        return output.ToArray();
    }

    private static string BuildAlphabet()
    {
        var chars = BaseAlphabet.ToCharArray();
        long state = 0;
        for (int i = chars.Length; i >= 2; i--)
        {
            state = (state * 1103515245L + 12345) % 2147483648L;
            int j = (int)(state % i);
            (chars[i - 1], chars[j]) = (chars[j], chars[i - 1]);
        }
        return new string(chars);
    }

    private static byte[] Ts(int al, int bs, int mode)
    {
        const int dG = 268875, eN = 125;
        if (mode == 2)
        {
            int c = Mod(dG + al * 19, 256);
            int f = Mod(eN + bs * 31, 256);
            int g = Mod(f * 37 + dG * 13, 256);
            int h = Mod(c + g, 256);
            int ie = f;
            int jl = Mod(h * 73 + eN * 17, 256);
            int ks = Mod(ie + jl, 256);
            return new byte[] { (byte)h, (byte)ks,
                (byte)Mod(h * 53 + ks * 89 + al * 7, 256),
                (byte)Mod(ks * 97 + h * 11 + bs * 23, 256) };
        }
        if (mode == 3)
        {
            int c = Mod(al * 13 + bs, 256);
            int f = Mod(Mod(Mod(dG, 256) * c + eN, 256) * c + al * 17, 256);
            int g = Mod(Mod(eN * c + Mod(dG, 256), 256) * c + bs * 29, 256);
            g = Mod(g + 13, 256);
            int h = Mod(Mod(f * c + g, 256) * c + dG * 7, 256); h = Mod(h + 37, 256);
            int ie = Mod(Mod(g * c + h, 256) * c + eN * 19, 256); ie = Mod(ie + 71, 256);
            return new byte[] { (byte)f, (byte)g, (byte)h, (byte)ie };
        }
        return new byte[]
        {
            (byte)Mod(dG * 37 + al * 13 + bs * 7, 256),
            (byte)Mod(dG * 73 + al * 199 + eN * 17, 256),
            (byte)Mod(eN * 53 + al * 89 + bs * 23, 256),
            (byte)Mod(dG * 11 + eN * 97 + bs * 43, 256)
        };
    }

    private static LuaTable ExtractConstantPool(string source)
    {
        int marker = source.IndexOf("local _EG=", StringComparison.Ordinal);
        if (marker < 0) throw new InvalidDataException("no constant pool (_EG).");
        int start = source.IndexOf('{', marker);
        if (start < 0) throw new InvalidDataException("malformed _EG table.");
        var parser = new LuaLiteralParser(source, start);
        object? value = parser.ParseValue();
        return value as LuaTable ?? throw new InvalidDataException("_EG did not parse as a table.");
    }

    private static int ByteAt(byte[] b, int oneBased) => oneBased >= 1 && oneBased <= b.Length ? b[oneBased - 1] : 0;
    private static bool IsNumber(object? o) => o is double or int or long;
    private static int Int(object? o) => o switch { int i => i, long l => checked((int)l), double d => (int)d, _ => 0 };
    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
    private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    private static int Mod(long a, int m) { long r = a % m; return (int)(r < 0 ? r + m : r); }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "nil",
            bool b => b ? "true" : "false",
            string s => Quote(s),
            double d when Math.Abs(d - Math.Round(d)) < 1e-12 => ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            LuaTable t when IsPrototype(t) => "<PROTO>",
            LuaTable => "{}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "nil"
        };
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32 || c > 126) sb.Append("\\x").Append(((int)c).ToString("X2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public sealed class LuaTable
    {
        public List<object?> Array { get; } = new();
        public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    }

    private sealed class LuaLiteralParser
    {
        private readonly string _s;
        private int _p;
        public LuaLiteralParser(string s, int start) { _s = s; _p = start; }

        public object? ParseValue()
        {
            Skip();
            if (_p >= _s.Length) throw Error("unexpectde end of input");
            char c = _s[_p];
            if (c == '{') return ParseTable();
            if (c == '"' || c == '\'') return ParseString();
            if (c == '-' || c == '.' || char.IsDigit(c)) return ParseNumber();
            string id = ParseIdentifier();
            return id switch { "nil" => null, "true" => true, "false" => false, _ => id };
        }

        private LuaTable ParseTable()
        {
            Expect('{');
            var t = new LuaTable();
            Skip();
            while (_p < _s.Length && _s[_p] != '}')
            {
                Skip();
                if (_s[_p] == '[')
                {
                    _p++; var k = ParseValue(); Skip(); Expect(']'); Skip(); Expect('=');
                    var v = ParseValue();
                    if (k is double d && d >= 1 && Math.Abs(d - Math.Round(d)) < 1e-12)
                    {
                        int idx = (int)d;
                        while (t.Array.Count < idx) t.Array.Add(null);
                        t.Array[idx - 1] = v;
                    }
                    else t.Fields[Convert.ToString(k, CultureInfo.InvariantCulture) ?? ""] = v;
                }
                else if (IsIdentifierStart(_s[_p]))
                {
                    int save = _p; string id = ParseIdentifier(); Skip();
                    if (_p < _s.Length && _s[_p] == '=')
                    {
                        _p++; t.Fields[id] = ParseValue();
                    }
                    else
                    {
                        _p = save; t.Array.Add(ParseValue());
                    }
                }
                else t.Array.Add(ParseValue());
                Skip();
                if (_p < _s.Length && (_s[_p] == ',' || _s[_p] == ';')) { _p++; Skip(); }
            }
            Expect('}');
            return t;
        }

        private string ParseString()
        {
            char q = _s[_p++];
            var sb = new StringBuilder();
            while (_p < _s.Length)
            {
                char c = _s[_p++];
                if (c == q) return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_p >= _s.Length) break;
                char e = _s[_p++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case 'x':
                        if (_p + 1 <= _s.Length)
                        {
                            int len = Math.Min(2, _s.Length - _p);
                            if (int.TryParse(_s.AsSpan(_p, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x))
                            { sb.Append((char)x); _p += len; }
                        }
                        break;
                    case 'z': while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; break;
                    default:
                        if (char.IsDigit(e))
                        {
                            string digits = e.ToString();
                            for (int i = 0; i < 2 && _p < _s.Length && char.IsDigit(_s[_p]); i++) digits += _s[_p++];
                            sb.Append((char)int.Parse(digits, CultureInfo.InvariantCulture));
                        }
                        else sb.Append(e);
                        break;
                }
            }
            throw Error("unterminated string");
        }

        private double ParseNumber()
        {
            int start = _p;
            if (_s[_p] == '-') _p++;
            bool hex = _p + 1 < _s.Length && _s[_p] == '0' && (_s[_p + 1] == 'x' || _s[_p + 1] == 'X');
            if (hex)
            {
                _p += 2; int hs = _p;
                while (_p < _s.Length && Uri.IsHexDigit(_s[_p])) _p++;
                long v = long.Parse(_s.AsSpan(hs, _p - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return _s[start] == '-' ? -v : v;
            }
            while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] is '.' or 'e' or 'E' or '+' or '-'))
            {
                if ((_s[_p] == '+' || _s[_p] == '-') && _p > start && _s[_p - 1] is not ('e' or 'E')) break;
                _p++;
            }
            return double.Parse(_s.AsSpan(start, _p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private string ParseIdentifier()
        {
            Skip(); int start = _p;
            if (_p < _s.Length && IsIdentifierStart(_s[_p])) _p++;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
            if (_p == start) throw Error("expected value");
            return _s.Substring(start, _p - start);
        }

        private void Skip()
        {
            while (_p < _s.Length)
            {
                if (char.IsWhiteSpace(_s[_p])) { _p++; continue; }
                if (_p + 1 < _s.Length && _s[_p] == '-' && _s[_p + 1] == '-')
                {
                    _p += 2;
                    if (_p + 1 < _s.Length && _s[_p] == '[' && _s[_p + 1] == '[')
                    {
                        int end = _s.IndexOf("]]", _p + 2, StringComparison.Ordinal);
                        _p = end < 0 ? _s.Length : end + 2;
                    }
                    else while (_p < _s.Length && _s[_p] != '\n') _p++;
                    continue;
                }
                break;
            }
        }

        private void Expect(char c) { Skip(); if (_p >= _s.Length || _s[_p] != c) throw Error($"expected '{c}'"); _p++; }
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private Exception Error(string m) => new InvalidDataException($"{m} at offset {_p}.");
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<LuaTable>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public bool Equals(LuaTable? x, LuaTable? y) => ReferenceEquals(x, y);
        public int GetHashCode(LuaTable obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class RefTupleComparer : IEqualityComparer<(LuaTable, int)>
    {
        public bool Equals((LuaTable, int) x, (LuaTable, int) y) => ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((LuaTable, int) obj) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1), obj.Item2);
    }
}
