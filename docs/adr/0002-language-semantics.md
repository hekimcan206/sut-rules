# 2. A small, total, statically typed language with SQL-like nulls

- Status: accepted
- Date: 2026-11-12

## Context

The language has to express all 22 medula-precheck rules: per-line checks against versioned reference data,
same-day aggregation, rolling frequency windows over invoice and history, package absorption, and invoice-level
checks with a chosen line. Its users are billing analysts and developers reviewing amendments, not language
enthusiasts. Two properties matter more than expressiveness:

- a rule must not be able to hang or crash the engine on a hostile invoice;
- a typo must fail when the bundle loads, not when the first matching invoice arrives.

## Options considered

1. **Embed a general language** (C# scripting, JavaScript, Lua). Maximum power; rules can loop forever, allocate
   without bound, call I/O, and type errors appear at run time.
2. **DMN decision tables with FEEL.** A standard; but tables fit "if these columns then this output", not "sum the
   quantities of the other lines with the same code on the same day". FEEL's null also doubles as "error", which the
   DMN task force itself has been revising (B-FEEL), and min/max/sum disagree on nulls.
3. **A purpose-built expression language**: no user functions, no recursion, no loops except comprehensions over
   finite lists from the invoice or the catalog; checked against a schema before it runs.

## Decision

Option 3, with these semantics (full reference in `docs/language.md`):

- **Static types against a schema** (`Schema.cs`): every root name, record field and function is known at compile
  time, so `line.cod` and `procedur(...)` are compile errors with a suggestion. Enum members are written bare and
  resolved from the other operand's type.
- **Every type admits null.** Reference data is sparse (most procedures have no age limit, frequency or branch list).
  Member access on null yields null, arithmetic propagates null, relational comparisons with a null side are false,
  and conditions are two-valued: only `true` passes. This makes `years < p.min_age or years > p.max_age` mean what a
  reader expects without guards, and matches C#'s lifted operators, which is why the port reproduces
  medula-precheck exactly. Unlike FEEL, null never means "error": run-time errors (integer overflow) raise an
  exception that names the rule and the invoice.
- `least` / `greatest` ignore nulls (PostgreSQL semantics), so "latest of last service and discharge" is one call.
  `min` / `max` over nothing are null; `sum` over nothing is 0.
- **Total by construction.** Comprehensions iterate finite lists; there is no recursion or unbounded loop; regular
  expressions run on .NET's non-backtracking engine (linear time) with a timeout as a second fence, and constructs
  that engine cannot run are compile errors (`SR106`). Evaluation cost is polynomial in invoice size, quadratic at
  worst (a line looking at all other lines).
- **First firing `when` ends the item**, like an if / else-if chain. Rules that report several independent things
  use several `check` blocks (MDL-007).
- **No shadowing.** A name means one thing in a rule; reusing one is an error (`SR108`).

## Consequences

- The 22 rule files are 19–29 lines each, and the language has 15 built-in functions and 7 comprehension forms.
- Some C# idioms are not expressible (dictionaries, early `break` across items, user-defined helpers). Rules repeat a
  few sub-expressions (for example `procedure(line.code)`), which `let` keeps readable.
- Two-valued conditions mean `not (x > 5)` is true when `x` is null. That is the C# behaviour the rules were ported
  from, and the golden cases pin it, but it differs from SQL's three-valued logic. A rule that must treat "unknown"
  specially says so with `x != null`.

## Sources

- DMN B-FEEL proposal (null only for missing data, comparisons with null are false): https://www.trisotech.com/make-way-for-b-feel/
- OMG DMN 1.8 issue on inconsistent null handling of sum/mean vs min/max: https://issues.omg.org/issues/DMN18-63
- PostgreSQL conditional expressions, GREATEST and LEAST ignore NULLs: https://www.postgresql.org/docs/17/functions-conditional.html
- .NET regular expressions: non-backtracking mode and match timeouts as ReDoS defences:
  https://devblogs.microsoft.com/dotnet/regular-expression-improvements-in-dotnet-7,
  https://www.meziantou.net/regex-deny-of-service-redos.htm
