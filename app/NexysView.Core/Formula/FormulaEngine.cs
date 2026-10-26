using System.Globalization;
using System.Text;

namespace NexysView.Core.Formula;

public sealed class FormulaException : Exception { public FormulaException(string m) : base(m) { } }

/// <summary>
/// Formula engine with the the reference calculator semantics measured in docs/08-formula.md:
///  '^' left-associative; unary minus binds tighter than '^' (-2^2 = 4); 'mod' integer; comparisons/logic → -1/0;
///  round = banker's; int = floor; trunc toward zero; ln and log both natural; ch("G/x") vectorizes element-wise;
///  assignment "Name = expr" required (error 554 otherwise); unknown identifier → "Variable is undefined: 'x'".
/// Values are double scalars, string scalars, or double[] vectors (channels).
/// </summary>
public sealed class FormulaEngine
{
    public Func<string, double[]?> ChannelResolver { get; set; } = _ => null;
    public Dictionary<string, object> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Result(string Target, object Value);

    public Result Evaluate(string formula)
    {
        int eq = IndexOfAssign(formula);
        if (eq < 0) throw new FormulaException("Cannot assign the calculation result to the left side of the formula.");
        string lhs = formula[..eq].Trim(), rhs = formula[(eq + 1)..];
        if (lhs.Length == 0) throw new FormulaException("Left side of the formula is not a clear assignment.");
        var toks = Lex(rhs);
        var p = new Parser(toks, this);
        var v = p.Logic();
        if (p.Peek().Kind != Tok.End) throw new FormulaException(p.Peek().Kind == Tok.Ident ? "Expected end of statement" : "Syntax error");
        if (!lhs.StartsWith("ch(", StringComparison.OrdinalIgnoreCase)) Variables[lhs] = v;
        return new Result(lhs, v);
    }

    static int IndexOfAssign(string s)
    {
        int depth = 0; bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') q = !q;
            if (q) continue;
            if (c == '(') depth++; else if (c == ')') depth--;
            else if (c == '=' && depth == 0) { if (i + 1 < s.Length && (s[i + 1] == '=' )) continue; if (i > 0 && (s[i - 1] == '<' || s[i - 1] == '>')) continue; return i; }
        }
        return -1;
    }

    enum Tok { Num, Str, Ident, Op, End }
    readonly record struct Token(Tok Kind, string Text, double Num);

    static List<Token> Lex(string s)
    {
        var res = new List<Token>(); int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) { int j = i + 1; if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++; if (j < s.Length && char.IsDigit(s[j])) { i = j; while (i < s.Length && char.IsDigit(s[i])) i++; } }
                res.Add(new Token(Tok.Num, s[st..i], double.Parse(s[st..i], CultureInfo.InvariantCulture))); continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length) { if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; } i++; break; } sb.Append(s[i++]); }
                res.Add(new Token(Tok.Str, sb.ToString(), 0)); continue;
            }
            if (char.IsLetter(c) || c == '_') { int st = i; while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; res.Add(new Token(Tok.Ident, s[st..i], 0)); continue; }
            if ("<>".Contains(c) && i + 1 < s.Length && (s[i + 1] == '=' || (c == '<' && s[i + 1] == '>'))) { res.Add(new Token(Tok.Op, s.Substring(i, 2), 0)); i += 2; continue; }
            if ("+-*/^(),=<>".Contains(c)) { res.Add(new Token(Tok.Op, c.ToString(), 0)); i++; continue; }
            throw new FormulaException("Syntax error");
        }
        res.Add(new Token(Tok.End, "", 0));
        return res;
    }

    sealed class Parser
    {
        readonly List<Token> _t; readonly FormulaEngine _e; public int Pos;
        public Parser(List<Token> t, FormulaEngine e) { _t = t; _e = e; }
        public Token Peek() => _t[Pos];
        Token Take() { if (_t[Pos].Kind == Tok.End) throw new FormulaException("Syntax error"); return _t[Pos++]; }
        bool IsOp(string s) => Peek().Kind == Tok.Op && Peek().Text == s;
        bool IsIdent(string s) => Peek().Kind == Tok.Ident && string.Equals(Peek().Text, s, StringComparison.OrdinalIgnoreCase);

        public object Logic()
        {
            var a = Compare();
            while (IsIdent("and") || IsIdent("or") || IsIdent("xor"))
            {
                string op = Take().Text.ToLowerInvariant(); var b = Compare();
                a = Map2(a, b, (x, y) => op switch { "and" => (long)x & (long)y, "or" => (long)x | (long)y, _ => (long)x ^ (long)y });
            }
            return a;
        }

        object Compare()
        {
            var a = Add();
            while (Peek().Kind == Tok.Op && Peek().Text is "=" or "<" or ">" or "<=" or ">=" or "<>")
            {
                string op = Take().Text; var b = Add();
                if (a is string sa && b is string sb) { bool r = op switch { "=" => sa == sb, "<>" => sa != sb, "<" => string.CompareOrdinal(sa, sb) < 0, ">" => string.CompareOrdinal(sa, sb) > 0, "<=" => string.CompareOrdinal(sa, sb) <= 0, _ => string.CompareOrdinal(sa, sb) >= 0 }; a = r ? -1.0 : 0.0; continue; }
                a = Map2(a, b, (x, y) => (op switch { "=" => x == y, "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y, _ => x != y }) ? -1.0 : 0.0);
            }
            return a;
        }

        object Add()
        {
            var a = Mul();
            while (IsOp("+") || IsOp("-"))
            {
                string op = Take().Text; var b = Mul();
                if (op == "+" && (a is string || b is string)) { a = Str(a) + Str(b); continue; }
                a = Map2(a, b, (x, y) => op == "+" ? x + y : x - y);
            }
            return a;
        }

        object Mul()
        {
            var a = Unary();
            while (IsOp("*") || IsOp("/") || IsIdent("mod"))
            {
                string op = Take().Text.ToLowerInvariant(); var b = Unary();
                if (op == "/" && b is double bd && bd == 0) throw new FormulaException(a is double ad && ad != 0 ? "Division by zero" : "Overflow");
                a = op switch
                {
                    "*" => Map2(a, b, (x, y) => x * y),
                    "/" => Map2(a, b, (x, y) => y == 0 ? double.NaN : x / y),
                    _ => Map2(a, b, (x, y) => (long)y == 0 ? double.NaN : (long)x % (long)y),
                };
            }
            return a;
        }

        object Unary()
        {
            if (IsOp("-")) { Take(); var a = Map1(Atom(), x => -x); return PowerTail(a); }
            if (IsOp("+")) { Take(); return Unary(); }
            if (IsIdent("not")) { Take(); return Map1(Unary(), x => x == 0 ? -1.0 : 0.0); }
            return PowerTail(Atom());
        }

        object PowerTail(object a)
        {
            while (IsOp("^")) { Take(); var b = Atom(); a = Map2(a, b, Math.Pow); }   // left-assoc (measured)
            return a;
        }

        object Atom()
        {
            var t = Take();
            if (t.Kind == Tok.Num) return t.Num;
            if (t.Kind == Tok.Str) return t.Text;
            if (t.Kind == Tok.Op && t.Text == "(") { var r = Logic(); if (!IsOp(")")) throw new FormulaException("Expected ')'"); Take(); return r; }
            if (t.Kind == Tok.Ident)
            {
                string name = t.Text.ToLowerInvariant();
                if (IsOp("("))
                {
                    Take();
                    var args = new List<object>();
                    if (!IsOp(")")) { args.Add(Logic()); while (IsOp(",")) { Take(); args.Add(Logic()); } }
                    if (!IsOp(")")) throw new FormulaException("Expected ')'");
                    Take();
                    return Call(name, args, t.Text);
                }
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                if (_e.Variables.TryGetValue(t.Text, out var v)) return v;
                throw new FormulaException($"Variable is undefined: '{t.Text}'");
            }
            throw new FormulaException("Syntax error");
        }

        object Call(string name, List<object> args, string original)
        {
            object A(int i) => i < args.Count ? args[i] : throw new FormulaException("Syntax error");
            switch (name)
            {
                case "ch": { var ch = _e.ChannelResolver(Str(A(0))) ?? throw new FormulaException($"Channel not found: '{Str(A(0))}'"); return ch; }
                case "sqrt": return Map1(A(0), Math.Sqrt);
                case "abs": return Map1(A(0), Math.Abs);
                case "exp": return Map1(A(0), Math.Exp);
                case "ln": case "log": return Map1(A(0), Math.Log);
                case "log10": return Map1(A(0), Math.Log10);
                case "sin": return Map1(A(0), Math.Sin);
                case "cos": return Map1(A(0), Math.Cos);
                case "tan": return Map1(A(0), Math.Tan);
                case "atan": return Map1(A(0), Math.Atan);
                case "asin": return Map1(A(0), Math.Asin);
                case "acos": return Map1(A(0), Math.Acos);
                case "int": return Map1(A(0), Math.Floor);
                case "trunc": case "fix": return Map1(A(0), Math.Truncate);
                case "round": return Map1(A(0), x => Math.Round(x, MidpointRounding.ToEven));
                case "sgn": return Map1(A(0), x => Math.Sign(x));
                case "len": return (double)Str(A(0)).Length;
                case "ctnv": return "NOVALUE";
                case "min": return args.Select(ToD).Min();
                case "max": return args.Select(ToD).Max();
                case "cmax": return Vec(A(0)).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
                case "cmin": return Vec(A(0)).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Min();
                case "cmean": { var v = Vec(A(0)).Where(x => !double.IsNaN(x)).ToArray(); return v.Length == 0 ? double.NaN : v.Average(); }
                case "csum": return Vec(A(0)).Where(x => !double.IsNaN(x)).Sum();
                case "cl": return (double)Vec(A(0)).Length;
                default: throw new FormulaException($"Variable is undefined: '{original}'");
            }
        }

        static double ToD(object o) => o is double d ? d : o is double[] v && v.Length > 0 ? v[0] : throw new FormulaException("Type mismatch");
        static double[] Vec(object o) => o is double[] v ? v : new[] { ToD(o) };
        static string Str(object o) => o is string s ? s : o is double d ? d.ToString(CultureInfo.InvariantCulture) : throw new FormulaException("Type mismatch");

        static object Map1(object a, Func<double, double> f)
        {
            if (a is double[] v) { var r = new double[v.Length]; for (int i = 0; i < v.Length; i++) r[i] = double.IsNaN(v[i]) ? double.NaN : f(v[i]); return r; }
            return f(ToD(a));
        }

        static object Map2(object a, object b, Func<double, double, double> f)
        {
            if (a is double[] || b is double[])
            {
                var va = a as double[]; var vb = b as double[];
                int n = Math.Max(va?.Length ?? 0, vb?.Length ?? 0);
                if (va != null && vb != null) n = Math.Min(va.Length, vb.Length);
                double sa = va == null ? ToD(a) : 0, sb = vb == null ? ToD(b) : 0;
                var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double x = va != null ? va[i] : sa, y = vb != null ? vb[i] : sb;
                    r[i] = double.IsNaN(x) || double.IsNaN(y) ? double.NaN : f(x, y);
                }
                return r;
            }
            return f(ToD(a), ToD(b));
        }
    }
}
