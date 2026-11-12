# Contributing

Thanks for taking the time to improve sut-rules.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker only for the compose demo; the test suite needs no containers

## Changing or adding a rule

A rule change is a file change plus golden cases, reviewed like code.

1. Edit or add `rules/<ID>.rule` ([language reference](docs/language.md)). The file name must be the rule id.
2. For an amendment, add a `version "<SUT id>" { ... }` block instead of editing an existing one: older invoices must
   keep the behaviour they had. A new SUT version goes into `rules/catalog/sut-catalog.json` first.
3. Add cases to `rules/golden/<ID>.json`: a merge patch over `rules/golden/_base.json` and the exact findings. Every
   rule needs a firing and a clean case, and every version block a firing case; the build enforces it.
4. Check locally:

   ```bash
   dotnet run --project src/SutRules.Cli -- lint      # compiles every rule, prints every diagnostic
   dotnet run --project src/SutRules.Cli -- test      # golden cases + coverage contract
   dotnet run --project src/SutRules.Cli -- explain --date 2026-03-01
   ```

The parity test compares the shipped rules with medula-precheck v1.0.0's output. A deliberate behaviour change to
one of the 22 ported rules will fail it; say so in the pull request and regenerate the fixture from the matching
medula-precheck version rather than editing it by hand.

## Changing the engine or the language

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Keep each pull request to one concern. A language change needs a reference update (`docs/language.md`) and, if
   it changes a decision, an ADR in `docs/adr/`.
3. New diagnostics get an approved example in `tests/SutRules.Tests/Language/broken/` (run the tests with
   `SUTRULES_APPROVE=1` to write the `.expected` file, then review the diff).
4. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test
   dotnet format --verify-no-changes
   ```

Performance changes come with numbers from `benchmarks/SutRules.Benchmarks`, before and after, on the same machine.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required: `feat:`, `fix:`, `perf:`, `docs:`,
`test:`, `build:`, `ci:`, `refactor:`, `chore:`; `feat!:` or a `BREAKING CHANGE:` footer for incompatible changes to
the language or the report format.

## Data

Use synthetic data only. Never commit real invoices, patient data, provider codes or real tariff tables.
