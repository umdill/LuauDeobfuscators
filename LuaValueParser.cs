using System.Globalization;
using System.Text;

namespace ZeroLuaDeobfuscator;

public abstract record LValue;
public sealed record LString(string Value) : LValue;
public sealed record LNumber(double Value) : LValue;
public sealed record LBool(bool Value) : LValue;
public sealed record LNil : LValue;
public sealed record LTable(List<LValue> Array, Dictionary<int, LValue> NumericKeys) : LValue
{
    public LValue? Get(int oneBased)
    {
        if (NumericKeys.TryGetValue(oneBased, out var keyed)) return keyed;
        var i = oneBased - 1;
        return i >= 0 && i < Array.Count ? Array[i] : null;
    }
}

public sealed class LuaValueParser
{
    private readonly string _s;
    private int _p;
    public int Position => _p;

    public LuaValueParser(string source, int start = 0) { _s = source; _p = start; }

    public LValue ParseValue()
    {
        Skip();
        if (_p >= _s.Length) throw Error("unexpected EOF");
        return _s[_p] switch
        {
            '{' => ParseTable(),
            '"' or '\'' => new LString(ParseString()),
            _ => ParseAtom()
        };
    }

    private LTable ParseTable()
    {
        Expect('{');
        var arr = new List<LValue>();
        var keys = new Dictionary<int, LValue>();
        Skip();
        while (_p < _s.Length && _s[_p] != '}')
        {
            Skip();
            if (_p < _s.Length && _s[_p] == '[')
            {
                _p++; Skip();
                var key = (int)ParseNumber();
                Skip(); Expect(']'); Skip(); Expect('=');
                keys[key] = ParseValue();
            }
            else arr.Add(ParseValue());
            Skip();
            if (_p < _s.Length && (_s[_p] == ',' || _s[_p] == ';')) { _p++; Skip(); }
            else if (_p < _s.Length && _s[_p] != '}') throw Error("expected ',' or '}'");
        }
        Expect('}');
        return new LTable(arr, keys);
    }

    private string ParseString()
    {
        var q = _s[_p++];
        var sb = new StringBuilder();
        while (_p < _s.Length)
        {
            var c = _s[_p++];
            if (c == q) return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (_p >= _s.Length) break;
            c = _s[_p++];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'z': while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; break;
                case 'x':
                    if (_p + 1 < _s.Length)
                    {
                        sb.Append((char)Convert.ToByte(_s.Substring(_p, 2), 16)); _p += 2;
                    }
                    break;
                default:
                    if (char.IsDigit(c))
                    {
                        var digits = c.ToString();
                        for (var i = 0; i < 2 && _p < _s.Length && char.IsDigit(_s[_p]); i++) digits += _s[_p++];
                        sb.Append((char)int.Parse(digits, CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        throw Error("unterminated string");
    }

    private LValue ParseAtom()
    {
        if (Match("nil")) return new LNil();
        if (Match("true")) return new LBool(true);
        if (Match("false")) return new LBool(false);
        return new LNumber(ParseNumber());
    }

    private double ParseNumber()
    {
        Skip();
        var start = _p;
        if (_p < _s.Length && (_s[_p] == '+' || _s[_p] == '-')) _p++;
        while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] is '.' or 'e' or 'E' or '+' or '-'))
        {
            if ((_s[_p] == '+' || _s[_p] == '-') && _p > start && _s[_p - 1] is not ('e' or 'E')) break;
            _p++;
        }
        var text = _s[start.._p];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) throw Error("bad number: " + text);
        return n;
    }

    private bool Match(string text)
    {
        if (_s.AsSpan(_p).StartsWith(text, StringComparison.Ordinal)) { _p += text.Length; return true; }
        return false;
    }
    private void Skip() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }
    private void Expect(char c) { Skip(); if (_p >= _s.Length || _s[_p] != c) throw Error($"expected '{c}'"); _p++; }
    private Exception Error(string msg) => new FormatException($"{msg} at {_p}");
}
