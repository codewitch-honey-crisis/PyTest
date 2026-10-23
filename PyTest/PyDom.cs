namespace PyTest;

/// <summary>
/// Each call returns a <see cref="PyExpr"/>
/// that renders to Python text; nothing is kept around once it's written.
/// Use with <c>using static PyTest.PyDom;</c>. For Python-only operators (**, //, in,
/// await, lambda, x if c else y, comprehensions, slices) use <see cref="Py"/>.
/// </summary>
internal static class PyDom
{
    #region Constants

    /// <summary>self -- Python's equivalent of this</summary>
    public static PyExpr Self { get; } = Py.Name("self");
    /// <summary>super() -- Python's equivalent of base. Base.Attr("__init__"), Invoke(Base, "__init__", ...)</summary>
    public static PyExpr Base { get; } = Py.Name("super").Call();
    public static PyExpr True => Py.True;
    public static PyExpr False => Py.False;
    /// <summary>None -- Python's equivalent of null</summary>
    public static PyExpr None => Py.None;
    public static PyExpr Zero { get; } = Py.Lit(0);
    public static PyExpr One { get; } = Py.Lit(1);
    public static PyExpr NegOne { get; } = Py.Lit(-1);

    #endregion

    #region References

    /// <summary>A local or global variable by name</summary>
    public static PyExpr MethodRef(string name) => Py.Name(name);

    /// <summary>A local or global variable by name</summary>
    public static PyExpr VarRef(string name) => Py.Name(name);
    /// <summary>A parameter by name. Same as VarRef in Python.</summary>
    public static PyExpr ArgRef(string name) => Py.Name(name);
    /// <summary>A class or module by name. Dotted names are fine: TypeRef("collections.OrderedDict")</summary>
    public static PyExpr TypeRef(string name) => Py.Name(name);
    /// <summary>target.name</summary>
    public static PyExpr FieldRef(PyExpr target, string name) => target.Attr(name);
    /// <summary>target.name. Same as FieldRef in Python: properties are accessed the same way.</summary>
    public static PyExpr PropRef(PyExpr target, string name) => target.Attr(name);
    /// <summary>target.name, without calling it (a bound method you can pass around)</summary>
    public static PyExpr MethRef(PyExpr target, string name) => target.Attr(name);

    #endregion

    #region Calls and construction

    /// <summary>method(args) -- method is any callable expression: a function name, a MethRef, a lambda...</summary>
    public static PyExpr Invoke(PyExpr method, params PyArg[] arguments) => method.Call(arguments);
    /// <summary>target.name(args)</summary>
    public static PyExpr Invoke(PyExpr target, string name, params PyArg[] arguments) =>
        target.Attr(name).Call(arguments);
    /// <summary>TypeName(args). Python has no new keyword; calling the class creates the instance.</summary>
    public static PyExpr New(string typeName, params PyArg[] arguments) => Py.Name(typeName).Call(arguments);
    /// <summary>type(args)</summary>
    public static PyExpr New(PyExpr type, params PyArg[] arguments) => type.Call(arguments);
    /// <summary>target[i] or target[i, j]</summary>
    public static PyExpr Indexer(PyExpr target, params PyExpr[] indices) => target.Index(indices);
    /// <summary>target[i]. Same as Indexer in Python.</summary>
    public static PyExpr ArrIndexer(PyExpr target, params PyExpr[] indices) => target.Index(indices);

    /// <summary>
    /// Converts a C# value to a Python literal: null, bool, numbers, strings, chars, enums,
    /// arrays/lists, ISet, IDictionary and C# tuples (recursively).
    /// </summary>
    public static PyExpr Literal(object? value) => Py.Lit(value);
    /// <summary>Raw Python expression text, written as-is</summary>
    public static PyExpr Snippet(string text) => Py.Raw(text);

    #endregion

    #region Operators

    // Arithmetic and boolean operators take extra operands and fold left: Add(a, b, c) is a + b + c

    public static PyExpr Add(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.Add, left, right, rightN);
    public static PyExpr Sub(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.Sub, left, right, rightN);
    public static PyExpr Mul(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.Mul, left, right, rightN);
    /// <summary>a / b. Always float division in Python 3 (7 / 2 == 3.5). Use Py.FloorDiv for C#-style int division.</summary>
    public static PyExpr Div(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.Div, left, right, rightN);
    public static PyExpr Mod(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.Mod, left, right, rightN);
    public static PyExpr BitwiseAnd(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.BitAnd, left, right, rightN);
    public static PyExpr BitwiseOr(PyExpr left, PyExpr right, params PyExpr[] rightN) => Fold(Py.BitOr, left, right, rightN);
    /// <summary>a and b</summary>
    public static PyExpr And(PyExpr left, PyExpr right, params PyExpr[] rightN) => Py.And(left, right, rightN);
    /// <summary>a or b</summary>
    public static PyExpr Or(PyExpr left, PyExpr right, params PyExpr[] rightN) => Py.Or(left, right, rightN);
    /// <summary>not a</summary>
    public static PyExpr Not(PyExpr target) => Py.Not(target);

    // Comparisons take exactly two operands. Python chains them (a < b < c means a < b and b < c),
    // so the CodeDom-style "fold extra operands" would mean something different here.

    /// <summary>a == b (value equality)</summary>
    public static PyExpr Eq(PyExpr left, PyExpr right) => Py.Eq(left, right);
    /// <summary>a != b</summary>
    public static PyExpr NotEq(PyExpr left, PyExpr right) => Py.Ne(left, right);
    public static PyExpr Gt(PyExpr left, PyExpr right) => Py.Gt(left, right);
    public static PyExpr Gte(PyExpr left, PyExpr right) => Py.Ge(left, right);
    public static PyExpr Lt(PyExpr left, PyExpr right) => Py.Lt(left, right);
    public static PyExpr Lte(PyExpr left, PyExpr right) => Py.Le(left, right);
    /// <summary>a is b (reference equality). Use this for None: IdentEq(x, None)</summary>
    public static PyExpr IdentEq(PyExpr left, PyExpr right) => Py.Is(left, right);
    /// <summary>a is not b</summary>
    public static PyExpr IdentNotEq(PyExpr left, PyExpr right) => Py.IsNot(left, right);

    #endregion

    #region Parameters and types

    /// <summary>A plain parameter: name</summary>
    public static PyParam Param(string name) => PyParam.Of(name);
    /// <summary>
    /// A parameter with an optional type hint and default: name: type = default.
    /// Type comes first to match CodeDom's Param(type, name). Pass null for no type hint.
    /// </summary>
    public static PyParam Param(string? type, string name, PyExpr? defaultValue = null) =>
        PyParam.Of(name, type, defaultValue);

    /// <summary>
    /// Type hint text. Type("int") is int; Type("dict", "str", "int") is dict[str, int];
    /// nest them: Type("list", Type("tuple", "int", "str")) is list[tuple[int, str]].
    /// </summary>
    public static string Type(string name, params string[] typeArguments) =>
        typeArguments.Length == 0 ? name : $"{name}[{string.Join(", ", typeArguments)}]";

    #endregion

    static PyExpr Fold(Func<PyExpr, PyExpr, PyExpr> op, PyExpr left, PyExpr right, PyExpr[] rightN) =>
        rightN.Aggregate(op(left, right), op);
}