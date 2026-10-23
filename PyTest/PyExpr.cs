using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PyTest;

/// <summary>Python operator precedence, loosest to tightest.</summary>
public enum PyPrec
{
    Lambda,
    Conditional,    // a if c else b
    Or,
    And,
    Not,
    Compare,        // < <= > >= == != in, not in, is, is not
    BitOr,
    BitXor,
    BitAnd,
    Shift,
    Additive,
    Multiplicative,
    Unary,          // -x +x ~x
    Power,          // **
    Await,
    Primary,        // x.attr  x[i]  x(...)
    Atom,
}

/// <summary>A Python expression. Rendering adds only the parentheses that precedence requires.</summary>
public abstract class PyExpr
{
    public abstract PyPrec Precedence { get; }
    public abstract void Render(StringBuilder sb);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Render(sb);
        return sb.ToString();
    }

    /// <summary>Renders a child, adding parentheses if it binds looser than <paramref name="min"/>.</summary>
    internal static void Child(StringBuilder sb, PyExpr e, PyPrec min)
    {
        if (e.Precedence < min)
        {
            sb.Append('(');
            e.Render(sb);
            sb.Append(')');
        }
        else
        {
            e.Render(sb);
        }
    }

    internal static void Join<T>(StringBuilder sb, IEnumerable<T> items, Action<StringBuilder, T> render)
    {
        bool first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(", ");
            first = false;
            render(sb, item);
        }
    }

    // Fluent primaries: Py.Name("os").Attr("path").Attr("join").Call(a, b)
    public PyExpr Attr(string name) => new PyAttr(this, name);
    public PyExpr Call(params PyArg[] args) => new PyCall(this, args);
    public PyExpr Index(params PyExpr[] items) => new PyIndex(this, items);

    // Arithmetic only. Comparisons and and/or/not are on Py so == and ! keep their C# meaning.
    public static PyExpr operator +(PyExpr a, PyExpr b) => Py.Add(a, b);
    public static PyExpr operator -(PyExpr a, PyExpr b) => Py.Sub(a, b);
    public static PyExpr operator *(PyExpr a, PyExpr b) => Py.Mul(a, b);
    public static PyExpr operator /(PyExpr a, PyExpr b) => Py.Div(a, b);
    public static PyExpr operator %(PyExpr a, PyExpr b) => Py.Mod(a, b);
    public static PyExpr operator -(PyExpr a) => Py.Neg(a);

    // No implicit conversion from string: it would be ambiguous between a name and a string literal.
    public static implicit operator PyExpr(int v) => Py.Lit(v);
    public static implicit operator PyExpr(long v) => Py.Lit(v);
    public static implicit operator PyExpr(double v) => Py.Lit(v);
    public static implicit operator PyExpr(bool v) => Py.Lit(v);
}

/// <summary>Source text emitted as-is. Used for names, strings, keywords and escape hatches.</summary>
internal sealed class PyRaw : PyExpr
{
    readonly string text;
    readonly PyPrec prec;
    public PyRaw(string text, PyPrec prec = PyPrec.Atom) { this.text = text; this.prec = prec; }
    public override PyPrec Precedence => prec;
    public override void Render(StringBuilder sb) => sb.Append(text);
}

internal sealed class PyNumber : PyExpr
{
    public string Text { get; }
    public PyNumber(string text) { Text = text; }
    /// <summary>Integer literals need parens before an attribute: (5).bit_length()</summary>
    public bool IsInteger => Text.IndexOfAny(new[] { '.', 'e', 'E', 'j' }) < 0;
    // A negative literal is really unary minus: -2 ** 2 == -4 in Python
    public override PyPrec Precedence => Text.StartsWith("-", StringComparison.Ordinal) ? PyPrec.Unary : PyPrec.Atom;
    public override void Render(StringBuilder sb) => sb.Append(Text);
}

internal sealed class PyAttr : PyExpr
{
    readonly PyExpr target;
    readonly string name;
    public PyAttr(PyExpr target, string name) { this.target = target; this.name = name; }
    public override PyPrec Precedence => PyPrec.Primary;
    public override void Render(StringBuilder sb)
    {
        if (target is PyNumber { IsInteger: true })
        {
            sb.Append('(');
            target.Render(sb);
            sb.Append(')');
        }
        else
        {
            Child(sb, target, PyPrec.Primary);
        }
        sb.Append('.').Append(name);
    }
}

internal sealed class PyCall : PyExpr
{
    readonly PyExpr func;
    readonly PyArg[] args;
    public PyCall(PyExpr func, PyArg[] args) { this.func = func; this.args = args; }
    public override PyPrec Precedence => PyPrec.Primary;
    public override void Render(StringBuilder sb)
    {
        Child(sb, func, PyPrec.Primary);
        sb.Append('(');
        Join(sb, args, (s, a) => a.Render(s));
        sb.Append(')');
    }
}

internal sealed class PyIndex : PyExpr
{
    readonly PyExpr target;
    readonly PyExpr[] items;
    public PyIndex(PyExpr target, PyExpr[] items)
    {
        if (items.Length == 0) throw new ArgumentException("Index needs at least one item", nameof(items));
        this.target = target;
        this.items = items;
    }
    public override PyPrec Precedence => PyPrec.Primary;
    public override void Render(StringBuilder sb)
    {
        Child(sb, target, PyPrec.Primary);
        sb.Append('[');
        Join(sb, items, (s, e) => Child(s, e, PyPrec.Lambda));
        sb.Append(']');
    }
}

/// <summary>lo:hi:step. Only valid inside Index().</summary>
internal sealed class PySlice : PyExpr
{
    readonly PyExpr? lo, hi, step;
    public PySlice(PyExpr? lo, PyExpr? hi, PyExpr? step) { this.lo = lo; this.hi = hi; this.step = step; }
    public override PyPrec Precedence => PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        if (lo != null) Child(sb, lo, PyPrec.Conditional);
        sb.Append(':');
        if (hi != null) Child(sb, hi, PyPrec.Conditional);
        if (step != null)
        {
            sb.Append(':');
            Child(sb, step, PyPrec.Conditional);
        }
    }
}

internal sealed class PyBinary : PyExpr
{
    readonly string op;
    readonly PyPrec prec;
    readonly PyExpr left, right;
    public PyBinary(string op, PyPrec prec, PyExpr left, PyExpr right)
    {
        this.op = op; this.prec = prec; this.left = left; this.right = right;
    }
    public override PyPrec Precedence => prec;
    public override void Render(StringBuilder sb)
    {
        PyPrec lmin, rmin;
        switch (prec)
        {
            case PyPrec.Power:
                // right-associative, and binds tighter than unary on its left: -x ** 2 == -(x ** 2)
                lmin = PyPrec.Await;
                rmin = PyPrec.Unary;
                break;
            case PyPrec.Compare:
                // Python chains comparisons (a < b < c), so (a < b) < c must keep its parens
                lmin = rmin = PyPrec.Compare + 1;
                break;
            default:
                // left-associative
                lmin = prec;
                rmin = prec + 1;
                break;
        }
        Child(sb, left, lmin);
        sb.Append(' ').Append(op).Append(' ');
        Child(sb, right, rmin);
    }
}

internal sealed class PyUnary : PyExpr
{
    readonly string op;
    readonly PyPrec prec, operandMin;
    readonly PyExpr operand;
    public PyUnary(string op, PyPrec prec, PyPrec operandMin, PyExpr operand)
    {
        this.op = op; this.prec = prec; this.operandMin = operandMin; this.operand = operand;
    }
    public override PyPrec Precedence => prec;
    public override void Render(StringBuilder sb)
    {
        sb.Append(op);
        Child(sb, operand, operandMin);
    }
}

internal sealed class PyConditional : PyExpr
{
    readonly PyExpr then, test, otherwise;
    public PyConditional(PyExpr then, PyExpr test, PyExpr otherwise)
    {
        this.then = then; this.test = test; this.otherwise = otherwise;
    }
    public override PyPrec Precedence => PyPrec.Conditional;
    public override void Render(StringBuilder sb)
    {
        Child(sb, then, PyPrec.Or);
        sb.Append(" if ");
        Child(sb, test, PyPrec.Or);
        sb.Append(" else ");
        Child(sb, otherwise, PyPrec.Conditional);
    }
}

internal sealed class PyLambda : PyExpr
{
    readonly PyParam[] parameters;
    readonly PyExpr body;
    public PyLambda(PyParam[] parameters, PyExpr body) { this.parameters = parameters; this.body = body; }
    public override PyPrec Precedence => PyPrec.Lambda;
    public override void Render(StringBuilder sb)
    {
        sb.Append("lambda");
        if (parameters.Length > 0)
        {
            sb.Append(' ');
            Join(sb, parameters, (s, p) => p.Render(s, allowAnnotation: false));
        }
        sb.Append(": ");
        Child(sb, body, PyPrec.Lambda);
    }
}

internal sealed class PyList : PyExpr
{
    readonly PyExpr[] items;
    public PyList(PyExpr[] items) { this.items = items; }
    public override PyPrec Precedence => PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        sb.Append('[');
        Join(sb, items, (s, e) => Child(s, e, PyPrec.Lambda));
        sb.Append(']');
    }
}

internal sealed class PyTuple : PyExpr
{
    readonly PyExpr[] items;
    public PyTuple(PyExpr[] items) { this.items = items; }
    public override PyPrec Precedence => PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        Join(sb, items, (s, e) => Child(s, e, PyPrec.Lambda));
        if (items.Length == 1) sb.Append(',');   // (x,) -- (x) is just x
        sb.Append(')');
    }
}

internal sealed class PySet : PyExpr
{
    readonly PyExpr[] items;
    public PySet(PyExpr[] items) { this.items = items; }
    // {} is an empty dict, so an empty set has to be set()
    public override PyPrec Precedence => items.Length == 0 ? PyPrec.Primary : PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        if (items.Length == 0)
        {
            sb.Append("set()");
            return;
        }
        sb.Append('{');
        Join(sb, items, (s, e) => Child(s, e, PyPrec.Lambda));
        sb.Append('}');
    }
}

internal sealed class PyDict : PyExpr
{
    readonly (PyExpr Key, PyExpr Value)[] entries;
    public PyDict((PyExpr Key, PyExpr Value)[] entries) { this.entries = entries; }
    public override PyPrec Precedence => PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        sb.Append('{');
        Join(sb, entries, (s, kv) =>
        {
            Child(s, kv.Key, PyPrec.Conditional);
            s.Append(": ");
            Child(s, kv.Value, PyPrec.Lambda);
        });
        sb.Append('}');
    }
}

internal sealed class PyListComp : PyExpr
{
    readonly PyExpr element, target, iterable;
    readonly PyExpr? condition;
    public PyListComp(PyExpr element, PyExpr target, PyExpr iterable, PyExpr? condition)
    {
        this.element = element; this.target = target; this.iterable = iterable; this.condition = condition;
    }
    public override PyPrec Precedence => PyPrec.Atom;
    public override void Render(StringBuilder sb)
    {
        sb.Append('[');
        Child(sb, element, PyPrec.Conditional);
        sb.Append(" for ");
        Child(sb, target, PyPrec.BitOr);
        sb.Append(" in ");
        Child(sb, iterable, PyPrec.Or);
        if (condition != null)
        {
            sb.Append(" if ");
            Child(sb, condition, PyPrec.Or);
        }
        sb.Append(']');
    }
}

/// <summary>A call argument: positional, keyword (name=value), *iterable or **mapping.</summary>
public sealed class PyArg
{
    readonly PyExpr value;
    readonly string? keyword;
    readonly int stars;

    PyArg(PyExpr value, string? keyword, int stars) { this.value = value; this.keyword = keyword; this.stars = stars; }

    public static PyArg Positional(PyExpr value) => new(value, null, 0);
    public static PyArg Keyword(string name, PyExpr value) => new(value, name, 0);
    public static PyArg Star(PyExpr value) => new(value, null, 1);
    public static PyArg StarStar(PyExpr value) => new(value, null, 2);

    public static implicit operator PyArg(PyExpr value) => Positional(value);
    // C# won't chain int -> PyExpr -> PyArg, so these are repeated here
    public static implicit operator PyArg(int v) => Positional(v);
    public static implicit operator PyArg(long v) => Positional(v);
    public static implicit operator PyArg(double v) => Positional(v);
    public static implicit operator PyArg(bool v) => Positional(v);

    internal void Render(StringBuilder sb)
    {
        if (keyword != null)
        {
            sb.Append(keyword).Append('=');
            PyExpr.Child(sb, value, PyPrec.Lambda);
        }
        else
        {
            sb.Append(stars switch { 1 => "*", 2 => "**", _ => "" });
            PyExpr.Child(sb, value, stars == 0 ? PyPrec.Lambda : PyPrec.BitOr);
        }
    }
}

public enum PyParamKind { Normal, VarArgs, KwArgs, PositionalOnlyMarker, KeywordOnlyMarker }

/// <summary>A def or lambda parameter. A plain string converts to a bare parameter name.</summary>
public sealed class PyParam
{
    public string Name { get; }
    public string? Annotation { get; }
    public PyExpr? Default { get; }
    public PyParamKind Kind { get; }

    public PyParam(string name, string? annotation, PyExpr? @default, PyParamKind kind)
    {
        Name = name; Annotation = annotation; Default = @default; Kind = kind;
    }
    public PyParam(string name, PyExpr? @default, PyParamKind kind)
    {
        Name = name; Annotation = null; Default = @default; Kind = kind;
    }

    public static implicit operator PyParam(string name) => new(name, null, null, PyParamKind.Normal);

    public static PyParam Of(string name, string? annotation = null, PyExpr? @default = null) =>
        new(name, annotation, @default, PyParamKind.Normal);
    /// <summary>*args</summary>
    public static PyParam Args(string name = "args", string? annotation = null) =>
        new(name, annotation, null, PyParamKind.VarArgs);
    /// <summary>**kwargs</summary>
    public static PyParam Kwargs(string name = "kwargs", string? annotation = null) =>
        new(name, annotation, null, PyParamKind.KwArgs);
    /// <summary>The bare / marker: parameters before it are positional-only.</summary>
    public static PyParam PositionalOnly { get; } = new("/", null, null, PyParamKind.PositionalOnlyMarker);
    /// <summary>The bare * marker: parameters after it are keyword-only.</summary>
    public static PyParam KeywordOnly { get; } = new("*", null, null, PyParamKind.KeywordOnlyMarker);

    internal void Render(StringBuilder sb, bool allowAnnotation)
    {
        if (Annotation != null && !allowAnnotation)
            throw new InvalidOperationException($"Lambda parameter '{Name}' can't have an annotation");
        if (Kind == PyParamKind.VarArgs) sb.Append('*');
        else if (Kind == PyParamKind.KwArgs) sb.Append("**");
        sb.Append(Name);
        if (Annotation != null) sb.Append(": ").Append(Annotation);
        if (Default != null)
        {
            sb.Append(Annotation != null ? " = " : "=");   // PEP 8 spacing
            PyExpr.Child(sb, Default, PyPrec.Conditional);
        }
    }
}

/// <summary>Factory for Python expressions.</summary>
internal static class Py
{
    public static readonly PyExpr None = new PyRaw("None");
    public static readonly PyExpr True = new PyRaw("True");
    public static readonly PyExpr False = new PyRaw("False");

    static readonly char[] FloatMarkers = { '.', 'e', 'E' };
    /// <summary>Triple-quoted literal. Real newlines stay as line breaks; everything else is escaped.</summary>
    internal static string QuoteTriple(string s)
    {
        var sb = new StringBuilder(s.Length + 6);
        sb.Append("\"\"\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append('\n'); break;
                // Python turns a raw CR (or CRLF) in source into \n, so it must be escaped
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"':
                    // Escape a quote only if it could close the string: when another quote
                    // follows it, or it is the last character (it would touch the closing """)
                    bool risky = i == s.Length - 1 || s[i + 1] == '"';
                    sb.Append(risky ? "\\\"" : "\"");
                    break;
                default: AppendChar(sb, s, ref i); break;
            }
        }
        sb.Append("\"\"\"");
        return sb.ToString();
    }
    public static PyExpr Name(string name) => new PyRaw(name);
    /// <summary>Escape hatch for anything the layer doesn't cover. You state its precedence.</summary>
    public static PyExpr Raw(string text, PyPrec precedence = PyPrec.Atom) => new PyRaw(text, precedence);
    /// <summary>
    /// Python string literal. Strings containing a newline become triple-quoted with real
    /// line breaks; others become "..." with escapes. Pass multiline to force either form.
    /// </summary>
    public static PyExpr Str(string value, bool? multiline = null)
    {
        bool triple = multiline ?? (value.IndexOf('\n') >= 0);
        return new PyRaw(triple ? QuoteTriple(value) : Quote(value));
    }

    /// <summary>Converts a C# value to a Python literal, recursing into collections.</summary>
    public static PyExpr Lit(object? value)
    {
        switch (value)
        {
            case null: return None;
            case PyExpr expr: return expr;
            case bool b: return b ? True : False;
            case string s: return Str(s);
            case char c: return Str(c.ToString());   // Python has no char type
            case float f: return FloatLit(f, f.ToString("R", CultureInfo.InvariantCulture));
            case double d: return FloatLit(d, d.ToString("R", CultureInfo.InvariantCulture));
            // Python has no decimal literal; this needs "from decimal import Decimal" in the output
            case decimal m: return Name("Decimal").Call(Str(m.ToString(CultureInfo.InvariantCulture)));
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return new PyNumber(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
            case Enum en:
                return Lit(Convert.ChangeType(en, Enum.GetUnderlyingType(en.GetType()), CultureInfo.InvariantCulture));
            case ITuple tuple:   // C# (a, b) value tuples and Tuple<...>
                return new PyTuple(Enumerable.Range(0, tuple.Length).Select(i => Lit(tuple[i])).ToArray());
            case IDictionary dict: return DictOf(dict);
            case IEnumerable set when IsSet(value.GetType()): return SetOf(set);
            case IEnumerable list when value is Array || value is IList: return ListOf(list);
            default:
                throw new ArgumentException($"Can't convert {value.GetType()} to a Python literal", nameof(value));
        }
    }

    // Collections from C# data (elements go through Lit)
    public static PyExpr ListOf(IEnumerable items) => new PyList(LitAll(items));
    public static PyExpr TupleOf(IEnumerable items) => new PyTuple(LitAll(items));
    public static PyExpr SetOf(IEnumerable items) => new PySet(LitAll(items));
    public static PyExpr DictOf(IDictionary dict)
    {
        var entries = new List<(PyExpr, PyExpr)>();
        foreach (DictionaryEntry e in dict) entries.Add((Lit(e.Key), Lit(e.Value)));
        return new PyDict(entries.ToArray());
    }

    // Collections from expressions
    public static PyExpr List(params PyExpr[] items) => new PyList(items);
    public static PyExpr Tuple(params PyExpr[] items) => new PyTuple(items);
    public static PyExpr Set(params PyExpr[] items) => new PySet(items);
    public static PyExpr Dict(params (PyExpr Key, PyExpr Value)[] entries) => new PyDict(entries);
    public static PyExpr ListComp(PyExpr element, PyExpr target, PyExpr iterable, PyExpr? condition = null) =>
        new PyListComp(element, target, iterable, condition);
    public static PyExpr Slice(PyExpr? lo = null, PyExpr? hi = null, PyExpr? step = null) => new PySlice(lo, hi, step);

    // Call arguments
    public static PyArg Kw(string name, PyExpr value) => PyArg.Keyword(name, value);
    public static PyArg Star(PyExpr value) => PyArg.Star(value);
    public static PyArg StarStar(PyExpr value) => PyArg.StarStar(value);

    // Arithmetic / bitwise
    public static PyExpr Add(PyExpr a, PyExpr b) => new PyBinary("+", PyPrec.Additive, a, b);
    public static PyExpr Sub(PyExpr a, PyExpr b) => new PyBinary("-", PyPrec.Additive, a, b);
    public static PyExpr Mul(PyExpr a, PyExpr b) => new PyBinary("*", PyPrec.Multiplicative, a, b);
    public static PyExpr MatMul(PyExpr a, PyExpr b) => new PyBinary("@", PyPrec.Multiplicative, a, b);
    public static PyExpr Div(PyExpr a, PyExpr b) => new PyBinary("/", PyPrec.Multiplicative, a, b);
    public static PyExpr FloorDiv(PyExpr a, PyExpr b) => new PyBinary("//", PyPrec.Multiplicative, a, b);
    public static PyExpr Mod(PyExpr a, PyExpr b) => new PyBinary("%", PyPrec.Multiplicative, a, b);
    public static PyExpr Pow(PyExpr a, PyExpr b) => new PyBinary("**", PyPrec.Power, a, b);
    public static PyExpr LShift(PyExpr a, PyExpr b) => new PyBinary("<<", PyPrec.Shift, a, b);
    public static PyExpr RShift(PyExpr a, PyExpr b) => new PyBinary(">>", PyPrec.Shift, a, b);
    public static PyExpr BitAnd(PyExpr a, PyExpr b) => new PyBinary("&", PyPrec.BitAnd, a, b);
    public static PyExpr BitXor(PyExpr a, PyExpr b) => new PyBinary("^", PyPrec.BitXor, a, b);
    public static PyExpr BitOr(PyExpr a, PyExpr b) => new PyBinary("|", PyPrec.BitOr, a, b);

    // Comparisons
    public static PyExpr Eq(PyExpr a, PyExpr b) => new PyBinary("==", PyPrec.Compare, a, b);
    public static PyExpr Ne(PyExpr a, PyExpr b) => new PyBinary("!=", PyPrec.Compare, a, b);
    public static PyExpr Lt(PyExpr a, PyExpr b) => new PyBinary("<", PyPrec.Compare, a, b);
    public static PyExpr Le(PyExpr a, PyExpr b) => new PyBinary("<=", PyPrec.Compare, a, b);
    public static PyExpr Gt(PyExpr a, PyExpr b) => new PyBinary(">", PyPrec.Compare, a, b);
    public static PyExpr Ge(PyExpr a, PyExpr b) => new PyBinary(">=", PyPrec.Compare, a, b);
    public static PyExpr Is(PyExpr a, PyExpr b) => new PyBinary("is", PyPrec.Compare, a, b);
    public static PyExpr IsNot(PyExpr a, PyExpr b) => new PyBinary("is not", PyPrec.Compare, a, b);
    public static PyExpr In(PyExpr a, PyExpr b) => new PyBinary("in", PyPrec.Compare, a, b);
    public static PyExpr NotIn(PyExpr a, PyExpr b) => new PyBinary("not in", PyPrec.Compare, a, b);

    // Boolean
    public static PyExpr And(PyExpr a, PyExpr b, params PyExpr[] more) => Fold("and", PyPrec.And, a, b, more);
    public static PyExpr Or(PyExpr a, PyExpr b, params PyExpr[] more) => Fold("or", PyPrec.Or, a, b, more);
    public static PyExpr Not(PyExpr a) => new PyUnary("not ", PyPrec.Not, PyPrec.Not, a);

    // Unary
    public static PyExpr Neg(PyExpr a) => new PyUnary("-", PyPrec.Unary, PyPrec.Unary, a);
    public static PyExpr Pos(PyExpr a) => new PyUnary("+", PyPrec.Unary, PyPrec.Unary, a);
    public static PyExpr Invert(PyExpr a) => new PyUnary("~", PyPrec.Unary, PyPrec.Unary, a);
    public static PyExpr Await(PyExpr a) => new PyUnary("await ", PyPrec.Await, PyPrec.Primary, a);

    /// <summary>then if test else otherwise</summary>
    public static PyExpr IfElse(PyExpr then, PyExpr test, PyExpr otherwise) => new PyConditional(then, test, otherwise);
    public static PyExpr Lambda(PyExpr body, params PyParam[] parameters) => new PyLambda(parameters, body);

    static PyExpr Fold(string op, PyPrec prec, PyExpr a, PyExpr b, PyExpr[] more)
    {
        PyExpr result = new PyBinary(op, prec, a, b);
        foreach (var e in more) result = new PyBinary(op, prec, result, e);
        return result;
    }

    static PyExpr[] LitAll(IEnumerable items) => items.Cast<object?>().Select(Lit).ToArray();

    static bool IsSet(Type t) => t.GetInterfaces().Any(i =>
        i.IsGenericType &&
        (i.GetGenericTypeDefinition() == typeof(ISet<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)));

    static PyExpr FloatLit(double value, string text)
    {
        if (double.IsNaN(value)) return Name("float").Call(Str("nan"));
        if (double.IsPositiveInfinity(value)) return Name("float").Call(Str("inf"));
        if (double.IsNegativeInfinity(value)) return Name("float").Call(Str("-inf"));
        // "R" gives e.g. "1", "1.5", "1E+20", "-0". Python reads all of those, but "1" would be an int.
        if (text.IndexOfAny(FloatMarkers) < 0) text += ".0";
        return new PyNumber(text);
    }

    /// <summary>Double-quoted Python string literal.</summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: AppendChar(sb, s, ref i); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Escapes control characters and lone surrogates; passes everything else through.</summary>
    internal static void AppendChar(StringBuilder sb, string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            sb.Append(c).Append(s[i + 1]);
            i++;
        }
        else if (char.IsSurrogate(c))
        {
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        else if (c < 0x20 || (c >= 0x7f && c <= 0x9f))
        {
            sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(c);
        }
    }
}