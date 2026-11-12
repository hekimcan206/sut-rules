# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Authentication | None in the service: it must run behind the billing system's gateway. Compose binds to 127.0.0.1 only |
| Rule integrity | Bundles load all-or-nothing; a rejected reload keeps the last good bundle; every report carries the bundle SHA-256 |
| Rule files as input | Total language (no recursion or unbounded loops); regular expressions on the linear-time engine with a 100 ms timeout |
| Personal data | No name or national-id field in the model; unknown input fields ignored, never echoed; request bodies never logged; nothing stored |
| Input limits | 8 MB request body, 5,000 invoices per request (configurable) |
| Container | Chiseled image, no shell, non-root user, read-only root filesystem in compose, rule directory mounted read-only |
| Supply chain | Pinned package versions (central package management), CodeQL, `dotnet list package --vulnerable` in CI |
| Secrets | None needed and none in the repository |
