# Benchmarks

All numbers below were measured on the machine described here, on 12 November 2026. Nothing is extrapolated. Raw
harness output is in [`benchmarks/results/`](../benchmarks/results).

## Method

- **Workload**: the 2,000-invoice parity fixture (medula-precheck v1.0.0 generator, seed 22, 40% injected defects,
  invoice dates 2025-01-01..2026-06-30, all three SUT versions) repeated 10 times with renumbered invoices:
  **20,000 invoices, 54,100 lines** (2.71 per invoice). That is the batch size medula-precheck's own harness uses.
- **Harness**: `benchmarks/SutRules.Benchmarks` (Release, workstation GC, invariant culture). Throughput is
  invoices / wall-clock time of `CheckAll`; 3 warm-up runs, then 15 timed runs, a forced GC before each; the
  tables report median, min and max. Allocation is `GC.GetAllocatedBytesForCurrentThread` around a sequential run.
- **Comparison**: medula-precheck's hand-written C# engine (v1.0.0, built from its source in Release) measured with
  the same method on the **same 20,000-invoice file** (written by `--dump`), by a 25-line throwaway harness that
  references `MedulaPrecheck.Core.dll` (reproduced at the end of this page; it is not in this repository because
  that project is separate). The two harnesses ran **interleaved**, three rounds each, to share the same host load.
- **Compile**: `RuleBundle.Compile` (22 rule files + catalog from memory) and `RuleBundle.Load` (read the directory,
  hash, compile), 50 samples after one warm-up. The first compile in a fresh process is reported separately because
  it includes JIT.

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores, 16 logical), laptop |
| RAM | 15.7 GB, about 1.4 GB free during the runs |
| OS | Windows 11 (10.0.26200), x64 |
| .NET | 10.0.12 runtime, SDK 10.0.4xx, workstation GC |
| Host load | 7–9 other containers running (another agent's build and test containers, unrelated databases); CPU load sampled before the runs 31–37%, after 30–74% |

The host was not idle. Medians over 15 runs and interleaved rounds are the defence; the min/max spread shows the
noise. Round 3 coincided with a load spike and is reported separately.

## Results

### Throughput (20,000 invoices)

| Engine | Round 1 median inv/s | Round 2 | Round 3 (load spike) | Median of rounds | Allocated per invoice |
|---|---:|---:|---:|---:|---:|
| medula-precheck, hand-written C#, sequential | 186,351 | 211,535 | 209,327 | **209,327** | 4,231 B |
| sut-rules, rule files, sequential | 117,484 | 115,969 | 61,537 | **115,969** | 4,367 B |
| sut-rules, rule files, parallel (PLINQ, 16 logical CPUs) | 387,541 | 326,847 | 91,748 | **326,847** | n/a |

Per-run min/max for the clean rounds: sut-rules sequential 104,092–137,714 inv/s; medula-precheck 95,761–246,705.

**Reading**: on one core, the rule language runs at about **55% of the hand-written engine** (115,969 vs 209,327
inv/s) with nearly the same allocation per invoice. Both are far above what a billing period needs: a 30,000-invoice
month is about a quarter of a second of single-core time with the rule files.

### Before and after the aggregation-loop fix

The first measured version allocated a closure and a boxed enumerator per comprehension evaluation. Replacing them
with static indexed loops (commit `perf: ...`):

| | Allocated per invoice | Sequential median inv/s (3 rounds) |
|---|---:|---:|
| Before | 6,334 B (6,198 in one round) | 105,438 / 98,914 / 109,828 |
| After | 4,367 B | 117,484 / 115,969 / 61,537 (spike) |

Allocation fell by 31%; the throughput change is within the run-to-run noise of this machine, so no speed-up is
claimed.

### Compile and reload

| Step | Median | Min | Max |
|---|---:|---:|---:|
| First compile in a fresh process (JIT included), 3 rounds | 101.5–122.6 ms | | |
| Compile 22 rules + catalog from memory, warm | 2.39–3.34 ms | 1.53 ms | 19.27 ms |
| Load from disk (read, SHA-256, compile), warm | 4.19–4.90 ms | 3.31 ms | 8.66 ms |
| Golden suite, 108 cases including reading the files | 5.4–6.9 ms (16.6 in the spike round) | 4.6 ms | 28.5 ms |

A hot reload in the API is one "load from disk" plus a reference swap, so an edited rule is live within the 2 s
poll interval plus a few milliseconds.

### Where the time goes

`benchmarks/results/*.md` has a per-rule table (each rule alone over 2,000 invoices, minus the no-rule baseline of
about 0.46 ms per 2,000 invoices). The ranking moves between rounds (for example ADM-001 was 7.6% in round 1);
consistently near the top are the rules that look at other lines or other records for every line: PKG-001
(packages on the invoice), CLN-001 and CLN-002 (diagnoses, branches), MDL-005 (duplicates), FRQ-002 (history and
other lines). These are O(lines²) by design, which is cheap at 2.7 lines per invoice and would need an index-style
built-in for invoices with hundreds of lines.

## What was not measured

- **The API under HTTP load.** The engine dominates per-invoice cost; an HTTP load test on this shared laptop would
  measure the host more than the service. The compose demo and the CI job check correctness, not throughput.
- **Linux numbers.** CI runners are shared and timing there is not comparable; only correctness runs in CI.
- **Large invoices.** The synthetic generator produces 1–6 lines per invoice; the quadratic rules were not timed on
  long inpatient invoices.

## Reproduce

```bash
dotnet run -c Release --project benchmarks/SutRules.Benchmarks -- 15 --dump /tmp/inv20k.json
```

The medula-precheck side, run against the dumped file (build medula-precheck's `MedulaPrecheck.Core` in Release and
reference the DLL):

```csharp
var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(args[0]), BatchReport.JsonOptions)!;
var engine = new PrecheckEngine(SutCatalog.Default);
for (var w = 0; w < 3; w++) engine.CheckAll(batch.Invoices);
var ms = new List<double>(); long alloc = 0;
for (var i = 0; i < 15; i++)
{
    GC.Collect(); GC.WaitForPendingFinalizers();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew(); engine.CheckAll(batch.Invoices); sw.Stop();
    alloc = GC.GetAllocatedBytesForCurrentThread() - before; ms.Add(sw.Elapsed.TotalMilliseconds);
}
// median of batch.Invoices.Count / seconds over the 15 runs
record Batch(IReadOnlyList<Invoice> Invoices);
```
