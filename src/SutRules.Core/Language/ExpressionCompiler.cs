using System.Globalization;
using System.Text.RegularExpressions;
using SutRules.Engine;

namespace SutRules.Language;

/// <summary>A type-checked, compiled expression.</summary>
internal sealed record Typed(DslType Type, Eval Eval, bool IsConstant = false, object? Constant = null)
{
    public static Typed Const(DslType type, object? value) => new(type, _ => value, true, value);
}

/// <summary>
/// Type checker and compiler in one pass: each syntax node is checked against the schema and turned into a closure
/// (<see cref="Eval"/>) with names already resolved to slots, getters and specialised operators. There is no
/// separate interpreter walking the tree at run time; see ADR 0003 for why closures rather than IL or expression
/// trees.
/// </summary>
internal sealed class ExpressionCompiler
{
    /// <summary>Marks an expression that already produced a diagnostic, so one mistake reports once.</summary>
    internal static readonly PrimitiveType ErrorType = new("<error>");

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly DiagnosticBag _diagnostics;
    private readonly IReadOnlyDictionary<string, (int Index, DslType Type)> _parameters;
    private readonly List<Dictionary<string, (int Slot, DslType Type)>> _scopes = [];

    public ExpressionCompiler(DiagnosticBag diagnostics, IReadOnlyDictionary<string, (int Index, DslType Type)> parameters)
    {
        _diagnostics = diagnostics;
        _parameters = parameters;
        _scopes.Add(new Dictionary<string, (int, DslType)>(StringComparer.Ordinal));
    }

    /// <summary>Highest number of local slots any check needs; frames are sized by it.</summary>
    public int SlotCount { get; private set; }

    public void PushScope() => _scopes.Add(new Dictionary<string, (int, DslType)>(StringComparer.Ordinal));

    public void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    public int Declare(string name, DslType type, SourcePos pos)
    {
        if (IsVariable(name) || _parameters.ContainsKey(name) || Schema.Roots.ContainsKey(name))
        {
            Error(pos, "SR108", $"'{name}' is already defined; choose another name (names cannot be shadowed).");
        }

        var slot = _scopes.Sum(s => s.Count);
        _scopes[^1][name] = (slot, type);
        SlotCount = Math.Max(SlotCount, slot + 1);
        return slot;
    }

    private bool IsVariable(string name) => _scopes.Any(s => s.ContainsKey(name));

    private bool IsKnownName(string name) => IsVariable(name) || _parameters.ContainsKey(name) || Schema.Roots.ContainsKey(name);

    private void Error(SourcePos pos, string code, string message) => _diagnostics.Error(pos, code, message);

    private static Typed Failed() => new(ErrorType, _ => null);

    private static bool IsError(params Typed[] parts) => parts.Any(p => p.Type == ErrorType);

    /// <summary>Compiles a condition: the expression must be boolean.</summary>
    public Typed CompileCondition(Expr expr, string context)
    {
        var typed = Compile(expr);
        if (typed.Type != DslType.Bool && typed.Type != ErrorType && typed.Type != DslType.Null)
        {
            Error(expr.Pos, "SR105", $"The {context} must be a bool, but this expression is {typed.Type}.");
            return Failed();
        }

        return typed;
    }

    public Typed Compile(Expr expr, DslType? expected = null) => expr switch
    {
        LiteralExpr l => Literal(l),
        NameExpr n => Name(n, expected),
        MemberExpr m => Member(m),
        CallExpr c => Call(c),
        ComprehensionExpr c => Comprehension(c),
        UnaryExpr u => Unary(u),
        BinaryExpr b => Binary(b),
        IfExpr i => If(i, expected),
        ListExpr l => List(l, expected),
        _ => throw new ArgumentOutOfRangeException(nameof(expr), expr.GetType().Name, null),
    };

    private static Typed Literal(LiteralExpr l) => l.Kind switch
    {
        LiteralKind.Null => Typed.Const(DslType.Null, null),
        LiteralKind.Bool => Typed.Const(DslType.Bool, Values.Bool((bool)l.Value!)),
        LiteralKind.Integer => Typed.Const(DslType.Int, Values.Int((int)l.Value!)),
        LiteralKind.Decimal => Typed.Const(DslType.Decimal, l.Value),
        LiteralKind.String => Typed.Const(DslType.String, l.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(l), l.Kind, null),
    };

    private Typed Name(NameExpr n, DslType? expected)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(n.Name, out var local))
            {
                var slot = local.Slot;
                return new Typed(local.Type, f => f.Slots[slot]);
            }
        }

        if (_parameters.TryGetValue(n.Name, out var param))
        {
            var index = param.Index;
            return new Typed(param.Type, f => f.Parameters[index]);
        }

        if (Schema.Roots.TryGetValue(n.Name, out var root))
        {
            var read = root.Read;
            return new Typed(root.Type, f => read(f.Context));
        }

        // Bare enum members: resolved against the type the context expects, else across all enums when unique.
        if (expected is EnumType e && e.Members.Contains(n.Name, StringComparer.Ordinal))
        {
            return Typed.Const(e, n.Name);
        }

        var owners = Schema.Enums.Where(x => x.Members.Contains(n.Name, StringComparer.Ordinal)).ToList();
        if (owners.Count == 1)
        {
            return Typed.Const(owners[0], n.Name);
        }

        if (owners.Count > 1)
        {
            Error(n.Pos, "SR110", $"'{n.Name}' is a member of several enums ({string.Join(", ", owners.Select(o => o.Name))}); compare it with a value of the intended type.");
            return Failed();
        }

        var candidates = _scopes.SelectMany(s => s.Keys).Concat(_parameters.Keys).Concat(Schema.Roots.Keys);
        Error(n.Pos, "SR100", $"Unknown name '{n.Name}'.{Suggest(n.Name, candidates)}");
        return Failed();
    }

    private Typed Member(MemberExpr m)
    {
        var target = Compile(m.Target);
        if (IsError(target))
        {
            return Failed();
        }

        if (target.Type is not RecordType record)
        {
            Error(m.Pos, "SR101", $"'{target.Type}' has no members; '.{m.Member}' needs a record.");
            return Failed();
        }

        if (!record.Fields.TryGetValue(m.Member, out var field))
        {
            Error(m.Pos, "SR101", $"'{record.Name}' has no field '{m.Member}'.{Suggest(m.Member, record.Fields.Keys)} Fields: {string.Join(", ", record.Fields.Keys)}.");
            return Failed();
        }

        var eval = target.Eval;
        var getter = field.Getter;
        // Member access on null yields null (like ?. in C#), so "procedure(code).price" is safe for unknown codes.
        return new Typed(field.Type, f => eval(f) is { } o ? getter(o) : null);
    }

    private Typed Unary(UnaryExpr u)
    {
        var operand = u.Operator == "not" ? CompileCondition(u.Operand, "operand of 'not'") : Compile(u.Operand);
        if (IsError(operand))
        {
            return Failed();
        }

        var eval = operand.Eval;
        if (u.Operator == "not")
        {
            return new Typed(DslType.Bool, f => Values.IsTrue(eval(f)) ? Values.False : Values.True);
        }

        if (operand.Type == DslType.Int)
        {
            return new Typed(DslType.Int, f => eval(f) is int i ? Values.Int(checked(-i)) : null);
        }

        if (operand.Type == DslType.Decimal)
        {
            return new Typed(DslType.Decimal, f => eval(f) is decimal d ? -d : null);
        }

        Error(u.Pos, "SR102", $"Unary '-' needs a number, not {operand.Type}.");
        return Failed();
    }

    /// <summary>Compiles two operands so a bare enum member on either side takes its type from the other side.</summary>
    private (Typed Left, Typed Right) Pair(Expr left, Expr right)
    {
        if (left is NameExpr n && !IsKnownName(n.Name))
        {
            var r = Compile(right);
            return (Compile(left, r.Type), r);
        }

        var l = Compile(left);
        return (l, Compile(right, l.Type is ListType lt ? lt.Element : l.Type));
    }

    private Typed Binary(BinaryExpr b)
    {
        switch (b.Operator)
        {
            case "and":
            case "or":
                return Logical(b);
            case "in":
            case "not in":
                return Membership(b);
        }

        var (left, right) = Pair(b.Left, b.Right);
        if (IsError(left, right))
        {
            return Failed();
        }

        var l = left.Eval;
        var r = right.Eval;
        var lt = left.Type;
        var rt = right.Type;

        switch (b.Operator)
        {
            case "==":
            case "!=":
            {
                if (DslType.Unify(lt, rt) is null)
                {
                    return Mismatch(b, lt, rt);
                }

                var negate = b.Operator == "!=";
                return new Typed(DslType.Bool, f => Values.Bool(Values.AreEqual(l(f), r(f)) != negate));
            }

            case "<":
            case "<=":
            case ">":
            case ">=":
            {
                var common = DslType.Unify(lt, rt);
                if (common is null || (!common.IsOrdered && common != DslType.Null))
                {
                    return Mismatch(b, lt, rt);
                }

                Func<int, bool> test = b.Operator switch
                {
                    "<" => c => c < 0,
                    "<=" => c => c <= 0,
                    ">" => c => c > 0,
                    _ => c => c >= 0,
                };

                // The two most common shapes get allocation-free fast paths; everything else goes through Compare.
                if (lt == DslType.Int && rt == DslType.Int)
                {
                    return new Typed(DslType.Bool, f => l(f) is int x && r(f) is int y ? Values.Bool(test(x.CompareTo(y))) : Values.False);
                }

                if (lt == DslType.Date && rt == DslType.Date)
                {
                    return new Typed(DslType.Bool, f => l(f) is DateOnly x && r(f) is DateOnly y ? Values.Bool(test(x.CompareTo(y))) : Values.False);
                }

                return new Typed(DslType.Bool, f => Values.Compare(l(f), r(f)) is { } c ? Values.Bool(test(c)) : Values.False);
            }

            case "??":
            {
                var common = DslType.Unify(lt, rt);
                if (common is null)
                {
                    return Mismatch(b, lt, rt);
                }

                return new Typed(common, f => l(f) ?? r(f));
            }

            case "+":
                return Plus(b, left, right);
            case "-":
                return Minus(b, left, right);
            case "*":
            case "/":
                return MultiplyDivide(b, left, right);
            default:
                throw new ArgumentOutOfRangeException(nameof(b), b.Operator, null);
        }
    }

    private Typed Mismatch(BinaryExpr b, DslType lt, DslType rt)
    {
        Error(b.Pos, "SR102", $"Operator '{b.Operator}' cannot combine {lt} and {rt}.");
        return Failed();
    }

    private Typed Logical(BinaryExpr b)
    {
        var left = CompileCondition(b.Left, $"left operand of '{b.Operator}'");
        var right = CompileCondition(b.Right, $"right operand of '{b.Operator}'");
        if (IsError(left, right))
        {
            return Failed();
        }

        var l = left.Eval;
        var r = right.Eval;
        return b.Operator == "and"
            ? new Typed(DslType.Bool, f => Values.IsTrue(l(f)) && Values.IsTrue(r(f)) ? Values.True : Values.False)
            : new Typed(DslType.Bool, f => Values.IsTrue(l(f)) || Values.IsTrue(r(f)) ? Values.True : Values.False);
    }

    private Typed Membership(BinaryExpr b)
    {
        Typed left, right;
        if (b.Left is NameExpr n && !IsKnownName(n.Name))
        {
            right = Compile(b.Right);
            left = Compile(b.Left, right.Type is ListType rl ? rl.Element : null);
        }
        else
        {
            left = Compile(b.Left);
            right = Compile(b.Right, new ListType(left.Type));
        }

        if (IsError(left, right))
        {
            return Failed();
        }

        if (right.Type is not ListType list || DslType.Unify(left.Type, list.Element) is null)
        {
            return Mismatch(b, left.Type, right.Type);
        }

        var l = left.Eval;
        var r = right.Eval;
        var negate = b.Operator == "not in";
        return new Typed(DslType.Bool, f =>
        {
            var found = false;
            if (l(f) is { } value && r(f) is IReadOnlyList<object?> items)
            {
                for (var k = 0; k < items.Count; k++)
                {
                    if (Values.AreEqual(value, items[k]))
                    {
                        found = true;
                        break;
                    }
                }
            }

            return Values.Bool(found != negate);
        });
    }

    private Typed Plus(BinaryExpr b, Typed left, Typed right)
    {
        var (l, r, lt, rt) = (left.Eval, right.Eval, left.Type, right.Type);
        if (lt == DslType.Int && rt == DslType.Int)
        {
            return new Typed(DslType.Int, f => l(f) is int x && r(f) is int y ? Values.Int(checked(x + y)) : null);
        }

        if (lt.IsNumeric && rt.IsNumeric)
        {
            return new Typed(DslType.Decimal, f => l(f) is { } x && r(f) is { } y ? Values.ToDecimal(x) + Values.ToDecimal(y) : null);
        }

        if (lt == DslType.String && rt == DslType.String)
        {
            return new Typed(DslType.String, f => l(f) is string x && r(f) is string y ? string.Concat(x, y) : null);
        }

        if (lt == DslType.Date && rt == DslType.Int)
        {
            return new Typed(DslType.Date, f => l(f) is DateOnly d && r(f) is int n ? d.AddDays(n) : null);
        }

        if (lt == DslType.String || rt == DslType.String)
        {
            Error(b.Pos, "SR102", $"Operator '+' cannot combine {lt} and {rt}; convert with str() to build text.");
            return Failed();
        }

        return Mismatch(b, lt, rt);
    }

    private Typed Minus(BinaryExpr b, Typed left, Typed right)
    {
        var (l, r, lt, rt) = (left.Eval, right.Eval, left.Type, right.Type);
        if (lt == DslType.Int && rt == DslType.Int)
        {
            return new Typed(DslType.Int, f => l(f) is int x && r(f) is int y ? Values.Int(checked(x - y)) : null);
        }

        if (lt.IsNumeric && rt.IsNumeric)
        {
            return new Typed(DslType.Decimal, f => l(f) is { } x && r(f) is { } y ? Values.ToDecimal(x) - Values.ToDecimal(y) : null);
        }

        if (lt == DslType.Date && rt == DslType.Int)
        {
            return new Typed(DslType.Date, f => l(f) is DateOnly d && r(f) is int n ? d.AddDays(-n) : null);
        }

        if (lt == DslType.Date && rt == DslType.Date)
        {
            // Whole days between two dates: invoice.date - line.date is the invoicing lag.
            return new Typed(DslType.Int, f => l(f) is DateOnly x && r(f) is DateOnly y ? Values.Int(x.DayNumber - y.DayNumber) : null);
        }

        return Mismatch(b, lt, rt);
    }

    private Typed MultiplyDivide(BinaryExpr b, Typed left, Typed right)
    {
        var (l, r, lt, rt) = (left.Eval, right.Eval, left.Type, right.Type);
        if (!lt.IsNumeric || !rt.IsNumeric)
        {
            return Mismatch(b, lt, rt);
        }

        if (b.Operator == "*")
        {
            return lt == DslType.Int && rt == DslType.Int
                ? new Typed(DslType.Int, f => l(f) is int x && r(f) is int y ? Values.Int(checked(x * y)) : null)
                : new Typed(DslType.Decimal, f => l(f) is { } x && r(f) is { } y ? Values.ToDecimal(x) * Values.ToDecimal(y) : null);
        }

        // Division is always decimal; dividing by zero yields null ("no value") rather than failing the whole batch.
        return new Typed(DslType.Decimal, f =>
            l(f) is { } x && r(f) is { } y && Values.ToDecimal(y) != 0m ? Values.ToDecimal(x) / Values.ToDecimal(y) : null);
    }

    private Typed If(IfExpr i, DslType? expected)
    {
        var condition = CompileCondition(i.Condition, "if condition");
        var then = Compile(i.Then, expected);
        var otherwise = Compile(i.Else, expected ?? then.Type);
        if (IsError(condition, then, otherwise))
        {
            return Failed();
        }

        var type = DslType.Unify(then.Type, otherwise.Type);
        if (type is null)
        {
            Error(i.Pos, "SR102", $"The branches of 'if' have different types: {then.Type} and {otherwise.Type}.");
            return Failed();
        }

        var (c, t, e) = (condition.Eval, then.Eval, otherwise.Eval);
        return new Typed(type, f => Values.IsTrue(c(f)) ? t(f) : e(f));
    }

    private Typed List(ListExpr l, DslType? expected)
    {
        var elementHint = (expected as ListType)?.Element;
        var items = l.Items.Select(x => Compile(x, elementHint)).ToList();
        if (IsError([.. items]))
        {
            return Failed();
        }

        DslType element = elementHint ?? DslType.Null;
        if (items.Count > 0)
        {
            element = items[0].Type;
        }

        foreach (var item in items.Skip(1))
        {
            if (DslType.Unify(element, item.Type) is not { } unified)
            {
                Error(l.Pos, "SR102", $"List items have incompatible types: {element} and {item.Type}.");
                return Failed();
            }

            element = unified;
        }

        if (items.All(x => x.IsConstant))
        {
            object?[] constant = [.. items.Select(x => x.Constant)];
            return Typed.Const(new ListType(element), constant);
        }

        var evals = items.Select(x => x.Eval).ToArray();
        return new Typed(new ListType(element), f =>
        {
            var values = new object?[evals.Length];
            for (var k = 0; k < evals.Length; k++)
            {
                values[k] = evals[k](f);
            }

            return values;
        });
    }

    // ---- comprehensions ---------------------------------------------------------------------------------------

    private Typed Comprehension(ComprehensionExpr c)
    {
        var source = Compile(c.Source);
        if (IsError(source))
        {
            return Failed();
        }

        if (source.Type is not ListType list)
        {
            Error(c.Source.Pos, "SR107", $"{c.Function}(... in ...) needs a list, but the source is {source.Type}.");
            return Failed();
        }

        PushScope();
        try
        {
            var slot = Declare(c.Variable, list.Element, c.Pos);
            var filter = c.Filter is null ? null : CompileCondition(c.Filter, $"'where' filter of {c.Function}");
            var projection = c.Projection is null ? null : Compile(c.Projection);
            if ((filter is not null && IsError(filter)) || (projection is not null && IsError(projection)))
            {
                return Failed();
            }

            return BuildComprehension(c, source.Eval, slot, list.Element, filter?.Eval, projection);
        }
        finally
        {
            PopScope();
        }
    }

    private Typed BuildComprehension(ComprehensionExpr c, Eval source, int slot, DslType element, Eval? filter, Typed? projection)
    {
        switch (c.Function)
        {
            case "count":
                if (projection is not null)
                {
                    Error(c.Pos, "SR107", "count() takes a 'where' filter, not a ': projection'.");
                    return Failed();
                }

                return new Typed(DslType.Int, f => Values.Int(Count(f, source, slot, filter)));

            case "any":
            case "all":
            {
                if (projection is not null && projection.Type != DslType.Bool)
                {
                    Error(c.Pos, "SR105", $"The ': condition' of {c.Function}() must be a bool, not {projection.Type}.");
                    return Failed();
                }

                if (c.Function == "all" && projection is null)
                {
                    Error(c.Pos, "SR107", "all() needs a condition: all(x in xs: condition).");
                    return Failed();
                }

                var test = projection?.Eval;
                var all = c.Function == "all";
                return new Typed(DslType.Bool, f =>
                {
                    var items = f.Slots;
                    if (source(f) is not IReadOnlyList<object?> list)
                    {
                        return Values.Bool(all);
                    }

                    for (var k = 0; k < list.Count; k++)
                    {
                        items[slot] = list[k];
                        if (filter is not null && !Values.IsTrue(filter(f)))
                        {
                            continue;
                        }

                        var ok = test is null || Values.IsTrue(test(f));
                        if (ok != all)
                        {
                            return Values.Bool(!all);
                        }
                    }

                    return Values.Bool(all);
                });
            }

            case "sum":
            {
                var valueType = projection?.Type ?? element;
                if (!valueType.IsNumeric)
                {
                    Error(c.Pos, "SR107", $"sum() needs numbers, not {valueType}; add ': <number>' to choose what to add up.");
                    return Failed();
                }

                var value = projection?.Eval ?? (f => f.Slots[slot]);
                if (valueType == DslType.Int)
                {
                    return new Typed(DslType.Int, f => Values.Int(SumInt(f, source, slot, filter, value)));
                }

                return new Typed(DslType.Decimal, f => SumDecimal(f, source, slot, filter, value));
            }

            case "min":
            case "max":
            {
                var valueType = projection?.Type ?? element;
                if (!valueType.IsOrdered)
                {
                    Error(c.Pos, "SR107", $"{c.Function}() needs numbers, strings or dates, not {valueType}.");
                    return Failed();
                }

                var value = projection?.Eval ?? (f => f.Slots[slot]);
                var sign = c.Function == "min" ? -1 : 1;
                // Empty input (or only nulls) gives null, which lets "least(a, b)" and "x > max(...)" fall through.
                return new Typed(valueType, f => Extremum(f, source, slot, filter, value, sign));
            }

            case "first":
            {
                var value = projection?.Eval;
                return new Typed(projection?.Type ?? element, f =>
                {
                    if (source(f) is not IReadOnlyList<object?> list)
                    {
                        return null;
                    }

                    for (var k = 0; k < list.Count; k++)
                    {
                        var item = list[k];
                        f.Slots[slot] = item;
                        if (filter is null || Values.IsTrue(filter(f)))
                        {
                            return value is null ? item : value(f);
                        }
                    }

                    return null;
                });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(c), c.Function, null);
        }
    }

    // Aggregation loops are static methods over indexes, not lambdas over foreach: a closure or a boxed interface
    // enumerator per evaluation was the largest allocation source in the engine (see docs/benchmarks.md).
    private static int Count(Frame f, Eval source, int slot, Eval? filter)
    {
        if (source(f) is not IReadOnlyList<object?> list)
        {
            return 0;
        }

        var n = 0;
        for (var k = 0; k < list.Count; k++)
        {
            f.Slots[slot] = list[k];
            if (filter is null || Values.IsTrue(filter(f)))
            {
                n++;
            }
        }

        return n;
    }

    private static int SumInt(Frame f, Eval source, int slot, Eval? filter, Eval value)
    {
        if (source(f) is not IReadOnlyList<object?> list)
        {
            return 0;
        }

        var total = 0;
        for (var k = 0; k < list.Count; k++)
        {
            f.Slots[slot] = list[k];
            if ((filter is null || Values.IsTrue(filter(f))) && value(f) is int v)
            {
                total = checked(total + v);
            }
        }

        return total;
    }

    private static decimal SumDecimal(Frame f, Eval source, int slot, Eval? filter, Eval value)
    {
        if (source(f) is not IReadOnlyList<object?> list)
        {
            return 0m;
        }

        var total = 0m;
        for (var k = 0; k < list.Count; k++)
        {
            f.Slots[slot] = list[k];
            if ((filter is null || Values.IsTrue(filter(f))) && value(f) is { } v)
            {
                total += Values.ToDecimal(v);
            }
        }

        return total;
    }

    private static object? Extremum(Frame f, Eval source, int slot, Eval? filter, Eval value, int sign)
    {
        if (source(f) is not IReadOnlyList<object?> list)
        {
            return null;
        }

        object? best = null;
        for (var k = 0; k < list.Count; k++)
        {
            f.Slots[slot] = list[k];
            if (filter is not null && !Values.IsTrue(filter(f)))
            {
                continue;
            }

            var v = value(f);
            if (v is not null && (best is null || Math.Sign(Values.Compare(v, best)!.Value) == sign))
            {
                best = v;
            }
        }

        return best;
    }

    // ---- built-in functions -----------------------------------------------------------------------------------

    /// <summary>Signatures shown in diagnostics and in the language reference.</summary>
    public static readonly IReadOnlyDictionary<string, string> Builtins = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["str"] = "str(x) -> string: invariant text; dates as yyyy-MM-dd; null as \"\"",
        ["money"] = "money(n) -> string: two decimals, invariant (120.00)",
        ["matches"] = "matches(s, \"regex\") -> bool: whole-pattern match, linear-time engine; null is false",
        ["starts_with"] = "starts_with(s, prefix) -> bool: ordinal; null is false",
        ["len"] = "len(s) -> int",
        ["substr"] = "substr(s, start, length) -> string: zero-based, clamped to the string",
        ["join"] = "join(list<string>, separator) -> string",
        ["count"] = "count(list) -> int, or count(x in xs where condition)",
        ["age"] = "age(birth_date, on_date) -> int: completed years",
        ["least"] = "least(a, b, ...) -> smallest non-null argument (PostgreSQL semantics)",
        ["greatest"] = "greatest(a, b, ...) -> largest non-null argument (PostgreSQL semantics)",
        ["between"] = "between(x, low, high) -> bool: inclusive; null is false",
        ["procedure"] = "procedure(code) -> procedure: the code in the SUT version in force, or null",
        ["doctor"] = "doctor(registration) -> doctor: the physician on the invoice, or null",
        ["date"] = "date(\"yyyy-MM-dd\") -> date literal",
    };

    private Typed Call(CallExpr c)
    {
        if (!Builtins.ContainsKey(c.Function))
        {
            var hint = Parser.Comprehensions.Contains(c.Function)
                ? $" Use the comprehension form: {c.Function}(x in list ...)."
                : Suggest(c.Function, Builtins.Keys);
            Error(c.Pos, "SR103", $"Unknown function '{c.Function}'.{hint}");
            return Failed();
        }

        if (c.Function == "date")
        {
            return DateLiteral(c);
        }

        if (c.Function == "matches")
        {
            return Matches(c);
        }

        var args = c.Arguments.Select(a => Compile(a)).ToList();
        if (IsError([.. args]))
        {
            return Failed();
        }

        var e = args.Select(a => a.Eval).ToArray();
        switch (c.Function)
        {
            case "str":
                return Arity(c, args, 1) ? new Typed(DslType.String, f => Values.Display(e[0](f))) : Failed();
            case "money":
                return Arity(c, args, 1) && Expect(c, args, 0, t => t.IsNumeric, "a number")
                    ? new Typed(DslType.String, f => e[0](f) is { } v ? Values.ToDecimal(v).ToString("0.00", CultureInfo.InvariantCulture) : null)
                    : Failed();
            case "starts_with":
                return Arity(c, args, 2) && Expect(c, args, 0, IsString, "a string") && Expect(c, args, 1, IsString, "a string")
                    ? new Typed(DslType.Bool, f => Values.Bool(e[0](f) is string s && e[1](f) is string p && s.StartsWith(p, StringComparison.Ordinal)))
                    : Failed();
            case "len":
                return Arity(c, args, 1) && Expect(c, args, 0, IsString, "a string")
                    ? new Typed(DslType.Int, f => e[0](f) is string s ? Values.Int(s.Length) : null)
                    : Failed();
            case "substr":
                return Arity(c, args, 3) && Expect(c, args, 0, IsString, "a string") && Expect(c, args, 1, IsInt, "an int") && Expect(c, args, 2, IsInt, "an int")
                    ? new Typed(DslType.String, f => e[0](f) is string s && e[1](f) is int start && e[2](f) is int length ? Substring(s, start, length) : null)
                    : Failed();
            case "join":
                return Arity(c, args, 2) && Expect(c, args, 0, t => t is ListType { Element: var el } && (el == DslType.String || el == DslType.Null), "a list of strings") && Expect(c, args, 1, IsString, "a string")
                    ? new Typed(DslType.String, f => string.Join(e[1](f) as string ?? string.Empty, (e[0](f) as IReadOnlyList<object?> ?? []).Select(Values.Display)))
                    : Failed();
            case "count":
                return Arity(c, args, 1) && Expect(c, args, 0, t => t is ListType, "a list")
                    ? new Typed(DslType.Int, f => Values.Int(e[0](f) is IReadOnlyList<object?> list ? list.Count : 0))
                    : Failed();
            case "age":
                return Arity(c, args, 2) && Expect(c, args, 0, IsDate, "a date") && Expect(c, args, 1, IsDate, "a date")
                    ? new Typed(DslType.Int, f => e[0](f) is DateOnly birth && e[1](f) is DateOnly on ? Values.Int(Domain.Patient.Age(birth, on)) : null)
                    : Failed();
            case "least":
            case "greatest":
                return Extremum(c, args, e);
            case "between":
            {
                if (!Arity(c, args, 3))
                {
                    return Failed();
                }

                var common = DslType.Unify(DslType.Unify(args[0].Type, args[1].Type) ?? ErrorType, args[2].Type);
                if (common is null || common == ErrorType || !common.IsOrdered)
                {
                    Error(c.Pos, "SR102", $"between() needs three values of one ordered type, not {args[0].Type}, {args[1].Type}, {args[2].Type}.");
                    return Failed();
                }

                return new Typed(DslType.Bool, f =>
                {
                    var x = e[0](f);
                    return Values.Bool(Values.Compare(x, e[1](f)) >= 0 && Values.Compare(x, e[2](f)) <= 0);
                });
            }

            case "procedure":
                return Arity(c, args, 1) && Expect(c, args, 0, IsString, "a SUT code string")
                    ? new Typed(Schema.Procedure, f => f.Context.Procedure(e[0](f) as string))
                    : Failed();
            case "doctor":
                return Arity(c, args, 1) && Expect(c, args, 0, IsString, "a registration number string")
                    ? new Typed(Schema.Doctor, f => f.Context.Doctor(e[0](f) as string))
                    : Failed();
            default:
                throw new InvalidOperationException($"Built-in '{c.Function}' has no implementation.");
        }
    }

    private Typed Extremum(CallExpr c, List<Typed> args, Eval[] e)
    {
        if (args.Count == 0)
        {
            Error(c.Pos, "SR104", $"{c.Function}() needs at least one argument.");
            return Failed();
        }

        DslType type = DslType.Null;
        foreach (var a in args)
        {
            if (DslType.Unify(type, a.Type) is not { } unified)
            {
                Error(c.Pos, "SR102", $"{c.Function}() arguments must share one ordered type.");
                return Failed();
            }

            type = unified;
        }

        if (!type.IsOrdered && type != DslType.Null)
        {
            Error(c.Pos, "SR102", $"{c.Function}() needs numbers, strings or dates, not {type}.");
            return Failed();
        }

        var sign = c.Function == "least" ? -1 : 1;
        return new Typed(type, f =>
        {
            object? best = null;
            foreach (var eval in e)
            {
                var v = eval(f);
                if (v is not null && (best is null || Math.Sign(Values.Compare(v, best)!.Value) == sign))
                {
                    best = v;
                }
            }

            return best;
        });
    }

    private Typed DateLiteral(CallExpr c)
    {
        if (c.Arguments is [LiteralExpr { Kind: LiteralKind.String, Value: string text }]
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return Typed.Const(DslType.Date, date);
        }

        Error(c.Pos, "SR104", "date() takes one string literal in yyyy-MM-dd form, e.g. date(\"2026-01-01\").");
        return Failed();
    }

    private Typed Matches(CallExpr c)
    {
        if (c.Arguments.Count != 2 || c.Arguments[1] is not LiteralExpr { Kind: LiteralKind.String, Value: string pattern })
        {
            Error(c.Pos, "SR104", "matches() takes a string and a regular expression written as a string literal.");
            return Failed();
        }

        var subject = Compile(c.Arguments[0]);
        if (IsError(subject))
        {
            return Failed();
        }

        if (!IsString(subject.Type))
        {
            Error(c.Arguments[0].Pos, "SR102", $"matches() needs a string, not {subject.Type}.");
            return Failed();
        }

        Regex regex;
        try
        {
            // Patterns come from rule files, which reviewers read but which are still input: the non-backtracking
            // engine runs in linear time, so no pattern can hang the engine (ReDoS); the timeout is a second fence.
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Error(c.Arguments[1].Pos, "SR106", $"Invalid or unsupported regular expression: {ex.Message}");
            return Failed();
        }

        var eval = subject.Eval;
        return new Typed(DslType.Bool, f => Values.Bool(eval(f) is string s && regex.IsMatch(s)));
    }

    private bool Arity(CallExpr c, List<Typed> args, int count)
    {
        if (args.Count == count)
        {
            return true;
        }

        Error(c.Pos, "SR104", $"{c.Function}() takes {count} argument(s), not {args.Count}. {Builtins[c.Function]}");
        return false;
    }

    private bool Expect(CallExpr c, List<Typed> args, int index, Func<DslType, bool> ok, string what)
    {
        var type = args[index].Type;
        if (type == DslType.Null || ok(type))
        {
            return true;
        }

        Error(c.Arguments[index].Pos, "SR102", $"Argument {index + 1} of {c.Function}() must be {what}, not {type}.");
        return false;
    }

    private static bool IsString(DslType t) => t == DslType.String;

    private static bool IsInt(DslType t) => t == DslType.Int;

    private static bool IsDate(DslType t) => t == DslType.Date;

    private static string Substring(string s, int start, int length)
    {
        start = Math.Clamp(start, 0, s.Length);
        length = Math.Clamp(length, 0, s.Length - start);
        return s.Substring(start, length);
    }

    /// <summary>"Did you mean" for names within edit distance 2.</summary>
    internal static string Suggest(string name, IEnumerable<string> candidates)
    {
        var best = candidates
            .Distinct(StringComparer.Ordinal)
            .Select(c => (c, d: Distance(name, c)))
            .Where(x => x.d <= 2)
            .OrderBy(x => x.d)
            .ThenBy(x => x.c, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.c is null ? string.Empty : $" Did you mean '{best.c}'?";
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }
}
