using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SutRules.Domain;
using SutRules.Engine;

namespace SutRules.Tests.Properties;

/// <summary>
/// Invariants over arbitrary invoices, including hostile ones (unknown codes, missing physicians, null strings,
/// empty lists, dates around every amendment): the engine never throws, never invents a line, and its verdict is a
/// pure function of the findings.
/// </summary>
public sealed class EnginePropertyTests
{
    private static readonly string[] Codes =
        [.. TestData.Bundle.Catalog.Versions.SelectMany(v => v.Procedures.Keys).Distinct().Order(StringComparer.Ordinal), "999999", "", null!];

    private static readonly DateOnly Origin = new(2024, 11, 1);

    private static Gen<DateOnly> Day => Gen.Choose(0, 600).Select(d => Origin.AddDays(d));

    private static Gen<string> Registration => Gen.Elements("100001", "100002", "100009", "AB12", null!);

    private static Gen<ServiceLine> Line(int no) =>
        from code in Gen.Elements(Codes)
        from date in Day
        from qty in Gen.Choose(1, 5)
        from price in Gen.Elements(12.40m, 42.00m, 85.00m, 120.00m, 135.00m, 650.00m)
        from doctor in Registration
        select new ServiceLine(no, code, date, qty, price, doctor);

    private static Gen<Invoice> InvoiceGen =>
        from date in Day
        from birth in Gen.Choose(0, 36_000).Select(d => new DateOnly(1925, 1, 1).AddDays(d))
        from sex in Gen.Elements(Gender.Female, Gender.Male)
        from treatment in Gen.Elements(Enum.GetValues<TreatmentType>())
        from type in Gen.Elements(Enum.GetValues<ProvisionType>())
        from admission in Day
        from stay in Gen.Choose(-1, 10)
        from tracking in Gen.Elements("TK1234567", "bad", null!)
        from icds in Gen.ListOf(Gen.Elements("J18.9", "O80", "N40.1", "E11.9", "V43.5", "x", null!))
        from primaryMask in Gen.Choose(0, 7)
        from lineCount in Gen.Choose(0, 6)
        from lines in Gen.CollectToArray(Enumerable.Range(1, lineCount), Line)
        from priorCodes in Gen.ListOf(from c in Gen.Elements(Codes) from d in Day from b in Gen.Elements("1100", "1600") select new PriorService(c, d, 1, b))
        select new Invoice(
            "PROP-1",
            date,
            "SYN001",
            new Patient("P1", birth, sex),
            new Provision(tracking, admission, stay < 0 ? null : admission.AddDays(stay), treatment, type, "1100"),
            [new Doctor("100001", "1100"), new Doctor("100002", "1600")],
            [.. icds.Take(4).Select((icd, i) => new Diagnosis(icd, (primaryMask & (1 << i)) != 0))],
            [.. lines],
            [.. priorCodes.Take(6)]);

    public static Arbitrary<Invoice> Invoices() => InvoiceGen.ToArbitrary();

    [Property(Arbitrary = [typeof(EnginePropertyTests)], MaxTest = 2000)]
    public bool Never_throws_and_findings_point_at_real_lines(Invoice invoice)
    {
        var result = new RuleEngine(TestData.Bundle).Check(invoice);
        var lineNumbers = invoice.Lines.Select(l => l.LineNo).ToHashSet();
        return result.Findings.All(f =>
            (f.LineNo is null || lineNumbers.Contains(f.LineNo.Value))
            && (f.RuleId == RuleEngine.NoVersionRuleId || TestData.Bundle.Find(f.RuleId) is not null));
    }

    [Property(Arbitrary = [typeof(EnginePropertyTests)], MaxTest = 1000)]
    public bool Outcome_and_amount_at_risk_follow_from_the_findings(Invoice invoice)
    {
        var result = new RuleEngine(TestData.Bundle).Check(invoice);
        var expectedOutcome = result.Findings.Any(f => f.Severity == Severity.Error) ? InvoiceOutcome.Rejected
            : result.Findings.Any(f => f.Severity == Severity.Warning) ? InvoiceOutcome.NeedsReview
            : InvoiceOutcome.Clean;
        return result.Outcome == expectedOutcome
            && result.AmountAtRisk >= 0m
            && result.AmountAtRisk <= result.Total
            && (result.Outcome == InvoiceOutcome.Rejected || result.AmountAtRisk == 0m);
    }

    [Property(Arbitrary = [typeof(EnginePropertyTests)], MaxTest = 500)]
    public bool Findings_are_ordered_by_line_then_rule(Invoice invoice)
    {
        var findings = new RuleEngine(TestData.Bundle).Check(invoice).Findings;
        return findings.Zip(findings.Skip(1)).All(p =>
            (p.First.LineNo ?? 0) < (p.Second.LineNo ?? 0)
            || ((p.First.LineNo ?? 0) == (p.Second.LineNo ?? 0) && string.CompareOrdinal(p.First.RuleId, p.Second.RuleId) <= 0));
    }

    [Property(MaxTest = 1000)]
    public bool Exactly_the_version_whose_window_contains_the_date_is_chosen(int offset)
    {
        var date = new DateOnly(2024, 6, 1).AddDays(Math.Abs(offset % 1400));
        var catalog = TestData.Bundle.Catalog;
        var chosen = catalog.ResolveFor(date);
        var containing = catalog.Versions.Where(v => v.EffectiveFrom <= date && (v.EffectiveTo is null || date < v.EffectiveTo)).ToList();
        return containing.Count <= 1 && chosen == containing.SingleOrDefault() && (chosen is null) == (date < catalog.Versions[0].EffectiveFrom);
    }
}
