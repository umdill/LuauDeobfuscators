//mostly complete, i rushed this, and owner is putting out a new version anyway so i don't think i should finish it
using System.Globalization;
using System.Text;

namespace ZeroLuaDeobfuscator;

public sealed class ZeroLua56
{
    private const string Alphabet = "3]1n&F#BjhmZdR^Je8DsSikI7<bVfCxX$*tM}r429zP{TG-ua%vQq?Uo;!+@0E[(L65>l)NYpyH_cAO|W=gwK";
    private readonly string _source;
    private readonly bool _profileGodmode;
    private LTable _constants = null!;
    private LTable _entry = null!;
    private readonly Dictionary<int, string> _decodedConstants = new();
    private readonly Dictionary<LTable, int> _prototypeIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LTable, List<Instruction>> _decodedPrototypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LTable, Dictionary<int, string>> _upvalueMaps = new(ReferenceEqualityComparer.Instance);

    public ZeroLua56(string source)
    {
        _source = source;

        _profileGodmode = source.Contains("local _dG=565718;local _eN=11", StringComparison.Ordinal); // this was for testing godmode script, it should work on any tho
    }

    public string Deobfuscate()
    {
        ParsePayload();
        Console.WriteLine("[5.6] preparing...");
        IndexPrototypes();
        Console.WriteLine($"[5.6] {_prototypeIds.Count:N0} functions...");

        var sb = new StringBuilder();
        var emitted = new HashSet<LTable>(ReferenceEqualityComparer.Instance);

        EmitPrototype(_entry, "main", sb, emitted, isRoot: true);

        Console.WriteLine("[5.6] cleaning output...");
        var result = CleanupGeneratedLua(sb.ToString());
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException("empty output when virtualized"); // if vm cleanup returns empty
        Console.WriteLine("[5.6] clean complete.");
        return result;
    }

    private void ParsePayload() // checking for v5.6
    {
        if (!_source.Contains("ZERO LUA V5.6", StringComparison.OrdinalIgnoreCase) &&
            !_source.Contains("zerolua.pages.dev", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("unrecognized");

        var scalarLocals = ParseScalarLocals();
        var candidates = new List<(int Pos, string Name, LTable Table)>();
        for (var p = 0; p < _source.Length;)
        {
            var hit = _source.IndexOf("local ", p, StringComparison.Ordinal);
            if (hit < 0) break;
            var n0 = hit + 6;
            var n1 = n0;
            while (n1 < _source.Length && (char.IsLetterOrDigit(_source[n1]) || _source[n1] == '_')) n1++;
            if (n1 == n0) { p = hit + 6; continue; }
            var q = n1;
            while (q < _source.Length && char.IsWhiteSpace(_source[q])) q++;
            if (q >= _source.Length || _source[q] != '=') { p = n1; continue; }
            q++;
            while (q < _source.Length && char.IsWhiteSpace(_source[q])) q++;
            if (q >= _source.Length || _source[q] != '{') { p = q; continue; }

            var table = TryParseLiteralTable(q, scalarLocals);
            if (table is not null)
                candidates.Add((hit, _source[n0..n1], table));
            p = q + 1;
        }

        if (candidates.Count < 2)
            throw new InvalidDataException("couldnt find prototypes / virtualization");

        var entryCandidate = candidates
            .Where(x => IsPrototype(x.Table))
            .OrderByDescending(x => x.Pos)
            .FirstOrDefault();
        if (entryCandidate.Table is null)
            throw new InvalidDataException("no entry prototype");

        var poolCandidate = candidates
            .Where(x => x.Pos < entryCandidate.Pos && !ReferenceEquals(x.Table, entryCandidate.Table))
            .OrderByDescending(x => x.Table.Array.Count + x.Table.NumericKeys.Count)
            .ThenByDescending(x => x.Pos)
            .FirstOrDefault();
        if (poolCandidate.Table is null || poolCandidate.Table.Array.Count + poolCandidate.Table.NumericKeys.Count < 2)
            throw new InvalidDataException("no constant pool");

        _constants = poolCandidate.Table;
        _entry = entryCandidate.Table;
    }

    private Dictionary<string, int> ParseScalarLocals()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p < _source.Length;)
        {
            var hit = _source.IndexOf("local ", p, StringComparison.Ordinal);
            if (hit < 0) break;
            var n0 = hit + 6;
            var n1 = n0;
            while (n1 < _source.Length && (char.IsLetterOrDigit(_source[n1]) || _source[n1] == '_')) n1++;
            if (n1 == n0) { p = hit + 6; continue; }
            var name = _source[n0..n1];
            var q = n1;
            while (q < _source.Length && char.IsWhiteSpace(_source[q])) q++;
            if (q >= _source.Length || _source[q] != '=') { p = n1; continue; }
            q++;
            while (q < _source.Length && char.IsWhiteSpace(_source[q])) q++;
            if (q < _source.Length && _source[q] == '(') { q++; while (q < _source.Length && char.IsWhiteSpace(_source[q])) q++; }
            var sign = 1;
            if (q < _source.Length && _source[q] == '-') { sign = -1; q++; }
            var d0 = q;
            while (q < _source.Length && char.IsDigit(_source[q])) q++;
            if (q > d0 && int.TryParse(_source[d0..q], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                result[name] = sign * value;
            p = Math.Max(q, n1 + 1);
        }
        return result;
    }

    private LTable? TryParseLiteralTable(int start, Dictionary<string, int> scalarLocals)
    {
        try { return new LuaValueParser(_source, start).ParseValue() as LTable; }
        catch { }

        var end = FindTableEnd(start);
        if (end < 0) return null;
        var literal = _source[start..(end + 1)];
        var resolved = ResolveScalarIdentifiers(literal, scalarLocals);
        try { return new LuaValueParser(resolved).ParseValue() as LTable; }
        catch { return null; }
    }

    private int FindTableEnd(int start)
    {
        var depth = 0;
        char quote = '\0';
        var escaped = false;
        for (var i = start; i < _source.Length; i++)
        {
            var c = _source[i];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string ResolveScalarIdentifiers(string literal, Dictionary<string, int> scalars)
    {
        var sb = new StringBuilder(literal.Length);
        char quote = '\0';
        var escaped = false;
        for (var i = 0; i < literal.Length;)
        {
            var c = literal[i];
            if (quote != '\0')
            {
                sb.Append(c); i++;
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; sb.Append(c); i++; continue; }
            if (c == '_' || char.IsLetter(c))
            {
                var j = i + 1;
                while (j < literal.Length && (literal[j] == '_' || char.IsLetterOrDigit(literal[j]))) j++;
                var id = literal[i..j];
                if (scalars.TryGetValue(id, out var value)) sb.Append(value.ToString(CultureInfo.InvariantCulture));
                else sb.Append(id);
                i = j;
                continue;
            }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }
    private void IndexPrototypes()
    {
        _prototypeIds[_entry] = 0;
        var next = 1;
        for (var i = 1; i <= _constants.Array.Count; i++)
        {
            if (_constants.Get(i) is LTable t && IsPrototype(t) && !_prototypeIds.ContainsKey(t))
                _prototypeIds[t] = next++;
        }
    }

    private static bool IsPrototype(LTable t) => t.Get(1) is LString && t.Get(2) is LTable;

    private sealed record DecodeContext(byte[] Bytes, int[] KeyStream, int Perm, int OpcodeMul, int OpcodeAdd);

    private DecodeContext CreateDecodeContext(LTable proto)
    {
        var enc = (proto.Get(1) as LString)?.Value ?? "";
        var meta = proto.Get(2) as LTable ?? new LTable([], new());
        var bytes = BaseDecode(enc);

        var rawVm = Int(meta.Get(1));
        var rawMode = Int(meta.Get(3), 1);
        var vm = Mod(rawVm - (_profileGodmode ? 9617206 : 13839071), 65536);
        var mode = Mod(rawMode - (_profileGodmode ? 10748642 : 15467197), 65536);
        var seedMode = mode % 4; if (seedMode == 0) seedMode = 1;
        var perm = (mode / 4) % 8;
        var opcodeMul = Mod(vm * 31 + 17, 256); if (opcodeMul % 2 == 0) opcodeMul++;
        var opcodeAdd = Mod(vm * 43 + 71, 256);
        var ks = KeyStream(Seed(vm, bytes.Length, seedMode), bytes.Length, vm);
        return new DecodeContext(bytes, ks, perm, opcodeMul, opcodeAdd);
    }

    private static Instruction DecodeInstructionAt(DecodeContext ctx, int pc)
    {
        var bytes = ctx.Bytes;
        var ks = ctx.KeyStream;
        if (pc < 0 || pc + 1 >= bytes.Length)
            throw new InvalidDataException($"V5.6 attempted to decode outside bytecode at byte {pc}.");

        int K(int off) => off >= 0 && off < ks.Length ? ks[off] : 0;
        int B(int off) => off >= 0 && off < bytes.Length ? bytes[off] : 0;

        var f = Mod(B(pc) - K(pc), 256);
        var g = Mod(B(pc + 1) - K(pc + 1), 256);
        var header = g * 256 + f - 32768;
        var q = Mod(header, 4);
        var fields = q + 2;
        var baseOp = (int)Math.Floor(header / 4.0);
        var byteLength = fields * 2;

        var a = Read16(pc + 2, bytes, ks);
        var b = fields >= 3 ? Read16(pc + 4, bytes, ks) : 0;
        var c = fields == 4 ? Read16(pc + 6, bytes, ks) : 0;
        if (fields == 4)
        {
            (a, b, c) = ctx.Perm switch
            {
                1 => (b, c, a), 2 => (c, a, b), 3 => (a, c, b),
                4 => (b, a, c), 5 => (c, b, a), _ => (a, b, c)
            };
        }

        var oneBasedPc = pc + 1;
        var opcode = Mod(baseOp * ctx.OpcodeMul + ctx.OpcodeAdd + oneBasedPc * 13, 256);
        return new Instruction(pc, pc + byteLength, opcode, a, b, c);
    }

    private List<Instruction> DecodePrototype(LTable proto)
    {

        if (_decodedPrototypes.TryGetValue(proto, out var cached)) return cached;
        var ctx = CreateDecodeContext(proto);
        var list = new List<Instruction>();
        for (var pc = 0; pc < ctx.Bytes.Length;)
        {
            var i = DecodeInstructionAt(ctx, pc);
            list.Add(i);
            if (i.NextPc <= pc) break;
            pc = i.NextPc;
        }
        _decodedPrototypes[proto] = list;
        return list;
    }

    private List<Instruction> ReachableCode(LTable proto, List<Instruction> _)
    {
        var ctx = CreateDecodeContext(proto);
        if (ctx.Bytes.Length == 0) return [];

        var decoded = new Dictionary<int, Instruction>();
        var work = new Stack<int>();
        work.Push(0);

        void AddTarget(int pc, Instruction from)
        {

            if (pc >= ctx.Bytes.Length) return;
            if (pc < 0) pc = 0;
            if (!decoded.ContainsKey(pc)) work.Push(pc);
        }

        var guard = Math.Max(4096, ctx.Bytes.Length * 8);
        var steps = 0;
        while (work.Count > 0)
        {
            if (++steps > guard)
                throw new InvalidDataException($"V5.6 reachable decoder exceeded safety limit ({guard:N0} states). Possible corrupt opcode mapping.");

            var pc = work.Pop();
            if (decoded.ContainsKey(pc)) continue;
            var i = DecodeInstructionAt(ctx, pc);
            decoded[pc] = i;

            if (_profileGodmode)
            {
                if ((i.Op >= 53 && i.Op < 69) || (i.Op >= 108 && i.Op < 117))
                    continue;
                if (i.Op >= 24 && i.Op < 27)
                {
                    AddTarget(i.NextPc + i.A * 2, i);
                    continue;
                }
                if (i.Op < 2)
                {
                    AddTarget(i.NextPc, i);
                    AddTarget(i.NextPc + i.B * 2, i);
                    continue;
                }
                if (i.Op >= 137 && i.Op < 142)
                {
                    AddTarget(i.NextPc + i.B * 2, i);
                    continue;
                }
                if (i.Op == 95 || i.Op == 107 || (i.Op >= 222 && i.Op < 224) || i.Op == 226 || i.Op == 227)
                {
                    AddTarget(i.NextPc, i);
                    AddTarget(i.NextPc + 4, i);
                    continue;
                }
                AddTarget(i.NextPc, i);
                continue;
            }
            if (i.Op == 111 || (i.Op >= 169 && i.Op < 177))
                continue;

            if (i.Op >= 237 && i.Op < 246)
            {
                AddTarget(i.NextPc + i.A * 2, i);
                continue;
            }

            if (i.Op >= 31 && i.Op < 35)
            {
                AddTarget(i.NextPc, i);
                var target = i.NextPc + i.B * 2;
                AddTarget(target, i);
                continue;
            }

            if (i.Op >= 229 && i.Op < 237)
            {
                var target = i.NextPc + i.B * 2;
                AddTarget(target, i);
                continue;
            }

            if ((i.Op >= 26 && i.Op < 31) ||
                (i.Op >= 138 && i.Op < 149) ||
                (i.Op >= 246 && i.Op < 255))
            {
                AddTarget(i.NextPc, i);
                AddTarget(i.NextPc + 4, i);
                continue;
            }

            if (i.Op >= 213 && i.Op < 223)
            {
                AddTarget(i.NextPc, i);
                if (i.C != 0) AddTarget(i.NextPc + 4, i);
                continue;
            }

            AddTarget(i.NextPc, i);
        }

        return decoded.Values.OrderBy(x => x.Pc).ToList();
    }

    private void EmitPrototype(LTable proto, string name, StringBuilder sb, HashSet<LTable> emitted, bool isRoot = false)
    {
        if (!emitted.Add(proto)) return;
        var id = _prototypeIds.TryGetValue(proto, out var pid) ? pid : -1;
        if (id > 0 && (id <= 5 || id % 25 == 0))
            Console.WriteLine($"[5.6] Function {id}/{_prototypeIds.Count}");
        var meta = proto.Get(2) as LTable ?? new LTable([], new());
        var paramCount = Math.Max(0, Int(meta.Get(2)));
        var args = string.Join(", ", Enumerable.Range(1, paramCount).Select(i => $"arg{i}"));

        if (!isRoot)
        {
            sb.AppendLine($"__zero_fn_{id} = function({args})");
        }

        var indent = isRoot ? "" : "    ";
        var decoded = DecodePrototype(proto);
        var code = ReachableCode(proto, decoded);
        if (id > 0) Console.WriteLine($"[5.6]     {code.Count:N0} reachable instruction(s)");
        else Console.WriteLine($"[5.6] Entry: {code.Count:N0} reachable instruction(s)");
        var decompiler = new PrototypeDecompiler(this, proto, code, indent, paramCount);
        var body = decompiler.Run();
        sb.Append(body);

        if (!isRoot)
        {
            sb.AppendLine("end");
            sb.AppendLine();
        }

        foreach (var child in ReferencedPrototypeTables(code))
            EmitPrototype(child, $"fn_{_prototypeIds[child]}", sb, emitted, false);
    }

    private IEnumerable<LTable> ReferencedPrototypeTables(IEnumerable<Instruction> code)
    {
        var seen = new HashSet<LTable>(ReferenceEqualityComparer.Instance);
        foreach (var ins in code)
        {
            if (_profileGodmode)
            {
                if (ins.Op < 210 || ins.Op >= 222) continue;
            }
            else if (ins.Op < 166 || ins.Op >= 169) continue;
            var k = ins.B + 1;
            if (_constants.Get(k) is LTable t && IsPrototype(t) && seen.Add(t)) yield return t;
        }
    }

    private string ConstantExpr(int zeroBased)
    {
        var one = zeroBased + 1;
        var raw = _constants.Get(one);
        if (raw is LTable t && IsPrototype(t))
            return _prototypeIds.TryGetValue(t, out var id) ? $"__zero_fn_{id}" : "function(...) end";
        if (raw is LString) return Quote(DecodeConstant(one));
        if (raw is LTable st && st.Get(1) is LString && st.Get(2) is not LTable && st.Get(3) is not LNumber)
            return Quote(DecodeConstant(one));
        if (raw is LTable nt && nt.Get(1) is LNumber)
            return Quote(DecodeArithmeticTable(nt));
        if (raw is LNumber n) return Number(n.Value);
        if (raw is LBool b) return b.Value ? "true" : "false";
        if (raw is LNil) return "nil";
        return "{}";
    }

    private string DecodeConstant(int oneBased)
    {
        if (_decodedConstants.TryGetValue(oneBased, out var cached)) return cached;
        var raw = _constants.Get(oneBased);
        var vt = Seed(_profileGodmode ? 7477 : 1293, 230, 3);
        if (raw is LString s)
        {
            var bytes = BaseDecode(s.Value);
            var chars = new char[bytes.Length];
            for (var i = 1; i <= bytes.Length; i++)
            {
                var add = ((_profileGodmode ? 7354334 : 10582819) + oneBased * 17 + i * 19 + vt[(oneBased + i - 1) % vt.Length]) % 256;
                chars[i - 1] = (char)Mod(bytes[i - 1] - add, 256);
            }
            return _decodedConstants[oneBased] = new string(chars);
        }
        if (raw is LTable t && t.Get(1) is LString && t.Get(2) is not LTable && t.Get(3) is not LNumber)
        {
            var output = new StringBuilder();
            var scheme = (oneBased * 13 + (_profileGodmode ? 127109 : 21981)) % 4 + 1;
            for (var partIndex = 1; partIndex <= t.Array.Count; partIndex++)
            {
                if (t.Get(partIndex) is not LString part) continue;
                var bytes = BaseDecode(part.Value);
                var state = (oneBased * 37 + partIndex * 41 + (_profileGodmode ? 52339 : 9051)) % 256;
                for (var i = 1; i <= bytes.Length; i++)
                {
                    var d = bytes[i - 1];
                    int add, decoded;
                    if (scheme == 1)
                    {
                        var e = vt[(oneBased * 7 + partIndex * 11 + i * 13 + state) % vt.Length];
                        add = ((_profileGodmode ? 10748642 : 15467197) + oneBased * 31 + partIndex * 47 + i * 23 + e * 17 + state * 13) % 256;
                        decoded = Mod(d - add, 256);
                        state = (state * 31 + d * 17 + decoded * 7) % 256;
                    }
                    else if (scheme == 2)
                    {
                        var e = vt[(oneBased * 17 + partIndex * 19 + i * 23 + state) % vt.Length];
                        add = ((((i * 17 + oneBased * 23 + partIndex * 43 + state * 7 + (_profileGodmode ? 214 : 239)) % 256) * i + e * 13) + 59) % 256;
                        decoded = Mod(d - add, 256);
                        state = (state * 41 + d * 23 + decoded * 11 + 17) % 256;
                    }
                    else if (scheme == 3)
                    {
                        var e = vt[(oneBased * 31 + partIndex * 19 + i * 29 + state) % vt.Length];
                        add = ((_profileGodmode ? 16405822 : 23607827) + oneBased * 43 + partIndex * 53 + i * 37 + e * 19 + state * 11 + 43) % 256;
                        decoded = Mod(d - add, 256);
                        state = (state * 47 + d * 29 + decoded * 13 + 31) % 256;
                    }
                    else
                    {
                        var e = vt[(oneBased * 13 + partIndex * 29 + i * 17 + state) % vt.Length];
                        add = ((_profileGodmode ? 6222898 : 8954693) + oneBased * 19 + partIndex * 23 + i * 31 + e * 7 + state * 5) % 256;
                        decoded = (Mod(d - add, 256) * 43) % 256;
                        state = (state * 53 + d * 29 + decoded * 13 + 37) % 256;
                    }
                    output.Append((char)decoded);
                }
            }
            return _decodedConstants[oneBased] = output.ToString();
        }
        if (raw is LTable nt && nt.Get(1) is LNumber)
            return _decodedConstants[oneBased] = DecodeArithmeticTable(nt);
        return raw switch
        {
            LNumber n => Number(n.Value), LBool b => b.Value ? "true" : "false", LNil => "nil", _ => ""
        };
    }

    private string DecodeArithmeticTable(LTable table)
    {
        var count = table.Array.Count;
        var bytes = new byte[count];
        var state = ((_profileGodmode ? 17537258 : 25235953) + count * 17) % 256;
        for (var i = 1; i <= count; i++)
        {
            var v = Int(table.Get(i));
            var e = ((_profileGodmode ? 9617206 : 13839071) + i * 19 + state * 7) % 256;
            var f = (v * 131 + e) % 256;
            bytes[i - 1] = (byte)f;
            state = (state * 41 + v * 13 + f * 7) % 256;
        }
        return Encoding.Latin1.GetString(bytes);
    }
    private sealed class PrototypeDecompiler
    {
        private readonly ZeroLua56 _owner;
        private readonly LTable _proto;
        private readonly List<Instruction> _code;
        private readonly string _indent;
        private readonly Dictionary<int, string> _r = new();
        private readonly HashSet<int> _declared = new();
        private readonly HashSet<int> _labels = new();
        private readonly StringBuilder _sb = new();
        private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nameCounters = new(StringComparer.Ordinal);
        private const int MaxInlineExpressionLength = 320;
        private const int MaxCallArguments = 128;

        public PrototypeDecompiler(ZeroLua56 owner, LTable proto, List<Instruction> code, string indent, int paramCount)
        {
            _owner = owner;
            _proto = proto;
            _code = code;
            _indent = indent;
            for (var i = 0; i < paramCount; i++)
            {
                var arg = $"arg{i + 1}";
                _r[i] = arg;
                _usedNames.Add(arg);
            }
            CollectLabels();
        }

        public string Run()
        {
            var total = _code.Count;
            for (var index = 0; index < total; index++)
            {
                if (index > 0 && index % 10000 == 0)
                    Console.WriteLine($"[5.6]     instruction {index:N0}/{total:N0}");

                var i = _code[index];
                if (_labels.Contains(i.Pc)) Line($"::L{i.Pc}::");
                Emit(i);
            }
            return _sb.ToString();
        }

        private void CollectLabels()
        {
            foreach (var i in _code)
            {
                if (_owner._profileGodmode)
                {
                    if (i.Op >= 24 && i.Op < 27) _labels.Add(i.NextPc + i.A * 2);
                    else if (i.Op < 2 || (i.Op >= 137 && i.Op < 142)) _labels.Add(i.NextPc + i.B * 2);
                    else if (i.Op == 95 || i.Op == 107 || (i.Op >= 222 && i.Op < 224) || i.Op == 226 || i.Op == 227)
                        _labels.Add(i.NextPc + 4);
                    continue;
                }
                if ((i.Op >= 26 && i.Op < 31) || (i.Op >= 138 && i.Op < 149) ||
                    (i.Op >= 213 && i.Op < 223) || (i.Op >= 246 && i.Op < 255))
                    _labels.Add(i.NextPc + 4);
                else if ((i.Op >= 31 && i.Op < 35) || (i.Op >= 229 && i.Op < 237))
                    _labels.Add(i.NextPc + i.B * 2);
                else if (i.Op >= 237 && i.Op < 246)
                    _labels.Add(i.NextPc + i.A * 2);
            }
        }
        private void Emit(Instruction i)
        {
            if (_owner._profileGodmode) { EmitGodmodeProfile(i); return; }
            string R(int x) => _r.TryGetValue(x, out var v) ? v : RegName(x);
            string K(int x) => _owner.ConstantExpr(x);
            void Set(int reg, string expr)
            {
                if (expr.Length > MaxInlineExpressionLength)
                {
                    var temp = Unique("temp");
                    Line($"local {temp} = {expr}");
                    _r[reg] = temp;
                }
                else
                {
                    _r[reg] = expr;
                    if (IsIdentifier(expr)) _usedNames.Add(expr);
                }
            }

            if (i.Op < 7) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 9) { Set(i.A, Bin(R(i.B), "%", R(i.C))); return; }
            if (i.Op < 14) { for (var x = i.A; x <= i.B; x++) Set(x, "nil"); return; }
            if (i.Op < 21) { Set(i.A, R(i.B)); return; }
            if (i.Op < 25) { Set(i.A, $"not {Paren(R(i.B))}"); return; }
            if (i.Op < 26) { Set(i.A, R(i.B)); return; }
            if (i.Op < 31)
            {
                var cond = $"({R(i.B)} < {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            if (i.Op < 35)
            {
                Line($"{R(i.A)} = ({R(i.A)} or 0) + ({R(i.A + 2)} or 1)");
                Line($"goto L{i.NextPc + i.B * 2}");
                return;
            }
            if (i.Op < 36) { Set(i.A, Bin(R(i.B), "/", R(i.C))); return; }
            if (i.Op < 38)
            {
                if (i.B == 1) Set(i.A, "select(1, ...)");
                else if (i.B > 1) for (var n = 0; n < i.B; n++) Set(i.A + n, $"select({i.C + n + 1}, ...)");
                else Set(i.A, "{...}");
                return;
            }
            if (i.Op < 68) { Line($"{Index(R(i.A), R(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 71) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 74) { EmitCall(i.A, i.B, i.C); return; }
            if (i.Op < 77) { Line($"{Index(R(i.A), K(i.C))} = {Index(R(i.B), K(i.C))}"); return; }
            if (i.Op < 80) { Set(i.A, Bin(R(i.B), "+", R(i.C))); return; }
            if (i.Op < 82) { Set(i.A, $"tostring({R(i.B)} or \"\") .. tostring({R(i.C)} or \"\")"); return; }
            if (i.Op < 92) { Set(i.A, $"#{Paren(R(i.B))}"); return; }
            if (i.Op < 95) { Line($"{Index(R(i.A), K(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 98) { Set(i.A, Bin(R(i.B), "-", R(i.C))); return; }
            if (i.Op < 107) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 109) { Set(i.A, $"-{Paren(R(i.B))}"); return; }
            if (i.Op < 111) { Set(i.A, Bin(R(i.B), "^", R(i.C))); return; }
            if (i.Op < 112)
            {
                if (i.B == -1 || i.B < i.A) { Line("return"); return; }
                var vals = Enumerable.Range(i.A, i.B - i.A + 1).Select(R);
                Line("return " + string.Join(", ", vals));
                return;
            }
            if (i.Op < 115) { Line($"{Global(K(i.B))} = {R(i.A)}"); return; }
            if (i.Op < 123) { Set(i.A, Bin(R(i.B), "+", R(i.C))); return; }
            if (i.Op < 136) { Set(i.A, Bin(R(i.B), "-", R(i.C))); return; }
            if (i.Op < 138)
            {
                var obj = R(i.B);
                Set(i.A, $"{Paren(obj)}:GetService({K(i.C)})");
                return;
            }
            if (i.Op < 149)
            {
                var cond = $"({R(i.B)} == {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            if (i.Op < 151) { Set(i.A, "game:GetService(\"Players\").LocalPlayer.Character"); return; }
            if (i.Op < 153) { Set(i.A, Bin(R(i.B), "+", K(i.C))); return; }
            if (i.Op < 154) { Line($"{Upvalue(i.B)} = {R(i.A)}"); return; }
            if (i.Op < 166) { Set(i.A, Bin(R(i.B), "*", R(i.C))); return; }
            if (i.Op < 169)
            {
                var raw = _owner._constants.Get(i.B + 1);
                if (raw is LTable child && IsPrototype(child)) RegisterClosureCaptures(child);
                Set(i.A, K(i.B));
                return;
            }
            if (i.Op < 177) { Line($"return {R(i.B)}"); return; }
            if (i.Op < 183) { EmitCall(i.A, i.B, i.C); return; }
            if (i.Op < 187) { Set(i.A, Upvalue(i.B)); return; }
            if (i.Op < 196) { Set(i.A, Index(R(i.B), K(i.C))); return; }
            if (i.Op < 213) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 223)
            {
                Set(i.A, i.B != 0 ? "true" : "false");
                if (i.C != 0) Line($"goto L{i.NextPc + 4}");
                return;
            }
            if (i.Op < 229) { Set(i.A, K(i.B)); return; }
            if (i.Op < 237)
            {
                Line($"{R(i.A)} = ({R(i.A)} or 0) - ({R(i.A + 2)} or 1)");
                Line($"goto L{i.NextPc + i.B * 2}");
                return;
            }
            if (i.Op < 246) { Line($"goto L{i.NextPc + i.A * 2}"); return; }
            if (i.Op < 255)
            {
                var cond = $"({R(i.B)} <= {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            var name = Unique("table");
            Line($"local {name} = {{}}");
            Set(i.A, name);
        }
        private void EmitGodmodeProfile(Instruction i)
        {
            string R(int x) => Expr(x);
            string K(int x) => _owner.ConstantExpr(x);
            void Set(int reg, string expr)
            {
                if (expr.Length > MaxInlineExpressionLength)
                {
                    var temp = Unique("temp");
                    Line($"local {temp} = {expr}");
                    _r[reg] = temp;
                }
                else _r[reg] = expr;
            }

            if (i.Op < 2)
            {
                Line($"{R(i.A)} = ({R(i.A)} or 0) + ({R(i.A + 2)} or 1)");
                Line($"goto L{i.NextPc + i.B * 2}"); return;
            }
            if (i.Op < 4) { EmitCall(i.A, i.B, i.C); return; }
            if (i.Op < 17) { Set(i.A, $"not {Paren(R(i.B))}"); return; }
            if (i.Op < 18) { Line($"{Index(R(i.A), K(i.C))} = {Index(R(i.B), K(i.C))}"); return; }
            if (i.Op < 19)
            {
                Set(i.A, Global(K(i.B)));
                EmitCall(i.A, i.C, 1);
                return;
            }
            if (i.Op < 20) { Line($"{Index(R(i.A), R(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 24) { Set(i.A, $"{Paren(R(i.B))}:GetService({K(i.C)})"); return; }
            if (i.Op < 27) { Line($"goto L{i.NextPc + i.A * 2}"); return; }
            if (i.Op < 30) { var n=Unique("table"); Line($"local {n} = {{}}"); Set(i.A,n); return; }
            if (i.Op < 37) { Set(i.A, Bin(R(i.B), "/", R(i.C))); return; }
            if (i.Op < 43) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 47) { Set(i.A, Bin(R(i.B), "+", K(i.C))); return; }
            if (i.Op < 51) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 53) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 69)
            {
                if (i.B == -1 || i.B < i.A) { Line("return"); return; }
                var vals=Enumerable.Range(i.A, i.B-i.A+1).Select(R); Line("return "+string.Join(", ",vals)); return;
            }
            if (i.Op < 70) { Set(i.A, Bin(R(i.B), "%", R(i.C))); return; }
            if (i.Op < 81) { Set(i.A, Upvalue(i.B)); return; }
            if (i.Op < 90) { Line($"{Global(K(i.B))} = {R(i.A)}"); return; }
            if (i.Op < 94) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 95) { Set(i.A, $"-{Paren(R(i.B))}"); return; }
            if (i.Op < 96)
            {
                var cond=$"({R(i.B)} <= {R(i.C)})"; if(i.A!=0) cond=$"not {cond}"; Line($"if {cond} then goto L{i.NextPc+4} end"); return;
            }
            if (i.Op < 107) { Set(i.A, Bin(R(i.B), "^", R(i.C))); return; }
            if (i.Op < 108) { Set(i.A, i.B != 0 ? "true" : "false"); if(i.C!=0) Line($"goto L{i.NextPc+4}"); return; }
            if (i.Op < 117) { Line($"return {R(i.B)}"); return; }
            if (i.Op < 118) { Set(i.A, Bin(R(i.B), "*", R(i.C))); return; }
            if (i.Op < 127) { Set(i.A, $"tostring({R(i.B)} or \"\") .. tostring({R(i.C)} or \"\")"); return; }
            if (i.Op < 136) { Set(i.A, R(i.B)); return; }
            if (i.Op < 137)
            {
                if (i.B == -1) Set(i.A, "{...}");
                else if (i.B == 1) Set(i.A, "select(1, ...)");
                else if (i.B > 1) for (var n=0;n<i.B;n++) Set(i.A+n,$"select({i.C+n+1}, ...)");
                return;
            }
            if (i.Op < 142)
            {
                Line($"{R(i.A)} = ({R(i.A)} or 0) - ({R(i.A + 2)} or 1)");
                Line($"goto L{i.NextPc + i.B * 2}"); return;
            }
            if (i.Op < 151) { Set(i.A, Index(R(i.B), K(i.C))); return; }
            if (i.Op < 163) { Set(i.A, Bin(R(i.B), "-", R(i.C))); return; }
            if (i.Op < 172) { for(var x=i.A;x<=i.B;x++) Set(x,"nil"); return; }
            if (i.Op < 200) { Set(i.A, Bin(R(i.B), "+", R(i.C))); return; }
            if (i.Op < 210) { Set(i.A, K(i.B)); return; }
            if (i.Op < 222)
            {
                var raw=_owner._constants.Get(i.B+1); if(raw is LTable child && IsPrototype(child)) RegisterClosureCaptures(child); Set(i.A,K(i.B)); return;
            }
            if (i.Op < 224)
            {
                var cond=$"({R(i.B)} < {R(i.C)})"; if(i.A!=0) cond=$"not {cond}"; Line($"if {cond} then goto L{i.NextPc+4} end"); return;
            }
            if (i.Op < 226) { Set(i.A,$"#{Paren(R(i.B))}"); return; }
            if (i.Op < 227)
            {
                var cond=$"({R(i.B)} == {R(i.C)})"; if(i.A!=0) cond=$"not {cond}"; Line($"if {cond} then goto L{i.NextPc+4} end"); return;
            }
            if (i.Op < 228) { Set(i.A,Bin(R(i.B),"+",R(i.C))); if(i.C>0) Line($"goto L{i.NextPc+4}"); return; }
            if (i.Op < 232) { Line($"{Upvalue(i.B)} = {R(i.A)}"); return; }
            if (i.Op < 233) { Set(i.A,Bin(R(i.B),"-",R(i.C))); return; }
            if (i.Op < 234) { Line($"{Index(R(i.A), K(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 237) { EmitCall(i.A,i.B,i.C); return; }
            if (i.Op < 241) { Set(i.A,R(i.B)); return; }
            Set(i.A,Bin(R(i.B),"+",R(i.C)));
        }

        private string Upvalue(int index)
        {
            if (_owner._upvalueMaps.TryGetValue(_proto, out var map) && map.TryGetValue(index, out var name))
                return name;
            return index < 0 ? $"upvalue_m{-index}" : $"upvalue_{index}";
        }

        private string CaptureRegister(int reg)
        {
            var expr = Expr(reg);
            if (IsIdentifier(expr)) return expr;

            var name = Unique("captured");
            Line($"local {name} = {expr}");
            _r[reg] = name;
            return name;
        }

        private void RegisterClosureCaptures(LTable child)
        {
            var descs = child.Get(3) as LTable;
            if (descs is null) return;

            var map = new Dictionary<int, string>();
            for (var n = 1; n <= descs.Array.Count; n++)
            {
                if (descs.Get(n) is not LTable d) continue;
                var kind = Int(d.Get(1));
                var slot = Int(d.Get(2));
                map[n - 1] = kind == 1 ? Upvalue(slot) : CaptureRegister(slot);
            }

            if (!_owner._upvalueMaps.ContainsKey(child))
                _owner._upvalueMaps[child] = map;
        }

        private void EmitCall(int a, int b, int c)
        {
            var fn = Expr(a);
            var varargCall = b == -1;
            var requestedArgs = varargCall ? 0 : Math.Max(0, b);
            var argCount = Math.Min(requestedArgs, MaxCallArguments);
            var args = new List<string>(argCount + (varargCall ? 1 : 0));
            for (var n = 1; n <= argCount; n++)
            {
                var reg = a + n;
                var expr = Expr(reg);

                if (IsLiteral(expr))
                {
                    var basis = SuggestLocalName(fn, n, expr);
                    var name = Unique(basis);
                    Line($"local {name} = {expr}");
                    _r[reg] = name;
                    expr = name;
                }

                args.Add(expr);
            }
            if (varargCall) args.Add("...");
            if (requestedArgs > MaxCallArguments)
                args.Add($"--[[ {requestedArgs - MaxCallArguments} additional VM args omitted ]] ");

            string call;
            if (TryMethodCall(fn, args, out var method)) call = method;
            else call = $"{fn}({string.Join(", ", args)})";

            if (c == 0)
            {
                Line(call);
                return;
            }

            _r[a] = call;
            if (c > 1)
                for (var n = 1; n < c; n++) _r[a + n] = $"select({n + 1}, {call})";

            if (LooksImportantCall(call))
            {
                var name = SuggestName(call);
                if (!string.IsNullOrEmpty(name))
                {
                    Line($"local {name} = {call}");
                    _r[a] = name;
                }
            }
        }

        private string Expr(int r) => _r.TryGetValue(r, out var v) ? v : RegName(r);

        private static string RegName(int r) => r < 0 ? $"r_m{-r}" : $"r{r}";

        private static bool IsLiteral(string s)
        {
            if (s is "true" or "false" or "nil") return true;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
            return s.Length >= 2 && s[0] == '"' && s[^1] == '"';
        }

        private static string SuggestLocalName(string fn, int argumentIndex, string expr)
        {
            if (fn == "print" && argumentIndex == 1 && expr.Length >= 2 && expr[0] == '"')
                return "message";
            if (expr.Length >= 2 && expr[0] == '"') return "text";
            if (expr is "true" or "false") return "flag";
            if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return "value";
            return "localValue";
        }

        private static bool TryMethodCall(string fn, List<string> args, out string call)
        {
            call = "";
            if (args.Count == 0) return false;
            var dot = fn.LastIndexOf('.');
            if (dot <= 0) return false;
            var obj = fn[..dot];
            var member = fn[(dot + 1)..];
            if (!IsIdentifier(member)) return false;
            if (args[0] != obj) return false;
            call = $"{obj}:{member}({string.Join(", ", args.Skip(1))})";
            return true;
        }

        private static bool LooksImportantCall(string call) =>
            call.Contains(":GetService(", StringComparison.Ordinal) ||
            call.StartsWith("Instance.new(", StringComparison.Ordinal) ||
            call.Contains(":FindFirstChild(", StringComparison.Ordinal) ||
            call.Contains(":WaitForChild(", StringComparison.Ordinal);

        private string SuggestName(string call)
        {
            if (call.Contains(":GetService(", StringComparison.Ordinal))
            {
                var q1 = call.IndexOf('"');
                var q2 = q1 >= 0 ? call.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1)
                {
                    var s = call[(q1 + 1)..q2];
                    if (IsIdentifier(s)) return Unique(s);
                }
            }
            if (call.StartsWith("Instance.new(\"", StringComparison.Ordinal))
            {
                var q2 = call.IndexOf('"', 14);
                if (q2 > 14)
                {
                    var cls = call[14..q2];
                    var baseName = char.ToLowerInvariant(cls[0]) + cls[1..];
                    return Unique(baseName);
                }
            }
            return "";
        }

        private string Unique(string basis)
        {
            if (!_usedNames.Contains(basis))
            {
                _usedNames.Add(basis);
                _nameCounters[basis] = 2;
                return basis;
            }

            var n = _nameCounters.TryGetValue(basis, out var next) ? next : 2;
            string candidate;
            do candidate = basis + n++; while (_usedNames.Contains(candidate));
            _nameCounters[basis] = n;
            _usedNames.Add(candidate);
            return candidate;
        }

        private static string Global(string quoted)
        {
            if (quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"')
            {
                var s = UnquoteSimple(quoted);
                if (IsIdentifier(s)) return s;
                return $"_G[{quoted}]";
            }
            return $"_G[{quoted}]";
        }

        private static string Index(string obj, string key)
        {
            if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
            {
                var s = UnquoteSimple(key);
                if (IsIdentifier(s)) return $"{ParenMember(obj)}.{s}";
            }
            return $"{ParenMember(obj)}[{key}]";
        }

        private static string Bin(string l, string op, string r) => $"{Paren(l)} {op} {Paren(r)}";
        private static string Paren(string s) => IsSimple(s) ? s : $"({s})";
        private static string ParenMember(string s) => IsSimpleMemberBase(s) ? s : $"({s})";
        private static bool IsSimple(string s) => IsIdentifier(s) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || s is "true" or "false" or "nil" || (s.StartsWith('"') && s.EndsWith('"'));
        private static bool IsSimpleMemberBase(string s) => IsIdentifier(s) || s.Contains('.') || s.Contains(':') || s.EndsWith(']');
        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (var i = 1; i < s.Length; i++) if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
            return s is not ("and" or "break" or "do" or "else" or "elseif" or "end" or "false" or "for" or "function" or "goto" or "if" or "in" or "local" or "nil" or "not" or "or" or "repeat" or "return" or "then" or "true" or "until" or "while");
        }
        private static string UnquoteSimple(string s) => s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        private void Line(string text) => _sb.Append(_indent).AppendLine(text);
    }

    private static string CleanupGeneratedLua(string text)
    {

        var recovered = TryRecoverProtectedChunk(text);
        if (recovered is not null && IsSafeStraightLineRecovery(recovered))
            return recovered;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var payload = lines.FindIndex(x => x.Contains("game:GetService(\"", StringComparison.Ordinal));
        if (payload >= 0)
        {
            var rootHeader = -1;
            for (var i = payload; i >= 0; i--)
            {
                if (lines[i].TrimStart().StartsWith("__zero_fn_1 = function(", StringComparison.Ordinal))
                {
                    rootHeader = i;
                    break;
                }
            }
            if (rootHeader >= 0 && payload > rootHeader + 1)
                lines.RemoveRange(rootHeader + 1, payload - rootHeader - 1);

            var repeated = -1;
            for (var i = rootHeader + 1; i < lines.Count; i++)
            {
                if (lines[i].Contains("if (not _G == true) then goto", StringComparison.Ordinal))
                {
                    var sawConnect = false;
                    for (var j = rootHeader + 1; j < i; j++)
                        if (lines[j].Contains(":Connect(__zero_fn_", StringComparison.Ordinal)) { sawConnect = true; break; }
                    if (sawConnect) { repeated = i; break; }
                }
            }
            if (repeated >= 0)
            {
                var rootEnd = -1;
                for (var i = repeated; i < lines.Count; i++)
                {
                    if (lines[i].Trim() == "end") { rootEnd = i; break; }
                }
                if (rootEnd > repeated)
                    lines.RemoveRange(repeated, rootEnd - repeated);
            }
        }

        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith("goto L", StringComparison.Ordinal)) continue;
            var target = t[5..].Trim();
            if (i + 1 < lines.Count && lines[i + 1].Trim() == $"::{target}::")
                lines[i] = "";
        }

        var outp = new List<string>();
        var blank = false;
        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && blank) continue;
            outp.Add(line);
            blank = isBlank;
        }
        return string.Join(Environment.NewLine, outp).TrimEnd() + Environment.NewLine;
    }

    private static bool IsSafeStraightLineRecovery(string recovered)
    {

        var bad = new[]
        {
            "goto ", "::L", "if ", "for ", "while ", "repeat", "function",
            "__zero_fn_", "upvalue_", "select(", "r0", "r1", "r2", "r3", "r4", "r5"
        };
        foreach (var token in bad)
            if (recovered.Contains(token, StringComparison.Ordinal))
                return false;
        return recovered.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 32;
    }

    private static string? TryRecoverProtectedChunk(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var rootStart = lines.FindIndex(x => x.TrimStart().StartsWith("__zero_fn_1 = function(", StringComparison.Ordinal));
        if (rootStart < 0) return null;

        var rootEnd = -1;
        for (var i = rootStart + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "end")
            {
                rootEnd = i;
                break;
            }
        }
        if (rootEnd < 0) return null;

        var k0 = -1;
        var markerBase = "";
        for (var i = rootStart + 1; i < rootEnd; i++)
        {
            var t = lines[i].Trim();
            var pos = t.IndexOf("._k0 = ", StringComparison.Ordinal);
            if (!t.StartsWith("table", StringComparison.Ordinal) || pos <= 0) continue;

            var candidate = t[..pos];
            if (i + 2 >= rootEnd) continue;
            if (!lines[i + 1].Trim().StartsWith(candidate + "._k1 = ", StringComparison.Ordinal)) continue;
            if (!lines[i + 2].Trim().StartsWith(candidate + "._k2 = ", StringComparison.Ordinal)) continue;

            k0 = i;
            markerBase = candidate;
            break;
        }
        if (k0 < 0) return null;

        var chunkEnd = rootEnd;
        for (var i = k0 + 3; i < rootEnd; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("if (not _G == true) then goto ", StringComparison.Ordinal) ||
                t.StartsWith("if not (not _G == true) then goto ", StringComparison.Ordinal))
            {
                chunkEnd = i;
                break;
            }
        }

        var firstReal = -1;
        for (var i = k0 + 3; i < chunkEnd; i++)
        {
            var t = lines[i].Trim();
            if (IsLeadingZeroLuaNoise(t, markerBase)) continue;
            firstReal = i;
            break;
        }
        if (firstReal < 0) return null;

        var payload = new List<string>();
        for (var i = firstReal; i < chunkEnd; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (string.IsNullOrWhiteSpace(t)) continue;

            if (t.StartsWith("::L", StringComparison.Ordinal) && t.EndsWith("::", StringComparison.Ordinal))
                continue;
            if (t.StartsWith("goto L", StringComparison.Ordinal))
                continue;

            payload.Add(t);
        }

        while (payload.Count > 0 && payload[^1] == "return") payload.RemoveAt(payload.Count - 1);
        if (payload.Count == 0) return null;

        return string.Join(Environment.NewLine, payload).TrimEnd() + Environment.NewLine;
    }

    private static bool IsLeadingZeroLuaNoise(string t, string markerBase)
    {
        if (string.IsNullOrWhiteSpace(t)) return true;
        if (t.StartsWith("::L", StringComparison.Ordinal) && t.EndsWith("::", StringComparison.Ordinal)) return true;
        if (t.StartsWith("goto L", StringComparison.Ordinal)) return true;
        if (t.StartsWith("if ", StringComparison.Ordinal) && t.Contains(" then goto L", StringComparison.Ordinal)) return true;
        if (t.StartsWith("if not ", StringComparison.Ordinal) && t.Contains(" then goto L", StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(markerBase) && t.StartsWith(markerBase + "._k", StringComparison.Ordinal)) return true;
        return false;
    }

    private sealed record Instruction(int Pc, int NextPc, int Op, int A, int B, int C);

    private static byte[] BaseDecode(string input)
    {
        var map = new Dictionary<byte, int>();
        for (var i = 0; i < Alphabet.Length; i++) map[(byte)Alphabet[i]] = i;
        var n = Alphabet.Length;
        var pad = n - 1;
        var result = new List<byte>();
        for (var p = 0; p < input.Length;)
        {
            var left = input.Length - p;
            var g = Math.Min(5, left);
            long acc = 0;
            for (var i = 0; i < 5; i++)
            {
                var x = pad;
                if (i < g)
                {
                    var ch = (byte)input[p + i];
                    if (!map.TryGetValue(ch, out x)) x = 0;
                }
                acc = acc * n + x;
            }
            p += g;
            var count = g - 1;
            var b4 = (byte)(acc % 256); acc /= 256;
            var b3 = (byte)(acc % 256); acc /= 256;
            var b2 = (byte)(acc % 256); acc /= 256;
            var b1 = (byte)(acc % 256);
            if (count >= 1) result.Add(b1);
            if (count >= 2) result.Add(b2);
            if (count >= 3) result.Add(b3);
            if (count >= 4) result.Add(b4);
        }
        return result.ToArray();
    }

    private int[] Seed(int a = 17, int b = 31, int mode = 1)
    {
        var d = _profileGodmode ? 565718 : 814063;
        var e = _profileGodmode ? 11 : 156;
        if (mode == 2)
        {
            var c = (d + a * 19) % 256; var f = (e + b * 31) % 256;
            var g = (f * 37 + d * 13) % 256; var h = (c + g) % 256;
            var j = (h * 73 + e * 17) % 256; var k = (f + j) % 256;
            return [h, k, (h * 53 + k * 89 + a * 7) % 256, (k * 97 + h * 11 + b * 23) % 256];
        }
        if (mode == 3)
        {
            var c = (a * 13 + b) % 256;
            var f = (((d % 256) * c + e) * c + a * 17) % 256;
            var g = (((e * c + d % 256) * c + b * 29) % 256 + 13) % 256;
            var h = (((f * c + g) * c + d * 7) % 256 + 37) % 256;
            var i = (((g * c + h) * c + e * 19) % 256 + 71) % 256;
            return [f, g, h, i];
        }
        return [(d * 37 + a * 13 + b * 7) % 256, (d * 73 + a * 199 + e * 17) % 256,
            (e * 53 + a * 89 + b * 23) % 256, (d * 11 + e * 97 + b * 43) % 256];
    }

    private static int[] KeyStream(int[] seed, int count, int salt = 17)
    {
        if (seed.Length == 0) seed = [17, 31, 53, 97];
        var outp = new int[count];
        var s1 = seed.Length > 0 ? seed[0] : 17;
        var s2 = seed.Length > 1 ? seed[1] : 31;
        var s3 = seed.Length > 2 ? seed[2] : 53;
        var s4 = seed.Length > 3 ? seed[3] : 97;
        var x = (s1 * 31 + s2 * 53 + salt * 19) % 256;
        var y = (s3 * 17 + s4 * 97 + salt * 37) % 256;
        for (var i = 1; i <= count; i++)
        {
            var g = seed[(i - 1) % seed.Length];
            x = (x * 37 + y * 13 + g + i * 19) % 256;
            y = (y * 53 + x * 23 + g * 7 + i * 11) % 256;
            var h = (x * 73 + y * 89 + g * 31 + salt * 43) % 256;
            var j = (y * 47 + x * 59 + g * 17 + i * 29) % 256;
            outp[i - 1] = (h * 53 + j * 67 + i * 41) % 256;
        }
        return outp;
    }

    private static int Read16(int p, byte[] bytes, int[] ks)
    {
        var lo = p < bytes.Length ? (bytes[p] - ks[p] + 256) % 256 : 0;
        var hi = p + 1 < bytes.Length ? (bytes[p + 1] - ks[p + 1] + 256) % 256 : 0;
        return hi * 256 + lo - 32768;
    }

    private static int Int(LValue? v, int fallback = 0) => v is LNumber n ? (int)n.Value : fallback;
    private static int Mod(int x, int m) => (x % m + m) % m;
    private static string Number(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    private static string Quote(string s)
    {
        s = RepairUtf8Mojibake(s);
        var clean = s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\"", "\\\"");
        return '"' + string.Concat(clean.Select(ch => char.IsControl(ch) ? $"\\x{(int)ch:X2}" : ch.ToString())) + '"';
    }

    private static string RepairUtf8Mojibake(string s)
    {
        if (!s.Any(ch => ch >= 0x80 && ch <= 0xFF)) return s;
        try
        {
            var bytes = s.Select(ch => (byte)(ch & 0xFF)).ToArray();
            var utf8 = new UTF8Encoding(false, true).GetString(bytes);
            return utf8.Any(ch => ch > 0xFF) ? utf8 : s;
        }
        catch { return s; }
    }
}
