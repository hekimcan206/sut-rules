# 5. Hot reload by polling and atomic swap, last good bundle keeps serving

- Status: accepted
- Date: 2026-11-12

## Context

The point of rules in files is that an amendment ships as a file change. The API should pick it up without a
restart, and a mistake in that file must never reach invoices.

## Options considered

1. **Restart to reload.** Simple and safe, but a deploy per amendment is what files were meant to avoid.
2. **FileSystemWatcher.** Immediate, but change notifications do not cross Docker Desktop bind mounts reliably, and
   Kubernetes ConfigMap updates arrive as symlink swaps that watchers report inconsistently.
3. **Polling a cheap fingerprint** (file names, sizes, modification times) every few seconds, then compiling the
   whole bundle and swapping one reference.

## Decision

Option 3 (`RuleBundleProvider`, default interval 2 s).

- A change triggers a full compile. Only a bundle with zero diagnostics replaces the active one; the swap is a single
  reference write, and each request takes one engine reference at its start, so a report never mixes two bundles.
- A rejected reload keeps the last good bundle serving, records the diagnostics (`GET /api/v1/ruleset`), logs them,
  counts it in `sutrules.bundle.reloads{result="rejected"}`, and turns readiness to **Degraded**, not Unhealthy: the
  instance still answers correctly with the previous rules.
- A broken bundle **at startup** stops the process: there is no previous bundle to fall back on.
- A file caught mid-write (I/O error) resets the fingerprint so the next poll retries.

## Consequences

- An edit takes effect within one poll interval plus a few milliseconds of compile time (ADR 0003). Tested
  end-to-end in `HotReloadTests` (edit, broken edit, fix) and by hand through the compose bind mount.
- Every report carries the bundle hash in the body and the `X-Rule-Bundle` header, so a report can be traced to the
  exact files even when they changed a minute later.
- Polling costs a directory listing every 2 s. With hundreds of rule files that would still be negligible.
- Reloading is per instance. Several instances converge within one interval of each other; there is no cluster-wide
  coordination, so during that window two instances may answer with different bundles (both named in their reports).
