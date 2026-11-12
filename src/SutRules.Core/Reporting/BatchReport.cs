using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SutRules.Engine;

namespace SutRules.Reporting;

public sealed record ReportSummary(
    int Invoices,
    int Clean,
    int NeedsReview,
    int Rejected,
    int Findings,
    decimal TotalAmount,
    decimal AmountAtRisk);

public sealed record RuleCount(string RuleId, string TitleTr, string TitleEn, int Count);

/// <summary>
/// The rejection-reason report. It carries invoice numbers and codes only, never patient identity, and names the
/// rule bundle (<see cref="Ruleset"/>) that produced it.
/// </summary>
public sealed record BatchReport(
    DateTimeOffset GeneratedAt,
    string Engine,
    string Ruleset,
    ReportSummary Summary,
    IReadOnlyList<RuleCount> ByRule,
    IReadOnlyList<InvoiceResult> Invoices)
{
    public const string EngineName = "sut-rules";

    /// <summary>Reading and writing invoices and reports: camelCase, enum names, readable Turkish, LF line endings.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BatchReport Create(RuleBundle bundle, IReadOnlyList<InvoiceResult> results, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(results);

        var findings = results.SelectMany(r => r.Findings).ToList();
        var byRule = findings
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .Select(g =>
            {
                var rule = bundle.Find(g.Key);
                return new RuleCount(g.Key, rule?.Title.Tr ?? "Fatura tarihinde geçerli SUT sürümü yok",
                    rule?.Title.En ?? "No SUT version valid on the invoice date", g.Count());
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();

        var summary = new ReportSummary(
            results.Count,
            results.Count(r => r.Outcome == InvoiceOutcome.Clean),
            results.Count(r => r.Outcome == InvoiceOutcome.NeedsReview),
            results.Count(r => r.Outcome == InvoiceOutcome.Rejected),
            findings.Count,
            results.Sum(r => r.Total),
            results.Sum(r => r.AmountAtRisk));

        return new BatchReport(generatedAt, EngineName, bundle.Hash, summary, byRule, results);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Markdown for a pull request comment or a billing team handover; LF line endings on every OS.</summary>
    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var s = Summary;
        var sb = new StringBuilder();
        sb.Append("# SUT pre-check report / Ön kontrol raporu\n\n");
        sb.Append(inv, $"Generated {GeneratedAt:yyyy-MM-dd HH:mm} UTC by `{Engine}`, rule bundle `{Ruleset[..12]}`. Synthetic data.\n\n");
        sb.Append("| Invoices | Clean | Needs review | Would be rejected | Findings | Total (TRY) | At risk (TRY) |\n");
        sb.Append("|---:|---:|---:|---:|---:|---:|---:|\n");
        sb.Append(inv, $"| {s.Invoices} | {s.Clean} | {s.NeedsReview} | {s.Rejected} | {s.Findings} | {s.TotalAmount:N2} | {s.AmountAtRisk:N2} |\n\n");

        sb.Append("## Findings by rule\n\n| Rule | Kural | Rule (EN) | Count |\n|---|---|---|---:|\n");
        foreach (var r in ByRule)
        {
            sb.Append(inv, $"| `{r.RuleId}` | {r.TitleTr} | {r.TitleEn} | {r.Count} |\n");
        }

        sb.Append("\n## Invoices with findings\n\n");
        foreach (var invoice in Invoices.Where(i => i.Findings.Count > 0))
        {
            sb.Append(inv, $"### {invoice.InvoiceNo} — {invoice.Outcome} ({invoice.SutVersion ?? "no version"}, at risk {invoice.AmountAtRisk:N2} TRY)\n\n");
            sb.Append("| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |\n|---|---|---:|---|---|---|\n");
            foreach (var f in invoice.Findings)
            {
                sb.Append(inv,
                    $"| `{f.RuleId}` | {f.Severity} | {f.LineNo?.ToString(inv) ?? "-"} | {f.SutCode ?? "-"} | {Cell(f.Reason.Tr)}<br>_{Cell(f.Reason.En)}_ | {Cell(f.Suggestion.Tr)}<br>_{Cell(f.Suggestion.En)}_ |\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
