using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Language;
using SutRules.Reporting;

// Exit codes: 0 = success / nothing rejected, 1 = usage, input or rule errors, 2 = at least one invoice would be
// rejected (so `sut-rules check` works as a gate in a billing pipeline), 3 = golden tests or coverage failed.
Console.OutputEncoding = Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;

try
{
    var options = Options.Parse(args.Skip(1).ToArray());
    return args.FirstOrDefault() switch
    {
        "check" => Check(options),
        "lint" => Lint(options),
        "test" => Test(options),
        "explain" => Explain(options),
        _ => Usage(),
    };
}
catch (RuleCompilationException ex)
{
    foreach (var d in ex.Diagnostics)
    {
        Console.Error.WriteLine(d);
    }

    Console.Error.WriteLine($"{ex.Diagnostics.Count} error(s); the rule bundle was not loaded.");
    return 1;
}
catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException or RuleRuntimeException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Check(Options o)
{
    var bundle = RuleBundle.Load(o.Rules);
    var input = o.Get("input") ?? throw new ArgumentException("--input <invoices.json> is required.");
    using var stream = File.OpenRead(input);
    var batch = JsonSerializer.Deserialize<InvoiceBatch>(stream, BatchReport.JsonOptions) ?? throw new FormatException("Empty input.");

    var engine = new RuleEngine(bundle);
    var watch = Stopwatch.StartNew();
    var results = engine.CheckAll(batch.Invoices, parallel: o.Has("parallel"));
    watch.Stop();

    var report = BatchReport.Create(bundle, results, DateTimeOffset.UtcNow);
    var rendered = (o.Get("format") ?? "json") switch
    {
        "json" => report.ToJson(),
        "markdown" => report.ToMarkdown(),
        var other => throw new ArgumentException($"Unknown format '{other}'; use json or markdown."),
    };

    if (o.Get("out") is { } outFile)
    {
        File.WriteAllText(outFile, rendered);
    }
    else
    {
        Console.Out.Write(rendered);
    }

    var s = report.Summary;
    Console.Error.WriteLine(string.Create(inv,
        $"checked {s.Invoices} invoices in {watch.Elapsed.TotalMilliseconds:F1} ms with bundle {bundle.Hash[..12]}: {s.Clean} clean, {s.NeedsReview} need review, {s.Rejected} would be rejected; {s.AmountAtRisk:N2} of {s.TotalAmount:N2} TRY at risk"));
    foreach (var rule in report.ByRule.Take(5))
    {
        Console.Error.WriteLine(string.Create(inv, $"  {rule.RuleId,-8} {rule.Count,5}  {rule.TitleEn}"));
    }

    return s.Rejected > 0 ? 2 : 0;
}

int Lint(Options o)
{
    var watch = Stopwatch.StartNew();
    var bundle = RuleBundle.Load(o.Rules);
    watch.Stop();
    Console.WriteLine(string.Create(inv,
        $"{bundle.Rules.Count} rules compiled in {watch.Elapsed.TotalMilliseconds:F1} ms against {bundle.Catalog.Versions.Count} SUT versions; bundle {bundle.Hash}"));
    return 0;
}

int Test(Options o)
{
    var bundle = RuleBundle.Load(o.Rules);
    var results = GoldenRunner.Run(bundle, o.Rules, out var coverage);
    foreach (var group in results.GroupBy(r => r.Rule))
    {
        var failed = group.Where(r => !r.Passed).ToList();
        Console.WriteLine(string.Create(inv, $"{(failed.Count == 0 ? "ok  " : "FAIL")} {group.Key,-8} {group.Count() - failed.Count}/{group.Count()} cases"));
        foreach (var f in failed)
        {
            Console.WriteLine($"     - {f.Case}: {f.Failure}");
        }
    }

    foreach (var problem in coverage)
    {
        Console.WriteLine($"COVERAGE {problem}");
    }

    var passed = results.Count(r => r.Passed);
    Console.WriteLine(string.Create(inv, $"{passed}/{results.Count} golden cases passed, {coverage.Count} coverage problem(s)"));
    return passed == results.Count && coverage.Count == 0 ? 0 : 3;
}

int Explain(Options o)
{
    var bundle = RuleBundle.Load(o.Rules);
    var date = o.Get("date") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", inv) : DateOnly.FromDateTime(DateTime.UtcNow);
    var version = bundle.Catalog.ResolveFor(date);
    if (version is null)
    {
        Console.WriteLine($"No SUT version is in force on {date:yyyy-MM-dd}; invoices dated then are rejected with {RuleEngine.NoVersionRuleId}.");
        return 0;
    }

    Console.WriteLine($"{date:yyyy-MM-dd} -> {version.Id} (in force from {version.EffectiveFrom:yyyy-MM-dd}): {version.DescriptionEn}");
    foreach (var rule in bundle.Rules)
    {
        var state = rule.StateFor(version);
        var parameters = state is null || state.ParametersByName.Count == 0
            ? string.Empty
            : " " + string.Join(", ", state.ParametersByName.Select(p => $"{p.Key}={Values.Display(p.Value)}"));
        Console.WriteLine(string.Create(inv,
            $"  {rule.Id,-8} {(state is null ? "inactive" : state.Severity.ToString()),-8} {(state is null ? string.Empty : "since " + state.Block),-18} {rule.Title.En}{parameters}"));
    }

    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        sut-rules - versioned SUT rule files, evaluated with the SUT version valid on the invoice date
        (synthetic, illustrative rules; not the official SUT)

          sut-rules check   --input <invoices.json> [--rules rules] [--format json|markdown] [--out <file>] [--parallel]
          sut-rules lint    [--rules rules]                 compile every rule; print all diagnostics
          sut-rules test    [--rules rules]                 run the golden cases and the coverage contract
          sut-rules explain [--rules rules] [--date yyyy-MM-dd]   which rules apply on a date, with severity and parameters

        Exit codes: 0 ok, 1 usage/input/rule error, 2 an invoice would be rejected, 3 golden failure.
        """);
    return 1;
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public string Rules => Get("rules") ?? "rules";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            o._values[key] = hasValue ? args[++i] : null;
        }

        return o;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public bool Has(string key) => _values.ContainsKey(key);
}
