using SutRules.Domain;

namespace SutRules.Engine;

// Result types keep medula-precheck's names and JSON shape on purpose: a billing system that consumes
// medula-precheck reports can switch to this engine without a mapping layer, and the parity test compares the two
// outputs as JSON.

/// <summary>Error: SGK would reject or cut it. Warning: likely to be questioned in review (inceleme).</summary>
public enum Severity
{
    Info,
    Warning,
    Error,
}

public enum RuleCategory
{
    Structural,
    Pricing,
    Demographic,
    Clinical,
    Frequency,
    Package,
    Provision,
    Administrative,
}

/// <summary>Machine-readable correction, so a billing UI can offer a one-click fix rather than free text only.</summary>
public enum FixAction
{
    CorrectField,
    SetUnitPrice,
    ReduceQuantity,
    RemoveLine,
    AddDiagnosis,
    CorrectDiagnosis,
    ChangeDoctor,
    ChangeProvision,
    ChangeInvoiceDate,
    ReviewClinically,
}

public sealed record LocalizedText(string Tr, string En);

public sealed record SuggestedFix(FixAction Action, int? LineNo, string? Value);

/// <summary>One rule violation; <see cref="Expected"/> is the value the rule wanted.</summary>
public sealed record Finding(
    string RuleId,
    RuleCategory Category,
    Severity Severity,
    int? LineNo,
    string? SutCode,
    string? Expected,
    string? Actual,
    LocalizedText Reason,
    LocalizedText Suggestion,
    SuggestedFix Fix);

/// <summary>Rejected: at least one Error. NeedsReview: Warnings only. Clean: nothing found.</summary>
public enum InvoiceOutcome
{
    Clean,
    NeedsReview,
    Rejected,
}

public sealed record InvoiceResult(
    string InvoiceNo,
    DateOnly InvoiceDate,
    string? SutVersion,
    InvoiceOutcome Outcome,
    decimal Total,
    decimal AmountAtRisk,
    IReadOnlyList<Finding> Findings)
{
    public static InvoiceResult Create(Invoice invoice, string? sutVersion, List<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(findings);

        // Stable order: by line (invoice-level first), then rule id; findings of one rule on one line keep the order
        // the rule emitted them in. List.Sort is unstable, so the original index breaks ties.
        var indexed = new (Finding Finding, int Index)[findings.Count];
        for (var i = 0; i < findings.Count; i++)
        {
            indexed[i] = (findings[i], i);
        }

        Array.Sort(indexed, static (a, b) =>
        {
            var byLine = (a.Finding.LineNo ?? 0).CompareTo(b.Finding.LineNo ?? 0);
            if (byLine != 0)
            {
                return byLine;
            }

            var byRule = string.CompareOrdinal(a.Finding.RuleId, b.Finding.RuleId);
            return byRule != 0 ? byRule : a.Index.CompareTo(b.Index);
        });

        var ordered = new Finding[indexed.Length];
        for (var i = 0; i < indexed.Length; i++)
        {
            ordered[i] = indexed[i].Finding;
        }

        return new InvoiceResult(invoice.InvoiceNo, invoice.InvoiceDate, sutVersion, OutcomeOf(ordered), invoice.Total,
            AtRisk(invoice, ordered), ordered);
    }

    private static InvoiceOutcome OutcomeOf(Finding[] findings)
    {
        var outcome = InvoiceOutcome.Clean;
        foreach (var f in findings)
        {
            if (f.Severity == Severity.Error)
            {
                return InvoiceOutcome.Rejected;
            }

            if (f.Severity == Severity.Warning)
            {
                outcome = InvoiceOutcome.NeedsReview;
            }
        }

        return outcome;
    }

    /// <summary>
    /// Money SGK would not pay as submitted: the amount of every line with an Error, or the whole invoice when an
    /// Error is invoice-level (a bad provision blocks every line).
    /// </summary>
    private static decimal AtRisk(Invoice invoice, Finding[] findings)
    {
        HashSet<int>? lines = null;
        foreach (var f in findings)
        {
            if (f.Severity != Severity.Error)
            {
                continue;
            }

            if (f.LineNo is not { } lineNo)
            {
                return invoice.Total;
            }

            (lines ??= []).Add(lineNo);
        }

        if (lines is null)
        {
            return 0m;
        }

        var sum = 0m;
        foreach (var line in invoice.Lines)
        {
            if (lines.Contains(line.LineNo))
            {
                sum += line.Amount;
            }
        }

        return sum;
    }
}
