using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SutRules.Api.Infrastructure;

/// <summary>Domain metrics: what was checked, what fired, and whether rule reloads succeed.</summary>
public sealed class RulesMetrics : IDisposable
{
    public const string MeterName = "SutRules";

    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Meter _meter;
    private readonly Counter<long> _invoices;
    private readonly Counter<long> _findings;
    private readonly Counter<long> _reloads;
    private readonly Histogram<double> _batchDuration;

    public RulesMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _meter = meters.Create(MeterName);
        _invoices = _meter.CreateCounter<long>("sutrules.invoices.checked", "{invoice}", "Invoices checked, by outcome.");
        _findings = _meter.CreateCounter<long>("sutrules.findings", "{finding}", "Findings, by rule and severity.");
        _reloads = _meter.CreateCounter<long>("sutrules.bundle.reloads", "{reload}", "Rule bundle loads, by result.");
        _batchDuration = _meter.CreateHistogram<double>("sutrules.check.duration", "ms", "Engine time per check request.");
    }

    public void Checked(IReadOnlyList<Engine.InvoiceResult> results, double milliseconds)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var r in results)
        {
            _invoices.Add(1, new KeyValuePair<string, object?>("outcome", r.Outcome.ToString()));
            foreach (var f in r.Findings)
            {
                _findings.Add(1, new KeyValuePair<string, object?>("rule", f.RuleId), new KeyValuePair<string, object?>("severity", f.Severity.ToString()));
            }
        }

        _batchDuration.Record(milliseconds);
    }

    public void Reloaded(bool success) => _reloads.Add(1, new KeyValuePair<string, object?>("result", success ? "loaded" : "rejected"));

    public void Dispose() => _meter.Dispose();
}
