using System.Globalization;
using System.Text;

namespace ZeroLuaDeobfuscator;

public sealed class ZeroLua52
{
    private const string Alphabet = "3]1n&F#BjhmZdR^Je8DsSikI7<bVfCxX$*tM}r429zP{TG-ua%vQq?Uo;!+@0E[(L65>l)NYpyH_cAO|W=gwK";
    private readonly string _source;
    private LTable _constants = null!;
    private LTable _entry = null!;
    private readonly Dictionary<int, string> _decodedConstants = new();
    private readonly Dictionary<LTable, int> _prototypeIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LTable, List<Instruction>> _decodedPrototypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LTable, Dictionary<int, string>> _upvalueMaps = new(ReferenceEqualityComparer.Instance);

    public ZeroLua52(string source) => _source = source;

    public string Deobfuscate()
    {
        ParsePayload();
        IndexPrototypes();

        var sb = new StringBuilder();

        if (_prototypeIds.Count > 1)
        {
            var names = _prototypeIds.Where(x => !ReferenceEquals(x.Key, _entry)).OrderBy(x => x.Value).Select(x => $"__zero_fn_{x.Value}");
            sb.AppendLine("local " + string.Join(", ", names));
            sb.AppendLine();
        }

        var emitted = new HashSet<LTable>(ReferenceEqualityComparer.Instance);

        var root = _constants.Get(1) as LTable;
        if (root is not null && IsPrototype(root))
        {
            _upvalueMaps[root] = new Dictionary<int, string>();
            EmitPrototype(root, "fn_1", sb, emitted, false);

            foreach (var pair in _prototypeIds.Where(x => !ReferenceEquals(x.Key, _entry) && !ReferenceEquals(x.Key, root)).OrderBy(x => x.Value))
                EmitPrototype(pair.Key, $"fn_{pair.Value}", sb, emitted, false);

            sb.AppendLine("return __zero_fn_1()");
        }
        else
        {
            foreach (var pair in _prototypeIds.Where(x => !ReferenceEquals(x.Key, _entry)).OrderBy(x => x.Value))
                EmitPrototype(pair.Key, $"fn_{pair.Value}", sb, emitted, false);
            EmitPrototype(_entry, "main", sb, emitted, isRoot: true);
        }

        return CleanupGeneratedLua(sb.ToString());
    }

    private void ParsePayload()
    {
        var cMarker = "local vC=";
        var dMarker = ";local vD=";
        var c = FindTableAssignment(cMarker, 0);
        var d = c >= 0 ? FindTableAssignment(dMarker, c + cMarker.Length) : -1;
        if (c < 0 || d < 0 || d <= c)
            throw new InvalidDataException("Not a recognized ZERO LUA V5.2 payload.");

        c += cMarker.Length;
        d += dMarker.Length;
        _constants = new LuaValueParser(_source, c).ParseValue() as LTable
            ?? throw new InvalidDataException("Constant pool is not a Lua table.");
        _entry = new LuaValueParser(_source, d).ParseValue() as LTable
            ?? throw new InvalidDataException("Entry prototype is not a Lua table.");
    }

    private int FindTableAssignment(string marker, int start)
    {
        var p = start;
        while (p < _source.Length)
        {
            var hit = _source.IndexOf(marker, p, StringComparison.Ordinal);
            if (hit < 0) return -1;
            var value = hit + marker.Length;
            while (value < _source.Length && char.IsWhiteSpace(_source[value])) value++;
            if (value < _source.Length && _source[value] == '{') return hit;
            p = hit + marker.Length;
        }
        return -1;
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

    private List<Instruction> DecodePrototype(LTable proto)
    {
        if (_decodedPrototypes.TryGetValue(proto, out var cached)) return cached;

        var enc = (proto.Get(1) as LString)?.Value ?? "";
        var meta = proto.Get(2) as LTable ?? new LTable([], new());
        var bytes = BaseDecode(enc);
        var vm = Int(meta.Get(1));
        var mode = Int(meta.Get(3), 1);
        var packed = Int(meta.Get(4), 257);
        var seedMode = mode % 4; if (seedMode == 0) seedMode = 1;
        var perm = (mode / 4) % 8;
        var ks = KeyStream(Seed(vm, bytes.Length, seedMode), bytes.Length);
        var list = new List<Instruction>();

        var pc = 0;
        while (pc < bytes.Length)
        {
            var start = pc;
            int K(int off) => off < ks.Length ? ks[off] : 0;
            int B(int off) => off < bytes.Length ? bytes[off] : 0;
            var f = (B(pc) - K(pc) + 256) % 256;
            var g = (B(pc + 1) - K(pc + 1) + 256) % 256;
            var header = g * 256 + f - 32768;
            var opcode = Mod(header / 4 * 69 + 17, 256);
            var fields = Mod(header, 4) + 2;
            var a = Read16(pc + 2, bytes, ks);
            var b = fields >= 3 ? Read16(pc + 4, bytes, ks) : 0;
            var c = fields == 4 ? Read16(pc + 6, bytes, ks) : 0;
            if (fields == 4)
            {
                (a, b, c) = perm switch
                {
                    1 => (b, c, a), 2 => (c, a, b), 3 => (a, c, b),
                    4 => (b, a, c), 5 => (c, b, a), _ => (a, b, c)
                };
            }
            pc += fields * 2;
            list.Add(new Instruction(start, pc, opcode, a, b, c));
        }

        _decodedPrototypes[proto] = list;
        return list;
    }

    private void EmitPrototype(LTable proto, string name, StringBuilder sb, HashSet<LTable> emitted, bool isRoot = false)
    {
        if (!emitted.Add(proto)) return;
        var id = _prototypeIds.TryGetValue(proto, out var pid) ? pid : -1;
        var meta = proto.Get(2) as LTable ?? new LTable([], new());
        var paramCount = Math.Max(0, Int(meta.Get(2)));
        var args = string.Join(", ", Enumerable.Range(1, paramCount).Select(i => $"arg{i}"));

        if (!isRoot)
        {
            sb.AppendLine($"__zero_fn_{id} = function({args})");
        }

        var indent = isRoot ? "" : "    ";
        var code = DecodePrototype(proto);
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
            if (ins.Op >= 10) continue;
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
        if (raw is LNumber n) return Number(n.Value);
        if (raw is LBool b) return b.Value ? "true" : "false";
        if (raw is LNil) return "nil";
        return "{}";
    }

    private string DecodeConstant(int oneBased)
    {
        if (_decodedConstants.TryGetValue(oneBased, out var cached)) return cached;
        var raw = _constants.Get(oneBased);
        var vt = Seed(4629, 28, 1);

        if (raw is LString s)
        {
            var bytes = BaseDecode(s.Value);
            var chars = new char[bytes.Length];
            for (var i = 1; i <= bytes.Length; i++)
            {
                var add = (7214623 + oneBased * 17 + i * 19 + vt[(oneBased + i - 1) % vt.Length]) % 256;
                chars[i - 1] = (char)((bytes[i - 1] - add + 256) % 256);
            }
            return _decodedConstants[oneBased] = new string(chars);
        }

        if (raw is LTable t && t.Get(1) is LString && t.Get(2) is not LTable && t.Get(3) is not LNumber)
        {
            var output = new StringBuilder();
            var scheme = (oneBased * 13 + 78693) % 4 + 1;
            for (var partIndex = 1; partIndex <= t.Array.Count; partIndex++)
            {
                if (t.Get(partIndex) is not LString part) continue;
                var bytes = BaseDecode(part.Value);
                var state = (oneBased * 37 + partIndex * 41 + 32403) % 256;
                for (var i = 1; i <= bytes.Length; i++)
                {
                    var d = bytes[i - 1];
                    int add, decoded;
                    if (scheme == 1)
                    {
                        var e = vt[(oneBased * 7 + partIndex * 11 + i * 13 + state) % vt.Length];
                        add = (10544449 + oneBased * 31 + partIndex * 47 + i * 23 + e * 17 + state * 13) % 256;
                        decoded = (d - add + 256) % 256;
                        state = (state * 31 + d * 17 + decoded * 7) % 256;
                    }
                    else if (scheme == 2)
                    {
                        var e = vt[(oneBased * 17 + partIndex * 19 + i * 23 + state) % vt.Length];
                        add = (((i * 17 + oneBased * 23 + partIndex * 43 + state * 7 + 219) % 256) * i + e * 13 + 59) % 256;
                        decoded = (d - add + 256) % 256;
                        state = (state * 41 + d * 23 + decoded * 11 + 17) % 256;
                    }
                    else if (scheme == 3)
                    {
                        var e = vt[(oneBased * 31 + partIndex * 19 + i * 29 + state) % vt.Length];
                        add = (16094159 + oneBased * 43 + partIndex * 53 + i * 37 + e * 19 + state * 11 + 43) % 256;
                        decoded = (d - add + 256) % 256;
                        state = (state * 47 + d * 29 + decoded * 13 + 31) % 256;
                    }
                    else
                    {
                        var e = vt[(oneBased * 13 + partIndex * 29 + i * 17 + state) % vt.Length];
                        add = (6104681 + oneBased * 19 + partIndex * 23 + i * 31 + e * 7 + state * 5) % 256;
                        decoded = (((d - add + 256) % 256) * 43) % 256;
                        state = (state * 53 + d * 29 + decoded * 13 + 37) % 256;
                    }
                    output.Append((char)decoded);
                }
            }
            return _decodedConstants[oneBased] = output.ToString();
        }

        return raw switch
        {
            LNumber n => Number(n.Value), LBool b => b.Value ? "true" : "false", LNil => "nil", _ => ""
        };
    }

    private sealed class PrototypeDecompiler
    {
        private readonly ZeroLua52 _owner;
        private readonly LTable _proto;
        private readonly List<Instruction> _code;
        private readonly string _indent;
        private readonly Dictionary<int, string> _r = new();
        private readonly HashSet<int> _declared = new();
        private readonly HashSet<int> _labels = new();
        private readonly StringBuilder _sb = new();
        private int _temp;

        public PrototypeDecompiler(ZeroLua52 owner, LTable proto, List<Instruction> code, string indent, int paramCount)
        {
            _owner = owner;
            _proto = proto;
            _code = code;
            _indent = indent;
            for (var i = 0; i < paramCount; i++) _r[i] = $"arg{i + 1}";
            CollectLabels();
        }

        public string Run()
        {
            foreach (var i in _code)
            {
                if (_labels.Contains(i.Pc)) Line($"::L{i.Pc}::");
                Emit(i);
            }
            return _sb.ToString();
        }

        private void CollectLabels()
        {
            foreach (var i in _code)
            {
                if (i.Op >= 54 && i.Op < 73) _labels.Add(i.NextPc + i.A * 2);
                else if (i.Op >= 36 && i.Op < 48) _labels.Add(i.NextPc + 4);
                else if (i.Op >= 221 && i.Op < 225) _labels.Add(i.NextPc + 4);
                else if (i.Op >= 242 && i.Op < 249) _labels.Add(i.NextPc + 4);
                else if (i.Op >= 48 && i.Op < 50) _labels.Add(i.NextPc + i.B * 2);
                else if (i.Op >= 201 && i.Op < 202) _labels.Add(i.NextPc + i.B * 2);
            }
        }

        private void Emit(Instruction i)
        {
            string R(int x) => _r.TryGetValue(x, out var v) ? v : $"r{x}";
            string K(int x) => _owner.ConstantExpr(x);
            void Set(int reg, string expr) => _r[reg] = expr;

            if (i.Op < 10)
            {
                var raw = _owner._constants.Get(i.B + 1);
                if (raw is LTable child && IsPrototype(child))
                {
                    RegisterClosureCaptures(child);
                    Set(i.A, K(i.B));
                }
                else Set(i.A, K(i.B));
                return;
            }
            if (i.Op < 21) { Set(i.A, "select(1, ...)"); return; }
            if (i.Op < 23) { Set(i.A, Bin(R(i.B), "+", R(i.C))); return; }
            if (i.Op < 25)
            {
                Set(i.A, i.B != 0 ? "true" : "false");
                if (i.C != 0) Line($"goto L{i.NextPc + 4}");
                return;
            }
            if (i.Op < 26) { Set(i.A, $"#{Paren(R(i.B))}"); return; }
            if (i.Op < 28) { Set(i.A, Bin(R(i.B), "%", R(i.C))); return; }
            if (i.Op < 33) { EmitCall(i.A, i.B, i.C); return; }
            if (i.Op < 36) { Set(i.A, Index(R(i.B), K(i.C))); return; }
            if (i.Op < 48)
            {
                var cond = $"({R(i.B)} == {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            if (i.Op < 50) { Line($"goto L{i.NextPc + i.B * 2}"); return; }
            if (i.Op < 54) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 73) { Line($"goto L{i.NextPc + i.A * 2}"); return; }
            if (i.Op < 76) { Set(i.A, $"tostring({R(i.B)}) .. tostring({R(i.C)})"); return; }
            if (i.Op < 82) { Set(i.A, Bin(R(i.B), "+", R(i.C))); return; }
            if (i.Op < 87)
            {
                Set(i.A, R(i.B));
                if (i.C != 0) EmitCall(i.A, i.C, 1);
                return;
            }
            if (i.Op < 88) { Set(i.A, Bin(R(i.B), "*", R(i.C))); return; }
            if (i.Op < 97) { Set(i.A, Bin(R(i.B), "-", R(i.C))); return; }
            if (i.Op < 108) { Line($"{Index(R(i.A), R(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 109) { Set(i.A, $"-{Paren(R(i.B))}"); return; }
            if (i.Op < 110) { Line($"return {R(i.B)}"); return; }
            if (i.Op < 112) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 113) { Set(i.A, $"not {Paren(R(i.B))}"); return; }
            if (i.Op < 123)
            {
                for (var x = i.A; x <= i.B; x++) Set(x, "nil");
                return;
            }
            if (i.Op < 139) { Set(i.A, R(i.B)); return; }
            if (i.Op < 145) { Line($"{Upvalue(i.B)} = {R(i.A)}"); return; }
            if (i.Op < 149) { EmitCall(i.A, i.B, i.C); return; }
            if (i.Op < 154) { Set(i.A, Bin(R(i.B), "-", R(i.C))); return; }
            if (i.Op < 173) { Line("-- iterator/table operation"); return; }
            if (i.Op < 182)
            {
                var vals = Enumerable.Range(i.A, Math.Max(0, i.B - i.A + 1)).Select(R);
                Line("return " + string.Join(", ", vals));
                return;
            }
            if (i.Op < 185) { Set(i.A, Global(K(i.B))); return; }
            if (i.Op < 191)
            {
                var name = Unique("table");
                Line($"local {name} = {{}}");
                Set(i.A, name);
                return;
            }
            if (i.Op < 197) { Set(i.A, Bin(R(i.B), "/", R(i.C))); return; }
            if (i.Op < 199) { Set(i.A, R(i.B)); return; }
            if (i.Op < 201) { Set(i.A, Upvalue(i.B)); return; }
            if (i.Op < 202) { Line($"goto L{i.NextPc + i.B * 2}"); return; }
            if (i.Op < 213) { Line($"{Index(R(i.A), K(i.B))} = {R(i.C)}"); return; }
            if (i.Op < 216) { Set(i.A, Bin(R(i.B), "^", R(i.C))); return; }
            if (i.Op < 221) { Set(i.A, K(i.B)); return; }
            if (i.Op < 225)
            {
                var cond = $"({R(i.B)} < {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            if (i.Op < 230) { Set(i.A, Index(R(i.B), R(i.C))); return; }
            if (i.Op < 236)
            {
                var obj = R(i.B);
                Set(i.A, Index(obj, K(i.C)));
                Set(i.A + 1, obj);
                return;
            }
            if (i.Op < 242) { Line($"{Global(K(i.B))} = {R(i.A)}"); return; }
            if (i.Op < 249)
            {
                var cond = $"({R(i.B)} <= {R(i.C)})";
                if (i.A != 0) cond = $"not {cond}";
                Line($"if {cond} then goto L{i.NextPc + 4} end");
                return;
            }
            Set(i.A, Bin(R(i.B), "+", K(i.C)));
        }

        private string Upvalue(int index)
        {
            if (_owner._upvalueMaps.TryGetValue(_proto, out var map) && map.TryGetValue(index, out var name))
                return name;
            return $"upvalue_{index}";
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
            var args = new List<string>();
            for (var n = 1; n <= Math.Max(0, b); n++) args.Add(Expr(a + n));

            string call;
            if (TryMethodCall(fn, args, out var method)) call = method;
            else call = $"{fn}({string.Join(", ", args)})";

            if (c <= 0)
            {
                Line(call);
                return;
            }

            _r[a] = call;
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

        private string Expr(int r) => _r.TryGetValue(r, out var v) ? v : $"r{r}";

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
            var candidate = basis;
            var n = 2;
            var used = new HashSet<string>(_r.Values.Where(IsIdentifier), StringComparer.Ordinal);
            while (used.Contains(candidate)) candidate = basis + n++;
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

    private static int[] Seed(int a = 17, int b = 31, int mode = 1)
    {
        const int d = 554971, e = 112;
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

    private static int[] KeyStream(int[] seed, int count)
    {
        if (seed.Length == 0) seed = [17, 31, 53, 97];
        var outp = new int[count];
        var x = (seed[0] * 31 + (seed.Length > 1 ? seed[1] : 53)) % 256;
        var y = ((seed.Length > 2 ? seed[2] : 79) * 17 + (seed.Length > 3 ? seed[3] : 97)) % 256;
        for (var i = 1; i <= count; i++)
        {
            var g = seed[(i - 1) % seed.Length];
            x = (x * 37 + y * 13 + g + i * 19) % 256;
            y = (y * 53 + x * 23 + g * 7 + i * 11) % 256;
            outp[i - 1] = (x * 73 + y * 89 + g * 31) % 256;
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
