namespace WRDDeobfuscator;

internal sealed class IntegerExpression
{
    private readonly string _s;
    private int _p;

    private IntegerExpression(string s) => _s = s;

    public static bool TryEvaluate(string s, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (char c in s)
            if (!(char.IsDigit(c) || char.IsWhiteSpace(c) || "+-*/%^()".Contains(c)))
                return false;

        try
        {
            var p = new IntegerExpression(s);
            value = p.Expr();
            p.Ws();
            return p._p == p._s.Length;
        }
        catch { return false; }
    }

    private long Expr()
    {
        long v = Term();
        while (true)
        {
            Ws();
            if (Eat('+')) v = checked(v + Term());
            else if (Eat('-')) v = checked(v - Term());
            else return v;
        }
    }

    private long Term()
    {
        long v = Power();
        while (true)
        {
            Ws();
            if (Eat('*')) v = checked(v * Power());
            else if (Eat('/')) { long r = Power(); if (r == 0) throw new DivideByZeroException(); v /= r; }
            else if (Eat('%')) { long r = Power(); if (r == 0) throw new DivideByZeroException(); v %= r; }
            else return v;
        }
    }

    private long Power()
    {
        long l = Unary();
        Ws();
        if (!Eat('^')) return l;
        long e = Power();
        if (e < 0 || e > 63) throw new OverflowException();
        long r = 1, b = l;
        while (e > 0)
        {
            if ((e & 1) != 0) r = checked(r * b);
            e >>= 1;
            if (e != 0) b = checked(b * b);
        }
        return r;
    }

    private long Unary()
    {
        Ws();
        if (Eat('+')) return Unary();
        if (Eat('-')) return checked(-Unary());
        return Primary();
    }

    private long Primary()
    {
        Ws();
        if (Eat('('))
        {
            long v = Expr();
            Ws();
            if (!Eat(')')) throw new FormatException();
            return v;
        }
        int start = _p;
        while (_p < _s.Length && char.IsDigit(_s[_p])) _p++;
        if (start == _p) throw new FormatException();
        return long.Parse(_s[start.._p]);
    }

    private bool Eat(char c)
    {
        if (_p < _s.Length && _s[_p] == c) { _p++; return true; }
        return false;
    }

    private void Ws() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }
}
