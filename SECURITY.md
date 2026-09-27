# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 2.0.x | ✅ |
| < 2.0 | ❌ — upgrade to the latest 2.0.x (1.x feeds can skip records, see the 2.0.0 changelog) |

Papuma Kernel has not yet carried production traffic (see
[Maturity](README.md#maturity-stated-plainly)). Fixes land on the current minor
version; there are no backports.

## Reporting a vulnerability

**Please do not open a public issue.**

Use GitHub's private vulnerability reporting on this repository
(*Security* → *Report a vulnerability*), which opens a private advisory visible
only to the maintainer.

What helps:

- the affected package and version,
- what an attacker gains (read across tenants, bypass a privacy policy, …),
- a minimal reproduction — ideally a failing test against the real engine.

You can expect an acknowledgement within a few days, an assessment with a
severity and a planned fix window after triage, and credit in the advisory and
the changelog unless you prefer otherwise. Please give a fix a reasonable chance
to ship before disclosing publicly.

## Security-relevant surface

These are the parts where a bug is a vulnerability rather than a defect:

- **Scope and tenant isolation** — `ScopeContext`, the explicit `scope`/`tenant_id`
  predicates, and the PostgreSQL row-level security policies behind them
  ([ADR-001](docs/adr/adr-001-postgresql-18-only.md), [architecture](docs/architecture.md)).
- **Privacy policies** — the five-tier catalog applied at write time, before
  anything reaches the immutable change feed ([ADR-007](docs/adr/adr-007-privacy-policies.md)).
- **GDPR tooling** — history redaction, masked reads, export assembly
  ([gdpr.md](docs/gdpr.md), [ADR-015](docs/adr/adr-015-gdpr-tooling.md)).
- **Schema and identifier handling** — the DDL identifier validator on the
  startup schema path.
- **The ASP.NET Core dashboard** — it is unauthenticated unless you put
  authorization in front of it; mapping it without authorization metadata emits a
  startup warning. Exposing it publicly is a deployment mistake, not a kernel
  vulnerability.

## Out of scope

- Findings that require an already-compromised database superuser.
- Denial of service by unbounded application input that the application itself
  is expected to bound.
- Missing hardening in `samples/` — samples are illustrative, not deployable.
