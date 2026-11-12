using System.Globalization;
using System.Text;

namespace SutRules.Language;

/// <summary>
/// Canonical formatter (<c>sut-rules fmt</c>). Parentheses are emitted only where precedence needs them, so
/// <c>parse(print(x))</c> has the same structure as <c>x</c>; a property test holds the printer to that.
/// </summary>
public static class Printer
{
    private const char Lf = '\n';

    public static string Print(RuleDecl rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var sb = new StringBuilder();
        sb.Append("rule ").Append(Quote(rule.Id)).Append(" {").Append(Lf);
        if (rule.Category is not null)
        {
            sb.Append("  category ").Append(rule.Category).Append(Lf);
        }

        if (rule.Title is not null)
        {
            AppendBilingual(sb, "  title", rule.Title);
        }

        if (rule.Basis is not null)
        {
            sb.Append("  basis ").Append(Quote(rule.Basis)).Append(Lf);
        }

        if (rule.Params.Count > 0)
        {
            sb.Append(Lf);
            foreach (var p in rule.Params)
            {
                sb.Append("  param ").Append(p.Name).Append(' ').Append(p.TypeName).Append(Lf);
            }
        }

        if (rule.Versions.Count > 0)
        {
            sb.Append(Lf);
            foreach (var v in rule.Versions)
            {
                sb.Append("  version ").Append(Quote(v.SutVersion)).Append(" {");
                if (v.Severity is not null)
                {
                    sb.Append(" severity ").Append(v.Severity);
                }

                if (v.Retired)
                {
                    sb.Append(" retired");
                }

                foreach (var a in v.Assignments)
                {
                    sb.Append(' ').Append(a.Name).Append(" = ").Append(Print(a.Value));
                }

                sb.Append(" }").Append(Lf);
            }
        }

        foreach (var check in rule.Checks)
        {
            sb.Append(Lf);
            sb.Append(check.Variable is null
                ? "  check invoice {"
                : $"  check each {check.Variable} in {Print(check.Source!)} {{").Append(Lf);
            foreach (var statement in check.Statements)
            {
                AppendStatement(sb, statement);
            }

            sb.Append("  }").Append(Lf);
        }

        if (rule.Reason is not null || rule.Fix is not null)
        {
            sb.Append(Lf);
        }

        if (rule.Reason is not null)
        {
            AppendBilingual(sb, "  reason", rule.Reason);
        }

        if (rule.Fix is not null)
        {
            sb.Append("  fix ").Append(rule.Fix.Action);
            if (rule.Fix.ValueSource is not null)
            {
                sb.Append(" value ").Append(rule.Fix.ValueSource);
            }

            sb.Append(Lf);
            sb.Append("    tr ").Append(Quote(rule.Fix.Text.Tr)).Append(Lf);
            sb.Append("    en ").Append(Quote(rule.Fix.Text.En)).Append(Lf);
        }

        sb.Append('}').Append(Lf);
        return sb.ToString();
    }

    private static void AppendBilingual(StringBuilder sb, string head, Bilingual text)
    {
        sb.Append(head).Append(" tr ").Append(Quote(text.Tr)).Append(Lf);
        sb.Append(' ', head.Length + 1).Append("en ").Append(Quote(text.En)).Append(Lf);
    }

    private static void AppendStatement(StringBuilder sb, Statement statement)
    {
        switch (statement)
        {
            case LetStatement letStmt:
                sb.Append("    let ").Append(letStmt.Name).Append(" = ").Append(Print(letStmt.Value)).Append(Lf);
                break;
            case RequireStatement require:
                sb.Append("    require ").Append(Print(require.Condition)).Append(Lf);
                break;
            case WhenStatement whenStmt:
                sb.Append("    when ").Append(Print(whenStmt.Condition)).Append(Lf);
                sb.Append("      report");
                if (whenStmt.At is not null)
                {
                    sb.Append(" at ").Append(Print(whenStmt.At));
                }

                if (whenStmt.Expected is not null)
                {
                    sb.Append(" expected ").Append(Print(whenStmt.Expected));
                }

                if (whenStmt.Actual is not null)
                {
                    sb.Append(" actual ").Append(Print(whenStmt.Actual));
                }

                sb.Append(Lf);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(statement), statement.GetType().Name, null);
        }
    }

    public static string Print(Expr expr) => Print(expr, 0);

    // Precedence levels; a child is parenthesised when it binds more loosely than its position requires.
    private const int PIf = 0, POr = 1, PAnd = 2, PNot = 3, PCmp = 4, PCoalesce = 5, PAdd = 6, PMul = 7, PUnary = 8, PPostfix = 9;

    private static int Precedence(Expr expr) => expr switch
    {
        IfExpr => PIf,
        BinaryExpr b => BinaryPrecedence(b.Operator),
        UnaryExpr { Operator: "not" } => PNot,
        UnaryExpr => PUnary,
        _ => PPostfix,
    };

    private static int BinaryPrecedence(string op) => op switch
    {
        "or" => POr,
        "and" => PAnd,
        "??" => PCoalesce,
        "+" or "-" => PAdd,
        "*" or "/" => PMul,
        _ => PCmp,
    };

    private static string Print(Expr expr, int minimum)
    {
        var text = expr switch
        {
            LiteralExpr l => Literal(l),
            NameExpr n => n.Name,
            MemberExpr m => Print(m.Target, PPostfix) + "." + m.Member,
            CallExpr c => c.Function + "(" + string.Join(", ", c.Arguments.Select(a => Print(a, 0))) + ")",
            ComprehensionExpr c => Comprehension(c),
            UnaryExpr { Operator: "not" } u => "not " + Print(u.Operand, PNot),
            UnaryExpr u => u.Operator + Print(u.Operand, PUnary),
            BinaryExpr b => Binary(b),
            IfExpr i => "if " + Print(i.Condition, 0) + " then " + Print(i.Then, 0) + " else " + Print(i.Else, 0),
            ListExpr l => "[" + string.Join(", ", l.Items.Select(x => Print(x, 0))) + "]",
            _ => throw new ArgumentOutOfRangeException(nameof(expr), expr.GetType().Name, null),
        };

        return Precedence(expr) < minimum ? "(" + text + ")" : text;
    }

    private static string Binary(BinaryExpr b)
    {
        var p = BinaryPrecedence(b.Operator);
        // Left-associative operators: the right operand needs parentheses at equal precedence. Comparisons do not
        // chain, so both of their operands must bind tighter.
        var left = Print(b.Left, p == PCmp ? p + 1 : p);
        var right = Print(b.Right, p + 1);
        return left + " " + b.Operator + " " + right;
    }

    private static string Comprehension(ComprehensionExpr c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Function).Append('(').Append(c.Variable).Append(" in ").Append(Print(c.Source, 0));
        if (c.Filter is not null)
        {
            sb.Append(" where ").Append(Print(c.Filter, 0));
        }

        if (c.Projection is not null)
        {
            sb.Append(": ").Append(Print(c.Projection, 0));
        }

        return sb.Append(')').ToString();
    }

    private static string Literal(LiteralExpr l) => l.Kind switch
    {
        LiteralKind.Null => "null",
        LiteralKind.Bool => (bool)l.Value! ? "true" : "false",
        LiteralKind.Integer => ((int)l.Value!).ToString(CultureInfo.InvariantCulture),
        LiteralKind.Decimal => ((decimal)l.Value!).ToString(CultureInfo.InvariantCulture),
        LiteralKind.String => Quote((string)l.Value!),
        _ => throw new ArgumentOutOfRangeException(nameof(l), l.Kind, null),
    };

    /// <summary>
    /// Inverse of the lexer's string rule: a backslash is doubled only where the lexer would otherwise read it as an
    /// escape (before a quote, another backslash, or the closing quote).
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                sb.Append("\\\"");
            }
            else if (c == '\\' && (i + 1 == value.Length || value[i + 1] is '"' or '\\'))
            {
                sb.Append("\\\\");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>Position-free structural dump of an expression, for equality checks in tests.</summary>
    public static string Sexp(Expr expr) => expr switch
    {
        LiteralExpr l => l.Kind + ":" + Literal(l),
        NameExpr n => n.Name,
        MemberExpr m => "(. " + Sexp(m.Target) + " " + m.Member + ")",
        CallExpr c => "(call " + c.Function + string.Concat(c.Arguments.Select(a => " " + Sexp(a))) + ")",
        ComprehensionExpr c => "(" + c.Function + " " + c.Variable + " " + Sexp(c.Source)
            + " " + (c.Filter is null ? "_" : Sexp(c.Filter)) + " " + (c.Projection is null ? "_" : Sexp(c.Projection)) + ")",
        UnaryExpr u => "(" + u.Operator + " " + Sexp(u.Operand) + ")",
        BinaryExpr b => "(" + b.Operator + " " + Sexp(b.Left) + " " + Sexp(b.Right) + ")",
        IfExpr i => "(if " + Sexp(i.Condition) + " " + Sexp(i.Then) + " " + Sexp(i.Else) + ")",
        ListExpr l => "[" + string.Join(" ", l.Items.Select(Sexp)) + "]",
        _ => throw new ArgumentOutOfRangeException(nameof(expr), expr.GetType().Name, null),
    };
}
