using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Reporting;

// Measurement harness. Usage (from the repository root):
//   dotnet run -c Release --project benchmarks/SutRules.Benchmarks -- [runs] [--dump <file>]
// Input: the 2,000-invoice parity fixture (medula-precheck v1.0.0 generator, seed 22), repeated 10 times with
// renumbered invoices = 20,000 invoices, the batch size medula-precheck's own harness uses.
var inv = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = inv;
CultureInfo.CurrentCulture = inv;
var runs = args.Length > 0 && int.TryParse(args[0], inv, out var r) ? r : 15;
var root = FindRoot();
var rulesDir = Path.Combine(root, "rules");

var fixture = ReadGzip(Path.Combine(root, "tests", "SutRules.Tests", "Parity", "invoices.json.gz"));
var baseInvoices = JsonSerializer.Deserialize<InvoiceBatch>(fixture, BatchReport.JsonOptions)!.Invoices;
var invoices = Enumerable.Range(0, 10)
    .SelectMany(copy => baseInvoices.Select(i => i with { InvoiceNo = $"{i.InvoiceNo}-{copy}" }))
    .ToList();

if (args.SkipWhile(a => a != "--dump").Skip(1).FirstOrDefault() is { } dump)
{
    File.WriteAllText(dump, JsonSerializer.Serialize(new InvoiceBatch(invoices), BatchReport.JsonOptions));
    Console.Error.WriteLine($"wrote {invoices.Count} invoices to {dump}");
}

var lines = invoices.Sum(i => i.Lines.Count);
Console.WriteLine($"# sut-rules measurements ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)");
Console.WriteLine();
Console.WriteLine($"- OS: {RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}");
Console.WriteLine($"- .NET: {RuntimeInformation.FrameworkDescription}; logical CPUs: {Environment.ProcessorCount}; server GC: {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"- Batch: {invoices.Count:N0} invoices, {lines:N0} lines ({(double)lines / invoices.Count:F2} per invoice); {runs} measured runs after 3 warm-up runs");
Console.WriteLine();

// ---- 1. bundle compile -----------------------------------------------------------------------------------
var catalogBytes = File.ReadAllBytes(Path.Combine(rulesDir, RuleBundle.CatalogFile));
var sources = Directory.EnumerateFiles(rulesDir, "*.rule").Order(StringComparer.Ordinal)
    .Select(p => new RuleSource(Path.GetFileName(p), File.ReadAllText(p))).ToList();
var cold = Stopwatch.StartNew();
var bundle = RuleBundle.Compile(catalogBytes, sources);
cold.Stop();
var compile = Sample(50, () => RuleBundle.Compile(catalogBytes, sources));
var load = Sample(50, () => RuleBundle.Load(rulesDir));
Console.WriteLine("## Compile");
Console.WriteLine();
Console.WriteLine("| Step | Median ms | Min ms | Max ms |");
Console.WriteLine("|---|---:|---:|---:|");
Console.WriteLine($"| First compile in the process (JIT included) | {cold.Elapsed.TotalMilliseconds:F1} | - | - |");
Row("Compile 22 rules + catalog from memory (warm)", compile);
Row("Load from disk: read, hash, compile (warm)", load);
Console.WriteLine();

// ---- 2. throughput ---------------------------------------------------------------------------------------
var engine = new RuleEngine(bundle);
Console.WriteLine("## Throughput");
Console.WriteLine();
Console.WriteLine("| Path | Median inv/s | Min inv/s | Max inv/s | Median ms per batch | Allocated per invoice |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");
Throughput("Rule engine, sequential", () => engine.CheckAll(invoices), measureAllocations: true);
Throughput("Rule engine, parallel (PLINQ)", () => engine.CheckAll(invoices, parallel: true), measureAllocations: false);
Console.WriteLine();

// ---- 3. where the time goes ------------------------------------------------------------------------------
var oneBatch = baseInvoices;
// The baseline (no rule enabled) is the fixed cost per invoice: version lookup, context, result and ordering.
var none = new HashSet<string>(StringComparer.Ordinal);
var baseline = Median(Sample(9, () =>
{
    foreach (var i in oneBatch)
    {
        engine.Check(i, none);
    }
}));
Console.WriteLine("## Cost per rule (2,000 invoices, one rule enabled at a time, median of 9, minus the no-rule baseline)");
Console.WriteLine();
Console.WriteLine($"No-rule baseline: {baseline:F2} ms per 2,000 invoices.");
Console.WriteLine();
Console.WriteLine("| Rule | Net median ms | Share of the sum |");
Console.WriteLine("|---|---:|---:|");
var perRule = bundle.Rules.Select(rule =>
{
    var only = new HashSet<string>(StringComparer.Ordinal) { rule.Id };
    var samples = Sample(9, () =>
    {
        foreach (var i in oneBatch)
        {
            engine.Check(i, only);
        }
    });
    return (rule.Id, Median: Math.Max(0, Median(samples) - baseline));
}).OrderByDescending(x => x.Median).ToList();
var total = perRule.Sum(x => x.Median);
foreach (var (id, median) in perRule)
{
    Console.WriteLine($"| `{id}` | {median:F2} | {median / total:P1} |");
}

Console.WriteLine();

// ---- 4. golden suite -------------------------------------------------------------------------------------
var golden = Sample(15, () => GoldenRunner.Run(bundle, rulesDir, out _));
var cases = GoldenRunner.Run(bundle, rulesDir, out _).Count;
Console.WriteLine("## Golden suite");
Console.WriteLine();
Console.WriteLine($"- {cases} cases: median {Median(golden):F1} ms (min {golden.Min():F1}, max {golden.Max():F1}), including reading the case files");
return;

void Throughput(string name, Func<IReadOnlyList<InvoiceResult>> run, bool measureAllocations)
{
    for (var w = 0; w < 3; w++)
    {
        run();
    }

    var ms = new List<double>();
    long allocated = 0;
    for (var i = 0; i < runs; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        run();
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ms.Add(watch.Elapsed.TotalMilliseconds);
    }

    var rates = ms.Select(x => invoices.Count / (x / 1000)).ToList();
    var alloc = measureAllocations ? $"{allocated / invoices.Count:N0} B" : "n/a (other threads)";
    Console.WriteLine($"| {name} | {Median(rates):N0} | {rates.Min():N0} | {rates.Max():N0} | {Median(ms):F1} | {alloc} |");
}

static List<double> Sample(int count, Action action)
{
    action();
    var samples = new List<double>(count);
    for (var i = 0; i < count; i++)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        samples.Add(watch.Elapsed.TotalMilliseconds);
    }

    return samples;
}

static void Row(string name, List<double> samples) =>
    Console.WriteLine($"| {name} | {Median(samples):F2} | {samples.Min():F2} | {samples.Max():F2} |");

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
}

static string ReadGzip(string path)
{
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip);
    return reader.ReadToEnd();
}

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SutRules.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
}
