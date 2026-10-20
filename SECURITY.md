# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 2.1.x | ✅ |
| < 2.1 | ❌ — upgrade to the latest 2.1.x (2.0.x lets the `All` scope delete across tenants; 1.x feeds can skip records — see the changelog) |

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
  vulnerability. The page itself escapes all dynamic values and sends a
  Content-Security-Policy plus `nosniff` / `no-store` headers; a rendering
  bypass of that escaping is in scope.

## Database roles

Row-level security is the second layer of tenant isolation (layer 1 is the explicit
`scope`/`tenant_id` predicate in every statement). `FORCE ROW LEVEL SECURITY` subjects the table
owner too, but three kinds of role are not bound by it:

- **superusers** and roles with **`BYPASSRLS`** bypass every policy;
- the **table owner** is subject to the policies, yet can `ALTER TABLE … DISABLE ROW LEVEL
  SECURITY`, drop a policy, or `TRUNCATE` (`TRUNCATE` is not subject to RLS at all).

So the application's runtime role must be **none of these**. With `EnsureSchema = true` the
connecting role creates the schema and therefore owns it, which makes the runtime role the owner
in the default setup: for production, run the schema step (`SchemaManager.EnsureSchemaAsync`, or
your own migration) under a separate migration/owner role and run the application as a plain
login role with DML grants on `papuma.*` (the way `Papuma.Kernel.Testing` sets up its
application role). Also set `idle_in_transaction_session_timeout` on that role
([architecture](docs/architecture.md#session--unit-of-work)). Isolation tests must not run as a
superuser, which passes them whether or not isolation works.

## Out of scope

- Findings that require an already-compromised database superuser.
- Denial of service by unbounded application input that the application itself
  is expected to bound.
- Missing hardening in `samples/` — samples are illustrative, not deployable.
