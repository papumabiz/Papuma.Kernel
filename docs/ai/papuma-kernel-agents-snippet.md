# Papuma.Kernel — AGENTS.md Snippet for Dependent Repositories

This file contains a ready-to-use snippet for the `AGENTS.md` file in any application repository that is built on top of Papuma.Kernel.

---

## How to Use This Snippet

Copy the content of the **"Snippet"** section below into the `AGENTS.md` of your application repository.

### Where to place it in AGENTS.md

**Place it near the top, directly after the "Purpose" section** — before any project-specific coding rules. The reason: AI agents read `AGENTS.md` sequentially and use the first relevant context they encounter to frame all subsequent decisions. The framework context must be established before the agent reads rules about naming, testing, or structure.

Recommended structure for your application's `AGENTS.md`:

```
# AGENTS.md

## Purpose
(your project description)

## Framework                          ← INSERT SNIPPET HERE
(Papuma.Kernel context)

## Core Rules
(your project-specific rules)

## Code Quality
...
```

**Do not place it at the bottom.** Rules at the end of `AGENTS.md` are less reliably applied when the agent has already formed a mental model from earlier sections.

---

## Snippet

Copy everything between the `---` markers into your `AGENTS.md`:

---

## Framework

This application is built on **Papuma.Kernel** (.NET 10 + PostgreSQL).

Before implementing any feature, read the following documents in `docs/`:

| Document | Purpose |
|----------|---------|
| `papuma-kernel-ai-framework-context.md` | Architecture overview, design philosophy, AI decision rules |
| `papuma-kernel-ai-best-practice.md` | Patterns, anti-patterns, payload design, GDPR, testing |
| `papuma-kernel-api-reference.md` | Complete type and method signatures with all parameters |

### Core Architecture Rules

- Use a **CRUD-truth + change-feed** architecture. Domain tables are the source of truth.
- Every state mutation must call `ChangeWriter.AppendChangeAsync()` **in the same transaction** as the domain write.
- Projections and outbox workers are **asynchronous at-least-once workers** — always implement idempotency.
- Use explicit `ScopeContext` (`Platform` or `Tenant`) on every write. Never omit it.
- Respect PostgreSQL RLS session variables (`app.current_scope`, `app.current_tenant`).

### Payload Design Rules

- Change payload (`AppendChangeAsync`, kind=Change): **complete** — everything projections need to reconstruct state on replay.
- Event payload (`AppendEventAsync`, kind=Event): **selective** — only what the signal consumer needs; lean payloads for high-frequency events.
- Outbox payload: **contract-driven** — designed for the external consumer's API, not copied from internal payloads.
- Different external consumers get separate, explicitly built outbox messages.

### GDPR Rules

- Never embed high-risk PII (email, phone, address, medical data) directly in event payloads.
- Use `SensitiveRef` indirection: store PII in `ISensitiveDataStore`, put only the reference in event payloads.
- Call `ISensitiveDataStore.AppendAsync()` **before** opening the main transaction (it runs outside the transaction boundary).
- Increment `schemaVersion` when the sensitive payload shape changes.
- For outbox: pass `sensitiveRef` to internal consumers; resolve PII only for external consumers with documented GDPR basis.
- Redaction always uses `GdprProcessor.RedactEntityAsync` with a documented `reason` and `actorId`.

### What NOT to Do

- Do not model the application as pure event sourcing — domain tables remain the truth.
- Do not write signal-only events (login, page view) via `AppendChangeAsync` — use `AppendEventAsync` instead.
- Do not write state-change events via `AppendEventAsync` — use `AppendChangeAsync` instead.
- Do not copy Change payloads blindly into outbox or Event-kind entries.
- Do not introduce Kafka, EventStore, or heavy CQRS abstractions unless explicitly requested.
- Do not call `ISensitiveDataStore.AppendAsync()` inside a `NpgsqlTransaction` — it has no transaction parameter by design.

---

## Customization Notes

After inserting the snippet, add your project-specific details below the Framework section:

- **Bounded contexts**: list the domain areas (e.g., Orders, Invoices, Users)
- **Tenant model**: describe how tenants are identified (JWT claim, subdomain, route parameter)
- **External systems**: list which systems receive outbox messages and what their payload contracts are
- **Sensitive data**: list which entity types use `SensitiveRef` and what PII they protect
