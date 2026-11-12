using System.Text.Json;
using System.Text.Json.Nodes;
using SutRules.Engine;
using SutRules.Reporting;

namespace SutRules.Tests.Parity;

/// <summary>
/// The claim "medula-precheck could adopt this engine" made testable. The fixture is 2,000 synthetic invoices from
/// medula-precheck v1.0.0's generator (seed 22, defect rate 0.4, 2025-01-01..2026-06-30) and the report its
/// hand-written C# engine produced for them. The 22 rules here are written in the rule language only; every invoice
/// result must match that report as JSON: outcome, totals, amount at risk, and each finding's rule, severity, line,
/// code, expected and actual values, Turkish and English texts and fix.
/// </summary>
public sealed class MedulaPrecheckParityTests
{
    [Fact]
    public void Every_invoice_result_matches_the_hand_written_engine()
    {
        var invoices = TestData.ParityInvoices();
        var engine = new RuleEngine(TestData.Bundle);
        var ours = JsonSerializer.SerializeToNode(engine.CheckAll(invoices), BatchReport.JsonOptions)!.AsArray();
        var theirs = JsonNode.Parse(TestData.ReadGzip("Parity/medula-precheck-report.json.gz"))!["invoices"]!.AsArray();

        ours.Count.Should().Be(2000);
        theirs.Count.Should().Be(ours.Count);
        var mismatches = new List<string>();
        for (var i = 0; i < ours.Count; i++)
        {
            if (!JsonNode.DeepEquals(ours[i], theirs[i]))
            {
                mismatches.Add($"{theirs[i]!["invoiceNo"]}:\n  medula-precheck: {theirs[i]!.ToJsonString()}\n  sut-rules:       {ours[i]!.ToJsonString()}");
            }
        }

        mismatches.Should().BeEmpty(string.Join("\n", mismatches.Take(3)));
    }

    [Fact]
    public void The_fixture_exercises_every_rule()
    {
        var report = JsonNode.Parse(TestData.ReadGzip("Parity/medula-precheck-report.json.gz"))!;
        var fired = report["byRule"]!.AsArray().Select(r => (string)r!["ruleId"]!).ToHashSet();
        fired.Should().BeEquivalentTo(TestData.Bundle.Rules.Select(r => r.Id));
    }
}
