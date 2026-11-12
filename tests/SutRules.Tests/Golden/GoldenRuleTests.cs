using SutRules.Engine;

namespace SutRules.Tests.Golden;

/// <summary>
/// Runs <c>rules/golden/*.json</c>: one theory row per case ("RULE / case name"), so a failing case is named in
/// the test report, plus the coverage contract (every rule, every version block, firing and clean cases).
/// </summary>
public sealed class GoldenRuleTests
{
    private static readonly Lazy<(IReadOnlyList<GoldenResult> Results, IReadOnlyList<string> Coverage)> Run = new(() =>
    {
        var results = GoldenRunner.Run(TestData.Bundle, TestData.RulesDirectory, out var coverage);
        return (results, coverage);
    });

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var r in Run.Value.Results)
        {
            data.Add($"{r.Rule} / {r.Case}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden_case(string name)
    {
        var result = Run.Value.Results.Single(r => $"{r.Rule} / {r.Case}" == name);
        result.Passed.Should().BeTrue(result.Failure);
    }

    [Fact]
    public void Every_rule_and_every_version_block_is_covered()
    {
        Run.Value.Coverage.Should().BeEmpty();
        Run.Value.Results.Select(r => r.Rule).Distinct().Should().HaveCount(TestData.Bundle.Rules.Count);
    }

    [Fact]
    public void Coverage_check_reports_a_rule_without_cases()
    {
        var files = new[] { new GoldenFile("MDL-001", [new GoldenCase("clean", null, [])]) };
        var baseInvoice = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TestData.RulesDirectory, "golden", "_base.json")))!.AsObject();
        GoldenRunner.Run(TestData.Bundle, baseInvoice, files, out var coverage);

        coverage.Should().Contain("MDL-001: no passing case where the rule fires");
        coverage.Should().Contain(p => p.StartsWith("PRV-001: no golden cases", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_patch_follows_rfc_7386()
    {
        // Examples from RFC 7386 section 3: members merge, null removes, arrays replace.
        var target = System.Text.Json.Nodes.JsonNode.Parse("""{"a":"b","c":{"d":"e","f":"g"},"l":[1,2]}""")!;
        var patch = System.Text.Json.Nodes.JsonNode.Parse("""{"a":"z","c":{"f":null},"l":[3],"n":{"x":1}}""");
        GoldenRunner.MergePatch(target, patch).ToJsonString().Should().Be("""{"a":"z","c":{"d":"e"},"l":[3],"n":{"x":1}}""");
    }
}
