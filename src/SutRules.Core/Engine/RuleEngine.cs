using System.Globalization;
using SutRules.Domain;
using SutRules.Language;

namespace SutRules.Engine;

/// <summary>A rule failed at run time (e.g. arithmetic overflow); names the rule and invoice so it can be fixed.</summary>
public sealed class RuleRuntimeException : Exception
{
    public RuleRuntimeException(string ruleId, string invoiceNo, Exception inner)
        : base($"Rule {ruleId} failed on invoice {invoiceNo}: {inner?.Message}", inner)
    {
        RuleId = ruleId;
        InvoiceNo = invoiceNo;
    }

    public RuleRuntimeException()
    {
        RuleId = InvoiceNo = string.Empty;
    }

    public RuleRuntimeException(string message)
        : base(message)
    {
        RuleId = InvoiceNo = string.Empty;
    }

    public RuleRuntimeException(string message, Exception inner)
        : base(message, inner)
    {
        RuleId = InvoiceNo = string.Empty;
    }

    public string RuleId { get; }

    public string InvoiceNo { get; }
}

/// <summary>
/// Applies a <see cref="RuleBundle"/> to invoices: resolves the SUT version valid on the invoice date, asks each rule
/// for its state in that version, and runs the compiled checks. Stateless and thread-safe.
/// </summary>
public sealed class RuleEngine
{
    public const string NoVersionRuleId = "SYS-001";

    public RuleEngine(RuleBundle bundle) => Bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

    public RuleBundle Bundle { get; }

    /// <summary>Checks one invoice; <paramref name="only"/> restricts the run to some rule ids (golden tests).</summary>
    public InvoiceResult Check(Invoice invoice, IReadOnlySet<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var version = Bundle.Catalog.ResolveFor(invoice.InvoiceDate);
        if (version is null)
        {
            return NoVersion(invoice);
        }

        var context = new EvalContext(invoice, version);
        var findings = new List<Finding>();
        foreach (var rule in Bundle.Rules)
        {
            if (only is not null && !only.Contains(rule.Id))
            {
                continue;
            }

            var state = rule.StateFor(version);
            if (state is null)
            {
                continue;
            }

            try
            {
                Run(rule, state, context, findings);
            }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException or ArgumentException or InvalidOperationException
                                         or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                throw new RuleRuntimeException(rule.Id, invoice.InvoiceNo, ex);
            }
        }

        return InvoiceResult.Create(invoice, version.Id, findings);
    }

    public IReadOnlyList<InvoiceResult> CheckAll(IReadOnlyList<Invoice> invoices, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        if (parallel)
        {
            // Invoices are independent; order is preserved for reproducible reports.
            return invoices.AsParallel().AsOrdered().Select(i => Check(i)).ToList();
        }

        var results = new InvoiceResult[invoices.Count];
        for (var i = 0; i < invoices.Count; i++)
        {
            results[i] = Check(invoices[i]);
        }

        return results;
    }

    private static void Run(CompiledRule rule, RuleVersionState state, EvalContext context, List<Finding> findings)
    {
        foreach (var check in rule.Checks)
        {
            var frame = new Frame(rule.SlotCount, state.Parameters, context);
            if (check.Source is null)
            {
                RunStatements(rule, state, check, frame, null, findings);
                continue;
            }

            if (check.Source(frame) is not IReadOnlyList<object?> items)
            {
                continue;
            }

            foreach (var item in items)
            {
                frame.Slots[check.Slot] = item;
                RunStatements(rule, state, check, frame, check.AttachItem ? item as ServiceLine : null, findings);
            }
        }
    }

    private static void RunStatements(CompiledRule rule, RuleVersionState state, CompiledCheck check, Frame frame, ServiceLine? item, List<Finding> findings)
    {
        foreach (var s in check.Statements)
        {
            switch (s.Kind)
            {
                case StatementKind.Let:
                    frame.Slots[s.Slot] = s.Condition(frame);
                    break;
                case StatementKind.Require:
                    if (!Values.IsTrue(s.Condition(frame)))
                    {
                        return;
                    }

                    break;
                case StatementKind.When:
                    if (!Values.IsTrue(s.Condition(frame)))
                    {
                        break;
                    }

                    var line = s.At is null ? item : s.At(frame) as ServiceLine;
                    findings.Add(rule.CreateFinding(
                        state.Severity,
                        line?.LineNo,
                        line?.SutCode,
                        Text(s.Expected, frame),
                        Text(s.Actual, frame)));
                    return; // first firing 'when' ends the item, like an if / else-if chain
            }
        }
    }

    private static string? Text(Eval? eval, Frame frame) => eval?.Invoke(frame) is { } value ? Values.Display(value) : null;

    /// <summary>
    /// An invoice dated before every SUT version cannot be checked at all, so it is rejected with an engine finding
    /// rather than passed as clean.
    /// </summary>
    public InvoiceResult NoVersion(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var earliest = Bundle.Catalog.Versions[0].EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var actual = invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var finding = new Finding(
            NoVersionRuleId,
            RuleCategory.Administrative,
            Severity.Error,
            null,
            null,
            earliest,
            actual,
            new LocalizedText(
                $"Fatura tarihi ({actual}) için yürürlükte olan bir SUT sürümü bulunamadı; hiçbir kural uygulanamadı.",
                $"No SUT version is in force on the invoice date ({actual}); no rule could be applied."),
            new LocalizedText(
                $"Fatura tarihini kontrol edin; en erken desteklenen sürüm {earliest} tarihinde başlar.",
                $"Check the invoice date; the earliest supported version starts on {earliest}."),
            new SuggestedFix(FixAction.ChangeInvoiceDate, null, null));
        return InvoiceResult.Create(invoice, null, [finding]);
    }
}
