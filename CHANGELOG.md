# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-12

### Added

- Rule language: one rule per file with category, bilingual title, reason and fix, typed parameters, version blocks
  that name SUT versions (inherit, override severity and parameters, `retired`), and `check invoice` /
  `check each x in list` blocks with `let`, `require` and `when ... report`.
- Hand-written lexer and recursive-descent parser with stable diagnostic codes and positions; canonical printer.
- Single-pass type checker and closure compiler against a schema of the invoice model and SUT reference data:
  comprehensions (`count`, `any`, `all`, `sum`, `min`, `max`, `first`), 15 built-in functions, SQL/C#-like null
  semantics, date arithmetic, bare enum members, linear-time regular expressions.
- Effective-dated SUT catalog (three synthetic versions as deltas) and all-or-nothing rule bundles with a SHA-256
  over rules and catalog; the SUT version valid on the invoice date selects reference data and rule state;
  invoices dated before every version are rejected (`SYS-001`).
- 22 rules ported from medula-precheck v1.0.0 as rule files, with 108 golden cases (RFC 7386 merge patches over a
  base invoice) and a coverage contract: every rule and every version block needs a firing case.
- Parity test: 2,000 invoice results identical, as JSON text, to medula-precheck's hand-written engine.
- `sut-rules` CLI: `check` (JSON or Markdown report, exit code 2 when an invoice would be rejected), `lint`, `test`,
  `explain`.
- HTTP API: `POST /api/v1/check`, `GET /api/v1/rules`, `GET /api/v1/rules/{id}`, `GET /api/v1/ruleset`; hot reload by
  polling with atomic swap, last good bundle kept on a broken edit, readiness Degraded; bundle hash in every report
  and in `X-Rule-Bundle`; OpenAPI with Scalar, Serilog JSON logs, OpenTelemetry traces and metrics.
- Chiseled non-root container image and a `docker compose up` demo with the rule directory mounted read-only.
- Tests: diagnostics approval files, language semantics, versioning, FsCheck properties (printer round trip,
  engine robustness on hostile invoices, outcome invariants, version resolution), API and end-to-end hot reload.
- Measurement harness and results: compile time, throughput against medula-precheck on the same 20,000 invoices,
  allocation per invoice, per-rule cost.
- ADRs 0001-0005, language reference, threat model, benchmarks.

### Performance

- Aggregations use static indexed loops instead of per-evaluation closures and enumerators: allocation per invoice
  fell from 6,334 to 4,367 bytes.

[1.0.0]: https://github.com/hekimcan206/sut-rules/releases/tag/v1.0.0
