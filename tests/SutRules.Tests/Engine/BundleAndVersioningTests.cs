using System.Text;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Language;

namespace SutRules.Tests.Engine;

public sealed class BundleAndVersioningTests
{
    private const string Header = """
        category administrative
        title tr "T" en "T"
        basis "B"
        """;

    private const string Footer = """
        reason tr "R {expected}" en "R {expected}"
        fix ChangeInvoiceDate tr "F" en "F"
        """;

    private static RuleSource Rule(string id, string body) =>
        new($"{id}.rule", $"rule \"{id}\" {{\n{Header}\n{body}\n{Footer}\n}}\n");

    private static readonly Invoice Base = TestData.ParityInvoices()[0];

    private static Invoice On(DateOnly date) => Base with { InvoiceDate = date };

    [Fact]
    public void Shipped_bundle_loads_22_rules_with_a_stable_hash()
    {
        var bundle = TestData.Bundle;
        bundle.Rules.Should().HaveCount(22);
        bundle.Rules.Select(r => r.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);
        bundle.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
        RuleBundle.Compile(TestData.CatalogBytes, TestData.RuleSources()).Hash.Should().Be(bundle.Hash);
    }

    [Fact]
    public void Hash_ignores_line_endings_but_not_content()
    {
        var sources = TestData.RuleSources();
        var crlf = sources.Select(s => s with { Text = s.Text.Replace("\n", "\r\n", StringComparison.Ordinal) });
        RuleBundle.Compile(TestData.CatalogBytes, crlf).Hash.Should().Be(TestData.Bundle.Hash);

        var edited = sources.Select(s => s.File == "ADM-001.rule" ? s with { Text = s.Text.Replace("= 45", "= 44", StringComparison.Ordinal) } : s);
        RuleBundle.Compile(TestData.CatalogBytes, edited).Hash.Should().NotBe(TestData.Bundle.Hash);
    }

    [Fact]
    public void A_broken_file_fails_the_whole_bundle_and_every_error_is_reported()
    {
        var sources = TestData.RuleSources().ToList();
        sources.Add(new RuleSource("ZZZ-001.rule", "rule \"ZZZ-001\" { category nope }"));
        sources.Add(Rule("ABC-001", "version \"SUT-2025.01\" { severity error }\ncheck invoice { when nosuch report }") with { File = "ABC-001.rule" });
        sources.Add(Rule("ABC-002", "version \"SUT-2025.01\" { severity error }\ncheck invoice { when true report }") with { File = "WRONG.rule" });

        var act = () => RuleBundle.Compile(TestData.CatalogBytes, sources);
        var error = act.Should().Throw<RuleCompilationException>().Which;
        error.Diagnostics.Select(d => (d.File, d.Code)).Should().Contain([
            ("ABC-001.rule", "SR100"),
            ("WRONG.rule", "SR211"),
            ("ZZZ-001.rule", "SR201"),
        ]);
    }

    [Fact]
    public void Duplicate_rule_ids_are_rejected()
    {
        var source = Rule("ABC-001", "version \"SUT-2025.01\" { severity error }\ncheck invoice { when true report }");
        var act = () => RuleBundle.Compile(TestData.CatalogBytes, [source, source]);
        act.Should().Throw<RuleCompilationException>().Which.Diagnostics.Should().Contain(d => d.Code == "SR212");
    }

    [Fact]
    public void Version_blocks_resolve_to_one_state_per_sut_version()
    {
        var adm = TestData.Bundle.Find("ADM-001")!;
        var catalog = TestData.Bundle.Catalog;
        adm.StateFor(catalog.Find("SUT-2025.01")!).Should().BeNull("ADM-001 starts with the mid-2025 amendment");

        var mid2025 = adm.StateFor(catalog.Find("SUT-2025.07")!)!;
        mid2025.Severity.Should().Be(Severity.Warning);
        mid2025.ParametersByName["max_lag_days"].Should().Be(60);
        mid2025.EffectiveTo.Should().Be(new DateOnly(2026, 1, 1));

        var y2026 = adm.StateFor(catalog.Find("SUT-2026.01")!)!;
        y2026.Severity.Should().Be(Severity.Warning, "severity is inherited from the previous block");
        y2026.ParametersByName["max_lag_days"].Should().Be(45);
        y2026.Block.Should().Be("SUT-2026.01");
        y2026.EffectiveTo.Should().BeNull();
    }

    [Fact]
    public void A_retired_rule_stops_and_can_come_back()
    {
        var bundle = RuleBundle.Compile(TestData.CatalogBytes, [Rule("ABC-001", """
            version "SUT-2025.01" { severity warning }
            version "SUT-2025.07" { retired }
            version "SUT-2026.01" { severity error }
            check invoice { when true report expected sut.id }
            """)]);
        var engine = new RuleEngine(bundle);

        engine.Check(On(new DateOnly(2025, 3, 1))).Findings.Should().ContainSingle().Which.Severity.Should().Be(Severity.Warning);
        engine.Check(On(new DateOnly(2025, 9, 1))).Findings.Should().BeEmpty();
        var revived = engine.Check(On(new DateOnly(2026, 3, 1))).Findings.Should().ContainSingle().Which;
        revived.Severity.Should().Be(Severity.Error);
        revived.Expected.Should().Be("SUT-2026.01");
    }

    [Theory]
    [InlineData("2025-06-30", "SUT-2025.01")]
    [InlineData("2025-07-01", "SUT-2025.07")]
    [InlineData("2025-12-31", "SUT-2025.07")]
    [InlineData("2026-01-01", "SUT-2026.01")]
    public void The_sut_version_is_chosen_by_invoice_date_with_exclusive_upper_bounds(string date, string version) =>
        new RuleEngine(TestData.Bundle).Check(On(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture))).SutVersion.Should().Be(version);

    [Fact]
    public void An_invoice_before_every_sut_version_is_rejected_not_passed()
    {
        var result = new RuleEngine(TestData.Bundle).Check(On(new DateOnly(2024, 12, 31)));
        result.Outcome.Should().Be(InvoiceOutcome.Rejected);
        result.SutVersion.Should().BeNull();
        var finding = result.Findings.Should().ContainSingle().Which;
        finding.RuleId.Should().Be(RuleEngine.NoVersionRuleId);
        finding.Expected.Should().Be("2025-01-01");
        result.AmountAtRisk.Should().Be(result.Total);
    }

    [Fact]
    public void Runtime_failures_name_the_rule_and_invoice()
    {
        var bundle = RuleBundle.Compile(TestData.CatalogBytes, [Rule("ABC-001", """
            version "SUT-2025.01" { severity error }
            check invoice { when 2147483647 + count(lines) > 0 report }
            """)]);
        var act = () => new RuleEngine(bundle).Check(Base);
        var error = act.Should().Throw<RuleRuntimeException>().Which;
        error.RuleId.Should().Be("ABC-001");
        error.InvoiceNo.Should().Be(Base.InvoiceNo);
        error.InnerException.Should().BeOfType<OverflowException>();
    }

    [Fact]
    public void Parallel_and_sequential_runs_agree()
    {
        var invoices = TestData.ParityInvoices();
        var engine = new RuleEngine(TestData.Bundle);
        var sequential = engine.CheckAll(invoices);
        var parallel = engine.CheckAll(invoices, parallel: true);
        parallel.Select(r => r.Findings.Count).Should().Equal(sequential.Select(r => r.Findings.Count));
        parallel.Select(r => r.InvoiceNo).Should().Equal(sequential.Select(r => r.InvoiceNo));
    }

    [Fact]
    public void Load_from_directory_requires_the_catalog()
    {
        var dir = Directory.CreateTempSubdirectory("sutrules-");
        try
        {
            var act = () => RuleBundle.Load(dir.FullName);
            act.Should().Throw<RuleCompilationException>().Which.Diagnostics.Single().Code.Should().Be("SR220");

            Directory.CreateDirectory(Path.Combine(dir.FullName, "catalog"));
            File.WriteAllText(Path.Combine(dir.FullName, RuleBundle.CatalogFile), "{ \"versions\": [] }", Encoding.UTF8);
            act.Should().Throw<RuleCompilationException>().Which.Diagnostics.Single().Message.Should().Contain("no version");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
