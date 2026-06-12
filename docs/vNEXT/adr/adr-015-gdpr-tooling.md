# ADR-015: GDPR tooling — mechanisms in the kernel, legal decisions in the application

## Status

Accepted (2026-06-12) · Implemented in phase 12 (guide: [gdpr.md](../gdpr.md))

## Context

Beyond the policies (ADR-007), three data-subject-rights questions arise:
export/access (Art. 15/20), erasure (Art. 17), and the conflict with retention
obligations (Art. 17 (3) / Art. 18 — e.g. German HGB/AO periods of 6–10 years
within an ongoing business relationship), which plays out **differently per
tenant**: tenant A may truly erase, tenant B has a legitimate interest or a legal
duty to retain.

Additionally a gap exists: document deletion cleans the feed only for
**policy-protected** fields. A tracked field containing personal data (e.g.
`Name` without an attribute) remains in plain text in historical diffs after the
delete.

## Decision

The dividing line follows the projection principle (ADR-009): **the kernel
provides executing mechanisms over what only it knows (metamodel, history,
scopes); the application makes the domain-legal decisions.**

### Kernel mechanisms (phase 12)

1. **Export assembly** (Art. 15/20): given document references and event
   selectors (payload path = value, e.g. `userId = X`), the kernel produces a
   structured JSON export: current state + change history (diffs/metadata) +
   events. Policy-protected fields appear as change markers, never as value
   histories — the minimization from ADR-007 automatically applies to the export
   as well.
2. **Data inventory** (Art.-30 support): a report from the metamodel — which
   types/fields carry which policies, which event types have which retention.
3. **Erasure primitive per scope**: document hard delete (exists) **plus
   `RedactHistoryAsync(documentRef, paths?)`** — retroactively rewriting
   historical diff entries and event payload fields to redaction markers, with
   audit metadata (who/when/why). Closes the gap of tracked PII fields. This
   deliberately violates append-only purity — Art. 17 beats architectural
   aesthetics. The consequence stays consistent with ADR-008: rollback across
   redacted history fails typed.

### Application business (deliberately no framework magic)

1. **Subject → data mapping**: which documents/events belong to a person is
   domain knowledge — the application supplies the references (via its keys and
   projections).
2. **Legal-basis decision per tenant/data category**: erase vs. restrict vs.
   retain is legal configuration. The kernel executes per scope; scope isolation
   structurally guarantees that erasure in tenant A does not touch the
   retention-obligated tenant B. For the retention case, the pattern is
   **restriction instead of erasure** (Art. 18): a blocking status as a document
   field, processing restricted application-side, erasure due date noted.
3. **Deadline scheduling** ("really erase after 10 years"): an application
   workflow that calls the kernel primitive on schedule.

## Consequences

- Access and erasure requests become servable with a few lines of app code,
  without the framework baking in legal assumptions that would be wrong per
  organization.
- PII discipline remains the first line of defense: **all** personal fields
  belong under a policy — then delete is clean out of the box and
  `RedactHistoryAsync` is only the safety net for omissions and legacy data.
- `RedactHistoryAsync` is a sharp tool (irreversible): audit metadata mandatory,
  not part of normal application flows.
- The inventory makes policy gaps visible (review tool: "which fields are *not*
  protected?") — preventive against exactly the gap this ADR closes.
