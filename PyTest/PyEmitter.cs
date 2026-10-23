using PyTest;

using System.Text;

namespace PyTest;

/// <summary>
/// Writes Python 3 source straight to a <see cref="TextWriter"/>, one statement at a time.
/// <para>
/// Blocks: open one (Class, Method, If, While, Try...), write its body, then call End().
/// For ElseIf/Else/Catch/Finally, call them while the previous block is still open --
/// don't call End() first. Empty blocks get "pass" automatically.
/// </para>
/// </summary>
public class PyEmitter(TextWriter? writer = null)
{
    readonly IndentedTextWriter writer = new(writer ?? new StringWriter());
    readonly Stack<Block> blocks = new();

    sealed class Block(string kind)
    {
        public string Kind { get; } = kind;
        public bool HasBody { get; set; }
    }

    static readonly HashSet<string> AugOps = [
        "+", "-", "*", "/", "//", "%", "**", "@", "&", "|", "^", "<<", ">>",
    ];
    public void Clear()
    {
        ((StringWriter)writer.BaseWriter).GetStringBuilder().Clear();
        writer.IndentLevel = 0;
    }
    public override string ToString()
    {
        if (writer.BaseWriter is StringWriter sw)
        {
            return sw.ToString();
        }
        return base.ToString() ?? "";
    }
    public string ToString(bool clear)
    {
        var result = ToString();
        if (clear)
        {
            Clear();
        }
        return result;
    }
    /// <summary>Call when done. Throws if a block was left open.</summary>
    public void Finish()
    {
        if (blocks.Count > 0)
        {
            throw new InvalidOperationException($"Unclosed '{blocks.Peek().Kind}' block");
        }
    }

    #region Plumbing

    void Line(string text)
    {
        writer.WriteLine(text);
        if (blocks.Count > 0)
        {
            blocks.Peek().HasBody = true;
        }
    }

    void Open(string kind, string header)
    {
        Line(header + ":");
        blocks.Push(new Block(kind));
        ++writer.IndentLevel;
    }

    void Close()
    {
        var block = blocks.Pop();

        if (!block.HasBody)
        {
            writer.WriteLine("pass");
        }

        --writer.IndentLevel;
    }

    /// <summary>Closes the open block and opens a continuation of it (elif, else, except, finally).</summary>
    void Chain(string kind, string header, params string[] allowedAfter)
    {
        if (blocks.Count == 0 || Array.IndexOf(allowedAfter, blocks.Peek().Kind) < 0)
        {
            throw new InvalidOperationException(
                $"'{header}' must be called while one of these blocks is open: {string.Join(", ", allowedAfter)}");
        }

        Close();
        Open(kind, header);
    }

    void RequireClass(string what)
    {
        if (blocks.Count == 0 || blocks.Peek().Kind != "class")
        {
            throw new InvalidOperationException($"{what} must be directly inside a Class block");
        }
    }

    static string Async(bool isAsync) => isAsync ? "async " : "";

    void Def(string name, IEnumerable<PyParam> parameters, string? returns, bool isAsync)
    {
        var sb = new StringBuilder();

        sb.Append(Async(isAsync)).Append("def ").Append(name).Append('(');

        PyExpr.Join(sb, parameters, (s, p) => p.Render(s, allowAnnotation: true));

        sb.Append(')');

        if (returns != null)
        {
            sb.Append(" -> ").Append(returns);
        }

        Open("def", sb.ToString());
    }

    static IEnumerable<PyParam> Prepend(string first, IEnumerable<PyParam> rest) =>
        new PyParam[] { first }.Concat(rest);

    #endregion

    #region Imports

    /// <summary>import module / import module as alias</summary>
    public void Import(string module, string? alias = null) =>
        Line(alias == null ? $"import {module}" : $"import {module} as {alias}");

    /// <summary>from module import a, b. A name can carry an alias: "b as c"</summary>
    public void ImportFrom(string module, params string[] names)
    {
        if (names.Length == 0)
        {
            throw new ArgumentException("Need at least one name", nameof(names));
        }

        Line($"from {module} import {string.Join(", ", names)}");
    }

    #endregion

    #region Statements

    /// <summary>target = value</summary>
    public void Let(PyExpr target, PyExpr value) => Line($"{target} = {value}");

    /// <summary>target op= value, e.g. AugAssign(x, "+", One) gives x += 1</summary>
    public void AugAssign(PyExpr target, string op, PyExpr value)
    {
        if (!AugOps.Contains(op))
        {
            throw new ArgumentException($"Not an augmented-assignment operator: {op}", nameof(op));
        }

        Line($"{target} {op}= {value}");
    }

    /// <summary>
    /// Declares a variable. Python has no declarations, so with no type this is just name = value.
    /// With a type it's a type hint: name: type = value (value may then be omitted).
    /// </summary>
    public void Var(string name, PyExpr? initializer, string? type = null)
    {
        if (type == null)
        {
            if (initializer is null)
            {
                throw new ArgumentException("An untyped Var needs an initializer", nameof(initializer));
            }

            Line($"{name} = {initializer}");
        }
        else
        {
            Line(initializer is null ? $"{name}: {type}" : $"{name}: {type} = {initializer}");
        }
    }

    /// <summary>method(args) as a statement</summary>
    public void Call(PyExpr method, params PyArg[] arguments) => Line(method.Call(arguments).ToString());

    /// <summary>target.name(args) as a statement</summary>
    public void Call(PyExpr target, string name, params PyArg[] arguments) =>
        Line(target.Attr(name).Call(arguments).ToString());

    /// <summary>Any expression as a statement (CodeDom's ExpressionStatement)</summary>
    public void Expr(PyExpr expression) => Line(expression.ToString());

    public void Return(PyExpr? value = null) => Line(value is null ? "return" : $"return {value}");

    /// <summary>raise exception / raise exception from cause. No arguments rethrows (only valid inside Catch).</summary>
    public void Throw(PyExpr? exception = null, PyExpr? cause = null)
    {
        if (exception is null)
        {
            if (cause != null)
            {
                throw new ArgumentException("A cause needs an exception", nameof(cause));
            }

            Line("raise");
        }

        else
        {
            Line(cause is null ? $"raise {exception}" : $"raise {exception} from {cause}");
        }
    }

    public void Break() => Line("break");
    public void Continue() => Line("continue");
    /// <summary>Does nothing. Empty blocks get one automatically, so you rarely need this.</summary>
    public void Pass() => Line("pass");

    /// <summary>assert test, message</summary>
    public void Assert(PyExpr test, PyExpr? message = null) =>
        Line(message is null ? $"assert {test}" : $"assert {test}, {message}");

    /// <summary>del a, b -- removes a variable, item or attribute</summary>
    public void Del(params PyExpr[] targets) => Line("del " + string.Join(", ", targets.Select(t => t.ToString())));

    /// <summary>global x -- lets a function assign to a module-level variable</summary>
    public void Global(params string[] names) => Line("global " + string.Join(", ", names));

    /// <summary>nonlocal x -- lets a nested function assign to the enclosing function's variable</summary>
    public void Nonlocal(params string[] names) => Line("nonlocal " + string.Join(", ", names));

    /// <summary>yield value -- like C#'s yield return</summary>
    public void Yield(PyExpr? value = null) => Line(value is null ? "yield" : $"yield {value}");

    /// <summary>yield from iterable -- yields every item of another iterable</summary>
    public void YieldFrom(PyExpr iterable) => Line($"yield from {iterable}");

    /// <summary>A raw line of Python, written as-is at the current indent</summary>
    public void Snippet(string line) => Line(line);

    /// <summary># comment. Multi-line text becomes several comment lines. Doesn't count as a block body.</summary>
    public void Comment(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            writer.WriteLine(line.Length == 0 ? "#" : "# " + line);
        }
    }

    /// <summary>
    /// """docstring""" -- Python's doc comment. Unlike C#, it goes first INSIDE the class or
    /// method it documents (or first in the file for the module).
    /// </summary>
    public void Docstring(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var escaped = new StringBuilder();
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            switch (c)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\n':
                    escaped.Append('\n');
                    break;
                case '\t':
                    escaped.Append('\t');
                    break;
                default:
                    Py.AppendChar(escaped, normalized, ref i);
                    break;
            }
        }

        var lines = escaped.ToString().Split('\n');

        if (lines.Length == 1)
        {
            Line($"\"\"\"{lines[0]}\"\"\"");
            return;
        }

        Line("\"\"\"" + lines[0]);
        for (int i = 1; i < lines.Length - 1; i++) Line(lines[i]);
        Line(lines[^1] + "\"\"\"");
    }

    public void BlankLine() => writer.WriteLine("");

    /// <summary>@decorator -- write it right before the Class/Method/Function it applies to</summary>
    public void Decorator(PyExpr decorator) => Line("@" + decorator);

    #endregion

    #region Types and members

    /// <summary>class Name(bases): -- bases can include keywords: Py.Kw("metaclass", TypeRef("ABCMeta"))</summary>
    public void Class(string name, params PyArg[] baseTypes)
    {
        var sb = new StringBuilder("class ").Append(name);
        if (baseTypes.Length > 0)
        {
            sb.Append('(');
            PyExpr.Join(sb, baseTypes, (s, b) => b.Render(s));
            sb.Append(')');
        }
        Open("class", sb.ToString());
    }

    /// <summary>A module-level (or nested) function: def name(params):</summary>
    public void Function(string name, params PyParam[] parameters) => Def(name, parameters, null, false);

    public void Function(string name, IEnumerable<PyParam> parameters, string? returns = null, bool isAsync = false) =>
        Def(name, parameters, returns, isAsync);

    /// <summary>An instance method. Must be directly inside a Class; self is added for you.</summary>
    public void Method(string name, params PyParam[] parameters) => Method(name, parameters, null, false);

    public void Method(string name, IEnumerable<PyParam> parameters, string? returns = null, bool isAsync = false)
    {
        RequireClass("Method");
        Def(name, Prepend("self", parameters), returns, isAsync);
    }

    /// <summary>@staticmethod -- like a C# static method; no self</summary>
    public void StaticMethod(string name, params PyParam[] parameters) => StaticMethod(name, parameters, null, false);

    public void StaticMethod(string name, IEnumerable<PyParam> parameters, string? returns = null, bool isAsync = false)
    {
        RequireClass("StaticMethod");
        Line("@staticmethod");
        Def(name, parameters, returns, isAsync);
    }

    /// <summary>@classmethod -- receives the class itself as cls (added for you). Often used for factory methods.</summary>
    public void ClassMethod(string name, params PyParam[] parameters) => ClassMethod(name, parameters, null, false);

    public void ClassMethod(string name, IEnumerable<PyParam> parameters, string? returns = null, bool isAsync = false)
    {
        RequireClass("ClassMethod");
        Line("@classmethod");
        Def(name, Prepend("cls", parameters), returns, isAsync);
    }

    /// <summary>
    /// The constructor: def __init__(self, params). Must be directly inside a Class.
    /// To call the base constructor: emit.Call(Base, "__init__", args)
    /// </summary>
    public void Ctor(params PyParam[] parameters)
    {
        RequireClass("Ctor");
        Def("__init__", Prepend("self", parameters), null, false);
    }

    /// <summary>Property getter: @property def name(self) -> type:  Return the value in the body.</summary>
    public void Property(string name, string? type = null)
    {
        RequireClass("Property");
        Line("@property");
        Def(name, ["self"], type, false);
    }

    /// <summary>
    /// Property setter: @name.setter def name(self, value: type):  Call after the getter.
    /// Note: the backing field must use a different name (e.g. self._name), or the setter calls itself.
    /// </summary>
    public void PropertySetter(string name, string? type = null, string valueName = "value")
    {
        RequireClass("PropertySetter");
        Line($"@{name}.setter");
        Def(name, ["self", PyParam.Of(valueName, type)], null, false);
    }

    #endregion

    #region Control flow

    public void If(PyExpr condition) => Open("if", $"if {condition}");

    /// <summary>elif condition: -- call while the If (or a previous ElseIf) is still open</summary>
    public void ElseIf(PyExpr condition) => Chain("elif", $"elif {condition}", "if", "elif");

    /// <summary>
    /// else: -- after If/ElseIf as usual. Python also allows it after While/ForEach (runs if the loop
    /// didn't break) and after Catch (runs if the Try raised nothing).
    /// </summary>
    public void Else()
    {
        var kind = blocks.Count == 0 ? null : blocks.Peek().Kind;
        var newKind = kind switch
        {
            "if" or "elif" => "if-else",
            "for" or "while" => "loop-else",
            "except" => "try-else",
            _ => throw new InvalidOperationException("Else must be called while an If/ElseIf, While/ForEach or Catch block is open"),
        };
        Chain(newKind, "else", kind!);
    }

    public void While(PyExpr condition) => Open("while", $"while {condition}");

    /// <summary>
    /// for target in iterable: -- Python's only for loop; it's C#'s foreach.
    /// For a counted loop: ForEach(VarRef("i"), Invoke(VarRef("range"), 10))
    /// </summary>
    public void ForEach(PyExpr target, PyExpr iterable, bool isAsync = false) =>
        Open("for", $"{Async(isAsync)}for {target} in {iterable}");

    public void Try() => Open("try", "try");

    /// <summary>except / except Type / except Type as name -- call while the Try or a previous Catch is open</summary>
    public void Catch(PyExpr? exceptionType = null, string? localName = null)
    {
        if (exceptionType is null && localName != null)
        {
            throw new ArgumentException("A local name needs an exception type", nameof(localName));
        }

        var header = exceptionType is null ? "except"
            : localName is null ? $"except {exceptionType}"
            : $"except {exceptionType} as {localName}";

        Chain("except", header, "try", "except");
    }

    public void Finally() => Chain("finally", "finally", "try", "except", "try-else");

    /// <summary>with resource as name: -- like C#'s using block</summary>
    public void With(PyExpr resource, string? asName = null, bool isAsync = false) =>
        With([(resource, asName)], isAsync);

    public void With(IEnumerable<(PyExpr Resource, string? As)> resources, bool isAsync = false)
    {
        var parts = resources.Select(r => r.As is null ?
                    r.Resource.ToString() :
                    $"{r.Resource} as {r.As}").ToList();

        if (parts.Count == 0)
        {
            throw new ArgumentException("Need at least one resource", nameof(resources));
        }

        Open("with", $"{Async(isAsync)}with {string.Join(", ", parts)}");
    }

    /// <summary>if __name__ == "__main__": -- runs only when the file is executed directly, not imported</summary>
    public void EntryPoint() => If(Py.Eq(Py.Name("__name__"), Py.Str("__main__")));

    /// <summary>Closes the innermost open block</summary>
    public void End()
    {
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("No open block to end");
        }

        if (blocks.Peek().Kind == "try")
        {
            throw new InvalidOperationException("Try needs a Catch or Finally before End");
        }

        Close();
    }

    #endregion
}