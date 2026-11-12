# 3. Compile rules to closures, not to IL and not to a tree-walking interpreter

- Status: accepted
- Date: 2026-11-12

## Context

medula-precheck's hand-written engine checks about 200,000 synthetic invoices per second on one core of the
development laptop. A billing period of a large hospital is tens of thousands of invoices, so even a tenth of that
speed is fast enough for batch use. What matters more is load time (the API reloads rules while serving) and that
the evaluator is simple enough to trust.

## Options considered

1. **Tree-walking interpreter**: evaluate the syntax tree with a `switch` per node and look names up by string at
   run time. Simplest; slowest; name lookups and type dispatch repeat for every line of every invoice.
2. **Closure compilation**: the type checker turns each node into a delegate with names already resolved to slot
   indexes, field getters and operators specialised by static type (for example `int < int` and `date < date` get
   allocation-free fast paths).
3. **System.Linq.Expressions or Reflection.Emit to IL**: fastest steady state, but compilation costs milliseconds
   per rule through the expression compiler, nullable lifting and decimal/date operators need careful mapping, and
   failures surface as JIT exceptions rather than diagnostics.

## Decision

Option 2. Type checking and compilation are one pass (`ExpressionCompiler`), so every compiled expression has
already been type checked. Values are boxed CLR objects; booleans and small integers come from caches.

## Consequences

Measured on the 20,000-invoice batch (docs/benchmarks.md, same machine, same file, interleaved runs):

| | Median invoices/s (sequential) | Allocated per invoice |
|---|---:|---:|
| medula-precheck, hand-written C# | 209,327 | 4,231 B |
| sut-rules, compiled closures | 115,969 | 4,367 B |

- The rule language costs about **45% of the hand-written engine's throughput** and still checks over 100,000
  invoices per second on one core and over 300,000 in parallel on a quiet machine.
- Compiling all 22 rules and the catalog takes about 2–3 ms warm (around 100–120 ms for the first compile in a
  process, which includes JIT). A hot reload is therefore cheap enough to run on every file change.
- A profiling pass found that comprehension loops allocated a closure and a boxed enumerator per evaluation;
  replacing them with static indexed loops cut allocation from 6,334 to 4,367 bytes per invoice. The remaining gap
  is boxing (dates and decimals read from fields) and delegate calls per node. If throughput ever matters more than
  simplicity, typed delegates (`Func<Frame, int>` and so on) or an expression-tree backend can be added behind the
  same type checker; the parity and golden suites would gate it.
