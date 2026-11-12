# 1. Rules as versioned files, resolved by the SUT version valid on the invoice date

- Status: accepted
- Date: 2026-11-12

## Context

The SUT (Sağlık Uygulama Tebliği) is amended several times a year by communiqués published in the Resmi Gazete.
In 2025 alone there were amendments on 25 January, 25 March, 26 June, 10 December and 31 December, several with
provisions that take effect on different dates. An amendment changes three kinds of thing:

1. reference data: tariff prices, age limits, frequency limits, which codes exist;
2. rule parameters: a deadline goes from 60 to 45 days;
3. rule existence and severity: a check is introduced, or a warning becomes blocking.

medula-precheck (the sibling project) keeps (1) and the activation part of (3) in a JSON catalog and writes every
rule as a C# class. Changing a rule means a code change, a build and a release, and the rule's history is spread
across C# and JSON. Its README names a file-based rule language as the next step; this repository is that step.

## Options considered

1. **Rules stay in C#, versions in JSON** (status quo). Fast and type-safe, but rule authors must be C# developers,
   and a reviewer cannot read "what changed in SUT-2026.01" in one place.
2. **One rule file per SUT version** (copy the whole rule set per amendment). Easy to reason about a single version,
   but every amendment duplicates every rule, and diffs between versions are noise.
3. **One file per rule, with version blocks** that name catalog versions and override severity and parameters, in
   ascending order, inheriting from the previous block. The catalog keeps the timeline and reference data.
4. **A general rules engine** (Drools, a DMN/FEEL engine, NRules). Mature, but brings its own runtime and
   versioning model; DMN has no notion of "rule set valid on date"; and the 22 rules here would be 22 decision
   tables plus glue code.

## Decision

Option 3.

- `rules/catalog/sut-catalog.json` defines the SUT timeline (id, effective date) and the reference data of each
  version as deltas over the previous one, the way amendments are published.
- `rules/<ID>.rule` holds one rule: metadata, parameters, version blocks, checks and bilingual texts. A version
  block applies from its SUT version until the next block; `retired` takes the rule out of force.
- At load time every rule is resolved to one state per catalog version (an array indexed by version ordinal), so at
  run time "which severity and parameters apply" is an array lookup.
- The SUT version is chosen by the **invoice date**, exactly as medula-precheck does, so the two engines can be
  compared invoice by invoice (ADR 0004). An invoice dated before the first version is rejected (`SYS-001`).
- A **bundle** is all rule files plus the catalog, compiled together. It loads all-or-nothing: one broken file means
  the bundle is not loaded, because silently skipping a rule would pass invoices it should reject. Each bundle has a
  SHA-256 over file names and contents (line endings normalised), returned with every report, so the question
  "which rules judged this invoice" has an exact answer.

## Consequences

- An amendment is a reviewable diff: `ADM-001.rule` shows the rule appearing in SUT-2025.07 with 60 days and the
  limit dropping to 45 in SUT-2026.01, next to the golden cases that prove both.
- Rule authors need the language reference (`docs/language.md`), not C#. They still need review: the type checker
  catches typos and type errors, not wrong billing logic.
- Versioning by invoice date inherits medula-precheck's known simplification: real tariffs apply by service date,
  and one provision can span an amendment. Per-line versioning would change the resolution key, not the model.
- The bundle hash identifies rules, not the engine binary. Two engine versions with the same bundle could still
  differ; the parity and golden tests are what keep engine changes honest.

## Sources

- Resmi Gazete issue of 26 June 2025 (SUT amendment with provisions effective on different dates):
  https://www.resmigazete.gov.tr/eskiler/2025/06/20250626-4.htm
- Reports of the 2025 SUT amendments (25 January, 26 June, 31 December 2025): https://www.alomaliye.com/2025/01/25/sgk-saglik-uygulama-tebliginde-degisiklik-25-01-2025/amp/,
  https://www.alomaliye.com/2025/06/26/sosyal-guvenlik-kurumu-saglik-uygulama-tebliginde-degisiklik-26-06-2025/amp/,
  https://www.alomaliye.com/2025/12/31/sosyal-guvenlik-kurumu-saglik-uygulama-tebliginde-degisiklik-31-12-2025/amp/
- medula-precheck v1.0.0, ADR 0001 (effective-dated rule sets) and README limitations.
