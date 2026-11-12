using System.Text.Json;
using System.Text.Json.Nodes;
using SutRules.Domain;
using SutRules.Reporting;

namespace SutRules.Engine;

/// <summary>One expected finding. Null fields in a case are not checked, except <c>line</c>, where null means invoice-level.</summary>
public sealed record GoldenExpectation(
    int? Line,
    Severity Severity,
    string? Expected,
    string? Actual,
    string? Code = null,
    string? ReasonEn = null,
    string? FixValue = null);

public sealed record GoldenCase(string Name, JsonObject? Patch, IReadOnlyList<GoldenExpectation> Expect);

public sealed record GoldenFile(string Rule, IReadOnlyList<GoldenCase> Cases);

public sealed record GoldenResult(string Rule, string Case, string? SutVersion, bool Passed, string? Failure, bool Fired);

/// <summary>
/// Golden tests per rule: <c>rules/golden/&lt;RULE&gt;.json</c> holds cases, each an RFC 7386 JSON merge patch over
/// <c>rules/golden/_base.json</c> plus the exact findings the rule must produce, nothing more and nothing less. Only
/// the rule under test runs, so a case documents one rule. Coverage is enforced: every rule needs a file, a firing
/// case, a clean case, and a firing case in every version block it has.
/// </summary>
public static class GoldenRunner
{
    public const string Directory = "golden";
    public const string BaseFile = "_base.json";

    public static IReadOnlyList<GoldenResult> Run(RuleBundle bundle, string rulesDirectory, out IReadOnlyList<string> coverageProblems)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var folder = Path.Combine(rulesDirectory, Directory);
        var baseInvoice = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, BaseFile)))!.AsObject();
        var files = System.IO.Directory.EnumerateFiles(folder, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_'))
            .Order(StringComparer.Ordinal)
            .Select(p => JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(p), BatchReport.JsonOptions)
                ?? throw new InvalidDataException($"Empty golden file {p}."))
            .ToList();
        return Run(bundle, baseInvoice, files, out coverageProblems);
    }

    public static IReadOnlyList<GoldenResult> Run(RuleBundle bundle, JsonObject baseInvoice, IReadOnlyList<GoldenFile> files, out IReadOnlyList<string> coverageProblems)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(baseInvoice);
        ArgumentNullException.ThrowIfNull(files);

        var engine = new RuleEngine(bundle);
        var results = new List<GoldenResult>();
        foreach (var file in files)
        {
            foreach (var c in file.Cases)
            {
                results.Add(RunCase(engine, baseInvoice, file.Rule, c));
            }
        }

        coverageProblems = Coverage(bundle, files, results);
        return results;
    }

    private static GoldenResult RunCase(RuleEngine engine, JsonObject baseInvoice, string ruleId, GoldenCase c)
    {
        Invoice invoice;
        try
        {
            var merged = MergePatch(baseInvoice.DeepClone(), c.Patch);
            invoice = merged.Deserialize<Invoice>(BatchReport.JsonOptions) ?? throw new JsonException("null invoice");
        }
        catch (JsonException ex)
        {
            return new GoldenResult(ruleId, c.Name, null, false, $"invalid invoice after patch: {ex.Message}", false);
        }

        if (engine.Bundle.Find(ruleId) is null)
        {
            return new GoldenResult(ruleId, c.Name, null, false, $"rule {ruleId} is not in the bundle", false);
        }

        var result = engine.Check(invoice, new HashSet<string>(StringComparer.Ordinal) { ruleId });
        var actual = result.Findings;
        var problems = new List<string>();
        if (actual.Count != c.Expect.Count)
        {
            problems.Add($"expected {c.Expect.Count} finding(s), got {actual.Count}");
        }

        for (var i = 0; i < Math.Min(actual.Count, c.Expect.Count); i++)
        {
            var (e, f) = (c.Expect[i], actual[i]);
            Compare(problems, i, "line", e.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture), f.LineNo?.ToString(System.Globalization.CultureInfo.InvariantCulture), always: true);
            Compare(problems, i, "severity", e.Severity.ToString(), f.Severity.ToString(), always: true);
            Compare(problems, i, "expected", e.Expected, f.Expected, always: true);
            Compare(problems, i, "actual", e.Actual, f.Actual, always: true);
            Compare(problems, i, "code", e.Code, f.SutCode, always: false);
            Compare(problems, i, "reasonEn", e.ReasonEn, f.Reason.En, always: false);
            Compare(problems, i, "fixValue", e.FixValue, f.Fix.Value, always: false);
        }

        foreach (var extra in actual.Skip(c.Expect.Count))
        {
            problems.Add($"unexpected finding: line {extra.LineNo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, expected '{extra.Expected}', actual '{extra.Actual}'");
        }

        return new GoldenResult(ruleId, c.Name, result.SutVersion, problems.Count == 0, problems.Count == 0 ? null : string.Join("; ", problems), actual.Count > 0);
    }

    private static void Compare(List<string> problems, int index, string field, string? expected, string? actual, bool always)
    {
        if ((always || expected is not null) && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            problems.Add($"finding {index + 1} {field}: expected '{expected ?? "null"}', got '{actual ?? "null"}'");
        }
    }

    private static List<string> Coverage(RuleBundle bundle, IReadOnlyList<GoldenFile> files, List<GoldenResult> results)
    {
        var problems = new List<string>();
        var byRule = files.GroupBy(f => f.Rule, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (rule, count) in byRule.Where(x => x.Value > 1))
        {
            problems.Add($"{rule}: {count} golden files; keep one per rule");
        }

        foreach (var rule in bundle.Rules)
        {
            var cases = results.Where(r => r.Rule == rule.Id).ToList();
            if (cases.Count == 0)
            {
                problems.Add($"{rule.Id}: no golden cases (add {Directory}/{rule.Id}.json)");
                continue;
            }

            if (!cases.Any(c => c.Fired && c.Passed))
            {
                problems.Add($"{rule.Id}: no passing case where the rule fires");
            }

            if (!cases.Any(c => !c.Fired && c.Passed))
            {
                problems.Add($"{rule.Id}: no passing clean case (the rule must also be shown not to fire)");
            }

            // Each version block that puts the rule in force must be exercised by a firing case on an invoice whose
            // SUT version resolves to that block, so a severity or parameter change in an amendment is tested.
            foreach (var block in rule.Versions.Where(v => !v.Retired))
            {
                var versionsInBlock = bundle.Catalog.Versions
                    .Where(v => rule.StateFor(v)?.Block == block.SutVersion)
                    .Select(v => v.Id)
                    .ToHashSet(StringComparer.Ordinal);
                if (!cases.Any(c => c.Fired && c.Passed && c.SutVersion is not null && versionsInBlock.Contains(c.SutVersion)))
                {
                    problems.Add($"{rule.Id}: version block '{block.SutVersion}' has no passing firing case");
                }
            }
        }

        foreach (var orphan in byRule.Keys.Where(id => bundle.Find(id) is null))
        {
            problems.Add($"{orphan}: golden file for a rule that does not exist");
        }

        return problems;
    }

    /// <summary>RFC 7386 JSON Merge Patch: objects merge recursively, null deletes a member, anything else replaces.</summary>
    public static JsonNode MergePatch(JsonNode target, JsonNode? patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone() ?? target;
        }

        if (target is not JsonObject targetObject)
        {
            targetObject = [];
        }

        foreach (var (name, value) in patchObject)
        {
            if (value is null)
            {
                targetObject.Remove(name);
            }
            else if (targetObject[name] is { } existing && value is JsonObject)
            {
                targetObject[name] = MergePatch(existing.DeepClone(), value);
            }
            else
            {
                targetObject[name] = value is JsonObject ? MergePatch(new JsonObject(), value) : value.DeepClone();
            }
        }

        return targetObject;
    }
}
