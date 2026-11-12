# Rule language reference

One rule per file, `rules/<ID>.rule`, UTF-8. `#` starts a comment. The SUT version timeline and reference data
(prices, limits, indications, packages) live in `rules/catalog/sut-catalog.json`; rule files say which versions a
rule applies to and what it checks.

## A rule file

```text
rule "ADM-001" {
  category administrative                       # structural pricing demographic clinical frequency package provision administrative
  title tr "Fatura geç düzenlenmiş"
        en "Invoice issued late"
  basis "Monthly invoicing deadlines"           # why the rule exists (shown in reviews and the API)

  param max_lag_days int                        # int | decimal | string | bool | date

  version "SUT-2025.07" { severity warning max_lag_days = 60 }
  version "SUT-2026.01" { max_lag_days = 45 }   # unset fields inherit from the previous block

  check invoice {                               # or: check each <name> in <list> { ... }
    require count(lines) > 0                    # skip this item unless true
    let lag = invoice.date - max(l in lines: l.date)
    when lag > max_lag_days                     # first 'when' that is true reports and ends the item
      report expected max_lag_days actual lag   # optional: at <line>, expected <expr>, actual <expr>
  }

  reason tr "Fatura, son hizmetten {actual} gün sonra düzenlenmiş; sınır {expected} gün."
         en "The invoice was issued {actual} days after the last service; the limit is {expected} days."
  fix ChangeInvoiceDate                         # optional: value expected | value actual
    tr "Dönem faturasına zamanında ekleyin; gecikme gerekçesini fatura ekinde belirtin."
    en "Include it in the period invoice on time; state the reason for the delay in the invoice annex."
}
```

### Versions

- A `version` block names a SUT version from the catalog. Blocks must be in ascending SUT order.
- Block *i* applies from its SUT version up to, not including, block *i+1*. Before the first block the rule is not
  in force. `retired` takes it out of force until a later block sets a severity again.
- The first block must set `severity` and every declared `param`.
- An invoice is checked against the SUT version valid on its **invoice date** (`EffectiveFrom <= date < next`). A
  date before the first version is rejected with `SYS-001`; it is never passed as clean.
- The catalog version also decides reference data: `procedure("900101").price` is 120.00 on a 2025 invoice and
  135.00 on a 2026 one.

### Checks and findings

- `check invoice` runs once. `check each x in <list>` runs once per element. When the list is `lines`, findings
  are attached to that line (line number and SUT code) unless `report at <line>` says otherwise.
- Statements run in order: `let` binds a name, `require` skips the item unless its condition is true, `when`
  reports a finding and ends the item when its condition is true. A rule may have several `check` blocks.
- `expected` and `actual` become text (`str()` rules). Templates may use `{line}`, `{code}`, `{expected}` and
  `{actual}`; any other placeholder is a compile error. Missing values print as `-`.

## Names in scope

| Name | Type | Meaning |
|---|---|---|
| `invoice` | invoice | `no`, `date`, `facility`, `total` |
| `patient` | patient | `ref`, `birth_date`, `sex` (Female, Male) |
| `provision` | provision | `tracking_no`, `admission`, `discharge` (null while admitted), `treatment` (Outpatient, Inpatient, DayCase), `type` (Normal, Emergency, WorkAccident, TrafficAccident, Forensic), `branch` |
| `lines` | list of line | `no`, `code`, `date`, `quantity`, `unit_price`, `amount`, `doctor` |
| `diagnoses` | list of diagnosis | `icd`, `primary` |
| `doctors` | list of doctor | `registration`, `branch` |
| `prior` | list of prior_service | `code`, `date`, `quantity`, `branch`: the patient's earlier billings |
| `sut` | sut_version | `id`, `effective_from`, `incompatible_pairs` (`primary`, `secondary`), `diagnosis_sex` (`from`, `to`, `sex`) |
| `procedure(code)` | procedure or null | `code`, `name_tr`, `name_en`, `kind` (Examination, Laboratory, Imaging, Procedure, Package, BedDay, Consumable), `price`, `sex`, `min_age`, `max_age`, `max_per_day`, `frequency` (`count`, `days`), `required_icd`, `allowed_branches`, `inpatient_only`, `package_contents` |
| `doctor(registration)` | doctor or null | the physician on the invoice with that registration |

Declared parameters are in scope by name. Names cannot be shadowed.

## Expressions

Precedence, lowest first: `if c then a else b`; `or`; `and`; `not`; comparisons `== != < <= > >= in` `not in`
(they do not chain); `??`; `+ -`; `* /`; unary `-`; member access `.`.

| Form | Meaning |
|---|---|
| `count(x in xs where c)` | elements that pass the filter (`count(xs)` counts a list) |
| `any(x in xs: c)`, `all(x in xs: c)` | quantifiers; `where` filters first |
| `sum(x in xs where c: v)` | 0 when nothing matches |
| `min(...)`, `max(...)` | null when nothing matches |
| `first(x in xs where c)` | the first matching element, or `: v` to project it |
| `[a, b]` | list literal |
| `date("2026-01-01")` | date literal |

Built-in functions: `str`, `money` (two decimals), `matches(s, "regex")`, `starts_with`, `len`, `substr(s, start,
length)`, `join(list, sep)`, `count(list)`, `age(birth, on)`, `least(...)`, `greatest(...)`, `between(x, lo, hi)`,
`procedure(code)`, `doctor(registration)`.

### Semantics worth knowing

- **Null.** Every type admits null. Member access on null is null (`procedure("x").price` is null for an unknown
  code). Arithmetic with null is null. `==` and `!=` treat null as a value. `<`, `<=`, `>`, `>=` with a null side are
  **false**. Conditions are two-valued: only `true` passes; `not null` is `true`.
- **Numbers.** `int` widens to `decimal`. Decimals keep their scale (`135.00` prints as `135.00`). `/` always gives
  a decimal; dividing by zero gives null. Integer overflow is a run-time error naming the rule and invoice.
- **Dates.** `date + n` and `date - n` add days; `date - date` is the number of days between them.
- **Strings.** Comparison is ordinal (`"O24" <= "O9A"`). Regular expressions run on .NET's non-backtracking engine
  (linear time), so look-arounds and back-references are rejected at compile time.
- **Enums.** Write members bare (`provision.treatment == Inpatient`); the other operand decides which enum.
- **least / greatest** ignore nulls and return null only when every argument is null, as in PostgreSQL.

## Diagnostics

Every error has a stable code and a position. All errors in all files are reported together, and a bundle with any
error is not loaded. `tests/SutRules.Tests/Language/broken/` has an approved example of each family.

| Codes | Family |
|---|---|
| SR001-SR010 | lexer and parser (bad character, unterminated string, syntax) |
| SR100-SR110 | names and types (unknown name or field, operator types, function arity, condition not bool, bad regex, comprehension misuse, shadowing, `report at` not a line, ambiguous enum member) |
| SR200-SR214 | rule structure and versions (missing items, unknown category, severity or fix action, unknown or out-of-order SUT version, missing parameter values, bad placeholders, file name mismatch, duplicate rule id) |
| SR220 | catalog missing or invalid |
