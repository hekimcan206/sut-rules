using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SutRules.Engine;
using SutRules.Language;

namespace SutRules.Tests.Properties;

/// <summary>
/// The grammar is unambiguous and the printer is its exact inverse: for any expression tree, printing and parsing
/// again gives the same tree. This is what lets precedence live in one place (the parser) and the printer add only
/// the parentheses it must.
/// </summary>
public sealed class LanguagePropertyTests
{
    private static readonly SourcePos P = new(1, 1);

    private static readonly string[] Names = ["a", "b", "line", "lines", "total", "x_1"];
    private static readonly string[] Members = ["code", "date", "no", "min_age"];
    private static readonly string[] Functions = ["str", "least", "greatest", "join", "procedure"];
    private static readonly string[] Comprehensions = ["count", "sum", "min", "max", "any", "all", "first"];
    private static readonly string[] BinaryOps = ["or", "and", "==", "!=", "<", "<=", ">", ">=", "in", "not in", "??", "+", "-", "*", "/"];

    private static Gen<string> StringValue =>
        Gen.ArrayOf(Gen.Elements('a', 'Z', '0', ' ', '\\', '"', '{', '}', 'ş', '.', '^', '$'))
            .Where(c => c.Length <= 8)
            .Select(c => new string(c));

    private static Gen<Expr> Leaf => Gen.OneOf(
        Gen.Elements(Names).Select(n => (Expr)new NameExpr(n, P)),
        Gen.Choose(0, 5000).Select(i => (Expr)new LiteralExpr(LiteralKind.Integer, i, P)),
        Gen.Choose(0, 99_999).Select(i => (Expr)new LiteralExpr(LiteralKind.Decimal, new decimal(i, 0, 0, false, 2), P)),
        StringValue.Select(s => (Expr)new LiteralExpr(LiteralKind.String, s, P)),
        Gen.Elements(true, false).Select(b => (Expr)new LiteralExpr(LiteralKind.Bool, b, P)),
        Gen.Constant((Expr)new LiteralExpr(LiteralKind.Null, null, P)));

    private static Gen<Expr> Tree(int size)
    {
        if (size <= 0)
        {
            return Leaf;
        }

        var sub = Gen.Sized(_ => Tree(size / 2));
        return Gen.Frequency(
            (3, Leaf),
            (2, from t in sub from m in Gen.Elements(Members) select (Expr)new MemberExpr(t, m, P)),
            (4, from op in Gen.Elements(BinaryOps) from l in sub from r in sub select (Expr)new BinaryExpr(op, l, r, P)),
            (1, from op in Gen.Elements("not", "-") from o in sub select (Expr)new UnaryExpr(op, o, P)),
            (1, from c in sub from t in sub from e in sub select (Expr)new IfExpr(c, t, e, P)),
            (1, from f in Gen.Elements(Functions) from args in Gen.ListOf(sub) where args.Count <= 3 select (Expr)new CallExpr(f, [.. args], P)),
            (1, from items in Gen.ListOf(sub) where items.Count <= 3 select (Expr)new ListExpr([.. items], P)),
            (1, from f in Gen.Elements(Comprehensions)
                from v in Gen.Elements("x", "y")
                from src in sub
                from hasFilter in Gen.Elements(true, false)
                from filter in sub
                from hasProjection in Gen.Elements(true, false)
                from projection in sub
                select (Expr)new ComprehensionExpr(f, v, src, hasFilter ? filter : null, hasProjection ? projection : null, P)));
    }

    public static Arbitrary<Expr> Expressions() => Gen.Sized(s => Tree(Math.Min(s, 24))).ToArbitrary();

    [Property(Arbitrary = [typeof(LanguagePropertyTests)], MaxTest = 1000)]
    public bool Print_then_parse_gives_the_same_tree(Expr expr)
    {
        var printed = Printer.Print(expr);
        var bag = new DiagnosticBag("prop");
        var parsed = Parser.ParseExpression(printed, bag);
        return parsed is not null && Printer.Sexp(parsed) == Printer.Sexp(expr);
    }

    [Property(MaxTest = 300)]
    public bool String_literals_survive_quoting(NonNull<string> value)
    {
        var s = value.Get.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
        var bag = new DiagnosticBag("prop");
        var parsed = Parser.ParseExpression(Printer.Quote(s), bag);
        return parsed is LiteralExpr { Value: string back } && back == s;
    }

    [Fact]
    public void Shipped_rules_print_to_a_fixed_point_that_behaves_identically()
    {
        var reprinted = new List<RuleSource>();
        foreach (var source in TestData.RuleSources())
        {
            var first = Printer.Print(Parser.ParseRule(source.Text, new DiagnosticBag(source.File))!);
            var second = Printer.Print(Parser.ParseRule(first, new DiagnosticBag(source.File))!);
            second.Should().Be(first, $"{source.File} must reach a fixed point after one print");
            reprinted.Add(source with { Text = first });
        }

        var original = new RuleEngine(TestData.Bundle);
        var printed = new RuleEngine(RuleBundle.Compile(TestData.CatalogBytes, reprinted));
        foreach (var invoice in TestData.ParityInvoices().Take(300))
        {
            printed.Check(invoice).Findings.Should().Equal(original.Check(invoice).Findings);
        }
    }
}
