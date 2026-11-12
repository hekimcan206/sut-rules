using SutRules.Domain;
using SutRules.Engine;
using SutRules.Language;

namespace SutRules.Tests.Language;

/// <summary>The language's value semantics, one rule per test: nulls, dates, numbers, collections, enums.</summary>
public sealed class ExpressionSemanticsTests
{
    private static readonly Invoice Sample = new(
        "T-1",
        new DateOnly(2026, 2, 10),
        "SYN001",
        new Patient("P1", new DateOnly(1980, 5, 15), Gender.Female),
        new Provision("TK1234567", new DateOnly(2026, 2, 1), null, TreatmentType.Outpatient, ProvisionType.Normal, "1100"),
        [new Doctor("100001", "1100")],
        [new Diagnosis("E11.9", true), new Diagnosis("I10", false)],
        [
            new ServiceLine(1, "900101", new DateOnly(2026, 2, 1), 1, 135.00m, "100001"),
            new ServiceLine(2, "901002", new DateOnly(2026, 2, 3), 2, 69.10m, "100001"),
        ],
        []);

    internal static object? Eval(string source, Invoice? invoice = null)
    {
        var bag = new DiagnosticBag("expr");
        var expr = Parser.ParseExpression(source, bag);
        bag.Items.Should().BeEmpty();
        var compiler = new ExpressionCompiler(bag, new Dictionary<string, (int, DslType)>());
        var typed = compiler.Compile(expr!);
        bag.Items.Should().BeEmpty(string.Join("; ", bag.Items));
        invoice ??= Sample;
        var context = new EvalContext(invoice, TestData.Bundle.Catalog.ResolveFor(invoice.InvoiceDate)!);
        return typed.Eval(new Frame(Math.Max(1, compiler.SlotCount), [], context));
    }

    internal static IReadOnlyList<Diagnostic> Errors(string source)
    {
        var bag = new DiagnosticBag("expr");
        var expr = Parser.ParseExpression(source, bag);
        if (expr is not null)
        {
            new ExpressionCompiler(bag, new Dictionary<string, (int, DslType)>()).Compile(expr);
        }

        return bag.Items;
    }

    [Theory]
    [InlineData("provision.discharge > invoice.date", false)]  // relational with null is false
    [InlineData("provision.discharge < invoice.date", false)]
    [InlineData("provision.discharge == null", true)]
    [InlineData("not (provision.discharge > invoice.date)", true)] // two-valued at the boolean level
    [InlineData("procedure(\"000000\").price == null", true)]     // member access on null propagates null
    [InlineData("count(procedure(\"000000\").required_icd) == 0", true)]
    public void Null_semantics(string expression, bool expected) => Eval(expression).Should().Be(expected);

    [Theory]
    [InlineData("invoice.date - provision.admission", 9)]
    [InlineData("str(invoice.date - 40)", "2026-01-01")]
    [InlineData("str(provision.admission + 28)", "2026-03-01")]
    [InlineData("age(patient.birth_date, date(\"2026-05-14\"))", 45)]
    [InlineData("age(patient.birth_date, date(\"2026-05-15\"))", 46)]
    public void Date_arithmetic(string expression, object expected) => Eval(expression).Should().Be(expected);

    [Fact]
    public void Numbers_widen_and_keep_their_scale()
    {
        Eval("1 + 2").Should().Be(3);
        Eval("1 + 0.50").Should().Be(1.50m);
        Eval("money(lines.amount)".Replace("lines.amount", "sum(l in lines: l.amount)", StringComparison.Ordinal)).Should().Be("273.20");
        Eval("str(135.00)").Should().Be("135.00");
        Eval("7 / 2").Should().Be(3.5m);
        Eval("7 / 0").Should().BeNull("division by zero yields no value rather than failing the batch");
    }

    [Fact]
    public void Least_and_greatest_ignore_nulls_like_postgresql()
    {
        Eval("str(greatest(provision.admission, provision.discharge))").Should().Be("2026-02-01");
        Eval("least(3, null, 1)").Should().Be(1);
        Eval("greatest(null, null)").Should().BeNull();
    }

    [Fact]
    public void Comprehensions()
    {
        Eval("count(l in lines where l.quantity > 1)").Should().Be(1);
        Eval("sum(l in lines: l.quantity)").Should().Be(3);
        Eval("str(max(l in lines: l.date))").Should().Be("2026-02-03");
        Eval("min(l in lines where l.code == \"x\": l.no)").Should().BeNull("min over nothing is null");
        Eval("sum(l in lines where l.code == \"x\": l.quantity)").Should().Be(0);
        Eval("any(d in diagnoses: starts_with(d.icd, \"E11\"))").Should().Be(true);
        Eval("all(d in diagnoses: d.primary)").Should().Be(false);
        Eval("all(d in diagnoses where d.primary: d.primary)").Should().Be(true);
        Eval("first(l in lines where l.no == 2).code").Should().Be("901002");
        Eval("any(l in lines: any(m in lines: m.no != l.no and m.doctor == l.doctor))").Should().Be(true);
    }

    [Fact]
    public void Enum_members_resolve_from_context()
    {
        Eval("provision.treatment == Outpatient").Should().Be(true);
        Eval("Outpatient == provision.treatment").Should().Be(true);
        Eval("provision.type in [WorkAccident, TrafficAccident]").Should().Be(false);
        Eval("procedure(\"900101\").kind == Examination").Should().Be(true);
        Eval("str(patient.sex)").Should().Be("Female");
    }

    [Fact]
    public void Membership_and_strings()
    {
        Eval("\"1100\" in procedure(\"901008\").allowed_branches").Should().Be(true);
        Eval("\"1200\" not in procedure(\"901008\").allowed_branches").Should().Be(true);
        Eval("join(procedure(\"901008\").allowed_branches, \"|\")").Should().Be("1100|1900|2000");
        Eval("substr(\"O24.4\", 0, 3)").Should().Be("O24");
        Eval("substr(\"O2\", 0, 3)").Should().Be("O2");
        Eval("matches(\"TK1234567\", \"^[0-9A-Z]{7,10}$\")").Should().Be(true);
        Eval("between(\"O24\", \"O00\", \"O9A\")").Should().Be(true);
        Eval("between(\"P00\", \"O00\", \"O9A\")").Should().Be(false);
        Eval("\"a\" + \"b\"").Should().Be("ab");
        Eval("provision.tracking_no ?? \"none\"").Should().Be("TK1234567");
    }

    [Fact]
    public void Reference_data_follows_the_sut_version_of_the_invoice()
    {
        Eval("procedure(\"900101\").price").Should().Be(135.00m);
        Eval("procedure(\"900101\").price", Sample with { InvoiceDate = new DateOnly(2025, 3, 1) }).Should().Be(120.00m);
        Eval("procedure(\"901009\") == null").Should().Be(true, "the 2026 amendment withdrew 901009");
        Eval("sut.id", Sample with { InvoiceDate = new DateOnly(2025, 12, 31) }).Should().Be("SUT-2025.07");
    }

    [Theory]
    [InlineData("1 + \"a\"", "SR102")]
    [InlineData("lines.code", "SR101")]
    [InlineData("nosuch", "SR100")]
    [InlineData("Female == Inpatient", "SR102")]
    [InlineData("frobnicate(1)", "SR103")]
    [InlineData("any(lines)", "SR103")]
    [InlineData("str(1, 2)", "SR104")]
    [InlineData("date(\"2026-13-01\")", "SR104")]
    [InlineData("matches(\"a\", invoice.no)", "SR104")]
    [InlineData("if true then 1 else \"x\"", "SR102")]
    [InlineData("count(l in invoice where true)", "SR107")]
    [InlineData("1 < 2 < 3", "SR010")]
    [InlineData("\"open", "SR002")]
    [InlineData("1 $ 2", "SR003")]
    public void Type_and_syntax_errors_have_stable_codes(string expression, string code) =>
        Errors(expression).Select(d => d.Code).Should().Contain(code);

    [Fact]
    public void One_mistake_reports_once()
    {
        // The unknown name poisons the whole expression; the operators around it must not add noise.
        Errors("nosuch.field + 1 > 2 and true").Should().ContainSingle().Which.Code.Should().Be("SR100");
    }
}
